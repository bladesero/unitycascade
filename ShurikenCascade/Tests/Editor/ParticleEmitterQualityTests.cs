using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public class ParticleEmitterQualityTests
    {
        GameObject root;
        ParticleSystem body, sparks, smoke;
        ParticleDistanceLOD lod;

        [SetUp] public void SetUp()
        {
            root = new GameObject("Quality effect"); root.SetActive(false);
            body = AddEmitter(root.transform, "Body");
            sparks = AddEmitter(body.transform, "Sparks");
            smoke = AddEmitter(root.transform, "Smoke");
            lod = root.AddComponent<ParticleDistanceLOD>();
            lod.emitterQuality.Add(new ParticleEmitterQuality { emitter = sparks, disabledQualities = new List<string> { "Low" } });
            root.SetActive(true);
        }

        internal static ParticleSystem AddEmitter(Transform parent, string name)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var p = go.AddComponent<ParticleSystem>();
            var main = p.main; main.playOnAwake = false; main.startLifetime = 2; main.startSpeed = 0;
            var emission = p.emission; emission.rateOverTime = 100;
            return p;
        }

        [TearDown] public void TearDown() { Object.DestroyImmediate(root); }

        [Test] public void QualityNamesDefaultToEnabledAndDistanceCannotReenableBlockedEmitter()
        {
            Assert.IsTrue(lod.IsEmitterAllowed(body, "Low"));
            Assert.IsTrue(lod.IsEmitterAllowed(sparks, "New quality"));
            sparks.Simulate(.2f, false, true); Assert.Greater(sparks.particleCount, 0);
            lod.ApplyPreviewLOD(0, "Low");
            for (int i = 0; i < 3; i++)
            {
                lod.SetLOD(i);
                Assert.IsFalse(sparks.emission.enabled);
                Assert.IsTrue(sparks.isStopped);
                Assert.AreEqual(0, sparks.particleCount);
                Assert.IsTrue(sparks.GetComponent<ParticleSystemRenderer>().forceRenderingOff);
            }
            Assert.IsTrue(body.emission.enabled);
            Assert.IsTrue(body.gameObject.activeSelf);
            lod.ApplyPreviewLOD(1, "High");
            Assert.IsTrue(sparks.emission.enabled);
            Assert.AreEqual(50, sparks.emission.rateOverTimeMultiplier);
            Assert.IsFalse(sparks.GetComponent<ParticleSystemRenderer>().forceRenderingOff);
        }

        [Test] public void EmptyDistanceTableStillAppliesQualityAndRestoreRestoresBaseline()
        {
            var main = sparks.main; main.stopAction = ParticleSystemStopAction.Disable;
            lod.levels = Array.Empty<ParticleLODLevel>();
            lod.ApplyPreviewLOD(0, "Low");
            Assert.AreEqual(-1, lod.CurrentLOD);
            Assert.IsFalse(sparks.emission.enabled);
            Assert.AreEqual(ParticleSystemStopAction.None, sparks.main.stopAction);
            lod.RestoreOriginalSettings();
            Assert.AreEqual(ParticleSystemStopAction.Disable, sparks.main.stopAction);
            Assert.IsTrue(sparks.emission.enabled);
            Assert.IsFalse(sparks.GetComponent<ParticleSystemRenderer>().forceRenderingOff);
            Assert.IsFalse(sparks.isPlaying);
        }

        [Test] public void ParentGatingDoesNotStopAllowedChild()
        {
            lod.emitterQuality.Clear();
            lod.emitterQuality.Add(new ParticleEmitterQuality { emitter = body, disabledQualities = new List<string> { "Low" } });
            sparks.Simulate(.2f, false, true);
            int count = sparks.particleCount;
            lod.ApplyPreviewLOD(0, "Low");
            Assert.Greater(count, 0);
            Assert.AreEqual(count, sparks.particleCount);
            Assert.IsTrue(sparks.emission.enabled);
            Assert.IsTrue(sparks.gameObject.activeInHierarchy);
        }

        [Test] public void SubEmitterLinksAreMaskedWithoutChangingIndicesOrAuthoredProperties()
        {
            var sub = body.subEmitters; sub.enabled = true;
            sub.AddSubEmitter(sparks, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritColor, .4f);
            sub.AddSubEmitter(smoke, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing, .7f);
            lod.ApplyPreviewLOD(0, "Low");
            Assert.AreEqual(2, sub.subEmittersCount);
            Assert.AreSame(sparks, sub.GetSubEmitterSystem(0));
            Assert.AreEqual(0, sub.GetSubEmitterEmitProbability(0));
            Assert.AreEqual(.7f, sub.GetSubEmitterEmitProbability(1), .0001f);
            body.Simulate(.2f, false, true);
            Assert.AreEqual(0, sparks.particleCount);
            lod.ApplyPreviewLOD(0, "High");
            Assert.AreEqual(.4f, sub.GetSubEmitterEmitProbability(0), .0001f);
            Assert.AreEqual(ParticleSystemSubEmitterType.Birth, sub.GetSubEmitterType(0));
            Assert.AreEqual(ParticleSystemSubEmitterProperties.InheritColor, sub.GetSubEmitterProperties(0));
        }

        [Test] public void NestedControllerOwnsItsEmittersAndIncomingLinks()
        {
            var nested = sparks.gameObject.AddComponent<ParticleDistanceLOD>();
            nested.emitterQuality.Add(new ParticleEmitterQuality { emitter = sparks, disabledQualities = new List<string> { "Low" } });
            var sub = body.subEmitters; sub.enabled = true;
            sub.AddSubEmitter(sparks, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing, .6f);
            lod.ApplyPreviewLOD(2, "Low");
            Assert.AreEqual(100, sparks.emission.rateOverTimeMultiplier);
            Assert.AreEqual(.6f, sub.GetSubEmitterEmitProbability(0), .0001f);
            nested.ApplyPreviewLOD(0, "Low");
            lod.SetLOD(0);
            Assert.AreEqual(0, sub.GetSubEmitterEmitProbability(0));
            Assert.IsFalse(sparks.emission.enabled);
            nested.RestoreOriginalSettings();
            Assert.AreEqual(.6f, sub.GetSubEmitterEmitProbability(0), .0001f);
        }

        [TestCase(ParticleLODMethod.Automatic)]
        [TestCase(ParticleLODMethod.ActivateAutomatic)]
        [TestCase(ParticleLODMethod.DirectSet)]
        public void ProjectQualityRefreshIsIndependentOfDistanceMode(ParticleLODMethod method)
        {
            int original = QualitySettings.GetQualityLevel();
            try
            {
                var names = QualitySettings.names;
                lod.emitterQuality[0].disabledQualities = new List<string> { names[0] };
                lod.method = method;
                QualitySettings.SetQualityLevel(0, false); lod.ActivateLOD();
                Assert.IsFalse(sparks.emission.enabled);
                QualitySettings.SetQualityLevel(names.Length - 1, false); lod.RefreshQuality();
                Assert.IsTrue(sparks.emission.enabled);
                Assert.AreEqual(names[names.Length - 1], lod.CurrentQualityName);
            }
            finally { QualitySettings.SetQualityLevel(original, false); }
        }

        [Test] public void EveryProjectTierAndLegacyNullRulesAreSupported()
        {
            int original = QualitySettings.GetQualityLevel();
            try
            {
                var names = QualitySettings.names;
                for (int i = 0; i < names.Length; i++)
                {
                    lod.emitterQuality[0].disabledQualities = new List<string> { names[i] };
                    QualitySettings.SetQualityLevel(i, false); lod.RefreshQuality();
                    Assert.IsTrue(lod.IsEmitterBlocked(sparks), names[i]);
                    Assert.IsFalse(lod.IsEmitterBlocked(body), names[i]);
                }
                lod.emitterQuality = null; lod.RefreshQuality();
                Assert.IsFalse(lod.IsEmitterBlocked(sparks));
                Assert.IsTrue(sparks.emission.enabled);
            }
            finally { QualitySettings.SetQualityLevel(original, false); }
        }
    }

    public partial class CascadeSessionTests
    {
        [Test] public void QualityMatrixSupportsUndoSaveCopyAndDeleteReferences()
        {
            var lod = session.Root.AddComponent<ParticleDistanceLOD>();
            var emitter = session.Emitters[0];
            Undo.IncrementCurrentGroup();
            Assert.IsTrue(CascadeLODPopup.SetEmitterQuality(session, lod, new[] { emitter }, "Low", false));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo(); Assert.IsTrue(lod.IsEmitterAllowed(emitter, "Low"));
            Undo.PerformRedo(); Assert.IsFalse(lod.IsEmitterAllowed(emitter, "Low"));
            Assert.IsTrue(session.Dirty);
            var copy = session.Duplicate(emitter);
            Assert.IsFalse(lod.IsEmitterAllowed(copy, "Low"));
            session.Delete(copy);
            Assert.IsFalse(lod.emitterQuality.Any(r => r.emitter == copy));
            session.Save(); session.Reload();
            lod = session.Root.GetComponent<ParticleDistanceLOD>();
            Assert.IsFalse(lod.IsEmitterAllowed(session.Emitters[0], "Low"));
            Assert.IsTrue(lod.IsEmitterAllowed(session.Emitters[1], "Low"));
        }

        [Test] public void QualityPreviewSkipsSimulationAndDoesNotChangeAuthorOrGlobalQuality()
        {
            var lod = session.Root.AddComponent<ParticleDistanceLOD>();
            var emitter = session.Emitters[0];
            CascadeLODPopup.SetEmitterQuality(session, lod, new[] { emitter }, "Low", false);
            int projectQuality = QualitySettings.GetQualityLevel();
            string author = EditorJsonUtility.ToJson(emitter);
            using (var preview = new CascadePreview { PreviewQualityName = "Low", PreviewLOD = 1 })
            {
                preview.Rebuild(session.Root);
                for (int i = 0; i < 15; i++) preview.Tick(.1f);
                Assert.IsNull(preview.Error);
                var blocked = preview.PreviewSystems[0];
                Assert.IsFalse(blocked.emission.enabled);
                Assert.AreEqual(0, blocked.particleCount);
                preview.ApplyVisibility();
                Assert.IsTrue(blocked.GetComponent<ParticleSystemRenderer>().forceRenderingOff);
                Assert.Greater(preview.PreviewSystems[1].particleCount, 0);
                Assert.IsFalse(preview.Statistics.Emitters[0].EventDriven);
            }
            Assert.AreEqual(projectQuality, QualitySettings.GetQualityLevel());
            Assert.AreEqual(author, EditorJsonUtility.ToJson(emitter));
        }
    }

    public class ParticleQualityRuntimeTests
    {
        [UnityTest] public IEnumerator RuntimeSwitchClearsSafelyResumesLoopsAndSupportsPooling()
        {
            yield return new EnterPlayMode();
            int original = QualitySettings.GetQualityLevel();
            GameObject root = null;
            try
            {
                var names = QualitySettings.names;
                int high = names.Length - 1;
                QualitySettings.SetQualityLevel(high, false);
                root = new GameObject("Runtime quality validation"); root.SetActive(false);
                var body = ParticleEmitterQualityTests.AddEmitter(root.transform, "Body");
                var sparks = ParticleEmitterQualityTests.AddEmitter(root.transform, "Sparks");
                var smoke = ParticleEmitterQualityTests.AddEmitter(root.transform, "Smoke");
                var child = ParticleEmitterQualityTests.AddEmitter(body.transform, "Sub emitter");
                var callback = ParticleEmitterQualityTests.AddEmitter(root.transform, "Callback emitter");
                var probe = callback.gameObject.AddComponent<ParticleQualityStopProbe>();
                var main = sparks.main; main.stopAction = ParticleSystemStopAction.Destroy;
                main = smoke.main; main.loop = false; main.stopAction = ParticleSystemStopAction.Disable;
                main = callback.main; main.stopAction = ParticleSystemStopAction.Callback;
                var sub = body.subEmitters; sub.enabled = true;
                sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing, .8f);
                var lod = root.AddComponent<ParticleDistanceLOD>();
                lod.method = ParticleLODMethod.DirectSet;
                foreach (var p in new[] { sparks, smoke, child, callback }) lod.emitterQuality.Add(new ParticleEmitterQuality {
                    emitter = p, disabledQualities = new List<string> { names[0] }
                });
                root.SetActive(true);
                body.Play(false); sparks.Play(false); smoke.Play(false); callback.Play(false);
                yield return null; yield return null;
                QualitySettings.SetQualityLevel(0, false); lod.RefreshQuality();
                Assert.AreEqual(0, sparks.particleCount);
                Assert.IsTrue(sparks.isStopped);
                Assert.IsTrue(body.isPlaying);
                Assert.AreEqual(0, sub.GetSubEmitterEmitProbability(0));
                yield return null; yield return null;
                Assert.IsTrue(sparks); Assert.IsTrue(smoke.gameObject.activeSelf);
                Assert.AreEqual(0, probe.StoppedCount);
                QualitySettings.SetQualityLevel(high, false); lod.RefreshQuality();
                Assert.IsTrue(sparks.isPlaying);
                Assert.IsFalse(smoke.isPlaying);
                Assert.IsFalse(child.isPlaying);
                Assert.AreEqual(.8f, sub.GetSubEmitterEmitProbability(0), .0001f);
                yield return null; yield return null;
                Assert.IsTrue(sparks); Assert.IsTrue(smoke.gameObject.activeSelf);
                Assert.AreEqual(0, probe.StoppedCount);
                root.SetActive(false);
                QualitySettings.SetQualityLevel(0, false);
                root.SetActive(true); sparks.Play(false);
                yield return null; yield return null;
                Assert.IsTrue(sparks.isStopped);
                Assert.AreEqual(0, sparks.particleCount);
                lod.ActivateLOD();
                Assert.IsFalse(sparks.emission.enabled);
                // Polling, not RefreshQuality, must react even in DirectSet mode.
                QualitySettings.SetQualityLevel(high, false);
                yield return new WaitForSecondsRealtime(.3f);
                Assert.IsTrue(sparks.emission.enabled);
                lod.enabled = false;
                Assert.AreEqual(ParticleSystemStopAction.Destroy, sparks.main.stopAction);
            }
            finally
            {
                if (root) Object.DestroyImmediate(root);
                QualitySettings.SetQualityLevel(original, false);
            }
            yield return new ExitPlayMode();
        }
    }
}
