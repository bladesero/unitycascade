using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ParameterGlobalTimingIncludesDelayAndRetainsNormalizedAuthoring(bool gradient)
        {
            var p = session.Emitters[0]; var main = p.main;
            main.startDelay = 4; main.startLifetime = 6; main.simulationSpeed = 2;
            var track = CascadeTimelineTrack.Build(session)[0];
            var row = new CascadeParameterRow { Kind = gradient ? CascadeParameterRowKind.Gradient : CascadeParameterRowKind.Curve,
                Lifetime = CascadeLifetimeDomain.Build(p), Timing = CascadeTimelineTiming.Build(track), Height = 136 };
            var graph = CascadeTimelineParameters.GraphRect(row, new Rect(0, 0, 1210, 136), 10);
            Assert.AreEqual(410, graph.x, 0.001f); Assert.AreEqual(710, graph.xMax, 0.001f);
            Assert.AreEqual(3.5f, CascadeTimelineParameters.ToDisplayTime(row, 0.5f));
            Assert.AreEqual(0.5f, CascadeTimelineParameters.FromDisplayTime(row, 3.5f));
            Assert.AreEqual(CascadeTimelineScale.X(new Rect(210, 0, 1000, 1), 3.5f, 10), graph.x + graph.width * 0.5f, 0.001f);
        }

        [Test]
        public void BurstOnlyTimingUsesEarliestEffectiveBurstAndFirstLoop()
        {
            var p = session.Emitters[0]; var main = p.main;
            main.startDelay = 2; main.startLifetime = 4; main.simulationSpeed = 2; main.loop = true;
            var emission = p.emission; emission.rateOverTime = 0; emission.rateOverDistance = 0;
            var ignored = new ParticleSystem.Burst(0.2f, 10) { probability = 0 };
            emission.SetBursts(new[] { new ParticleSystem.Burst(3, 10), ignored, new ParticleSystem.Burst(1, 10) });
            var timing = CascadeTimelineTiming.Build(CascadeTimelineTrack.Build(session)[0]);
            Assert.AreEqual(1.5f, timing.Origin); Assert.IsTrue(timing.Absolute);
            emission.rateOverTime = 10;
            Assert.AreEqual(1, CascadeTimelineTiming.Build(CascadeTimelineTrack.Build(session)[0]).Origin);
            main.prewarm = true;
            Assert.AreEqual(0, CascadeTimelineTiming.Build(CascadeTimelineTrack.Build(session)[0]).Origin);
        }

        [Test]
        public void EventDrivenAndFrozenTimingDoNotInventGlobalBirthTime()
        {
            var p = session.Emitters[0]; var track = CascadeTimelineTrack.Build(session)[0]; track.EventDriven = true;
            var row = new CascadeParameterRow { Kind = CascadeParameterRowKind.Gradient, Height = 118,
                Lifetime = CascadeLifetimeDomain.Build(p), Timing = CascadeTimelineTiming.Build(track) };
            Assert.IsFalse(CascadeTimelineParameters.UsesGlobalTime(row));
            Assert.AreEqual(50, CascadeTimelineParameters.ToDisplayTime(row, 0.5f));
            Assert.AreEqual(0.5f, CascadeTimelineParameters.FromDisplayTime(row, 50));
            track.EventDriven = false; track.Speed = 0;
            Assert.IsFalse(CascadeTimelineTiming.Build(track).Absolute);
            track.Speed = 1; track.UnknownDelay = true;
            Assert.IsFalse(CascadeTimelineTiming.Build(track).Absolute);
        }

        [Test]
        public void LifetimeTracksUseParticleLifetimeAndSimulationSpeedInsteadOfEmitterDuration()
        {
            var p = session.Emitters[0]; var main = p.main;
            main.duration = 30; main.startLifetime = 4; main.simulationSpeed = 2;
            var domain = CascadeLifetimeDomain.Build(p);
            Assert.AreEqual(2, domain.Seconds);
            var row = new CascadeParameterRow { Kind = CascadeParameterRowKind.Curve, Lifetime = domain, Height = 136 };
            var rect = new Rect(0, 0, 1210, 136);
            Assert.AreEqual(200, CascadeTimelineParameters.GraphRect(row, rect, 10).width, 0.001f);
            Assert.AreEqual(2, CascadeTimelineParameters.DisplaySpan(row));
            row.Kind = CascadeParameterRowKind.Gradient;
            Assert.AreEqual(200, CascadeTimelineParameters.GraphRect(row, rect, 10).width, 0.001f);
            row.RandomDomain = true;
            Assert.AreEqual(1000, CascadeTimelineParameters.GraphRect(row, rect, 10).width, 0.001f);
            Assert.AreEqual(100, CascadeTimelineParameters.DisplaySpan(row));
        }

        [Test]
        public void RandomLifetimeUsesMaximumWithTheSameAgeLabel()
        {
            var p = session.Emitters[0]; var main = p.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2, 6); main.simulationSpeed = 2;
            var random = CascadeLifetimeDomain.Build(p);
            Assert.AreEqual(3, random.Seconds);
            main.startLifetime = 6;
            Assert.AreEqual(CascadeLifetimeDomain.Build(p).Label, random.Label);
            main.startLifetime = new ParticleSystem.MinMaxCurve(4, AnimationCurve.Linear(0, 0.5f, 1, 1));
            Assert.AreEqual(2, CascadeLifetimeDomain.Build(p).Seconds);
            main.simulationSpeed = 0;
            Assert.AreEqual(4, CascadeLifetimeDomain.Build(p).Seconds);
            Assert.IsTrue(CascadeLifetimeDomain.Build(p).Frozen);
            main.startLifetime = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Constant(0, 1, 0));
            Assert.IsTrue(CascadeLifetimeDomain.Build(p).Normalized);
        }

        [Test]
        public void LifetimeChangeRescalesBothTracksWithoutChangingNormalizedKeys()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            var color = p.colorOverLifetime; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(new Gradient());
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            var state = new CascadeTimelineState(); var tracks = CascadeTimelineTrack.Build(session);
            state.OpenEmitters.Add(tracks[0].Key);
            var tree = new CascadeTimelineParameters(); tree.Ensure(session, tracks, state);
            var before = tree.Rows.First(r => r.Kind == CascadeParameterRowKind.Curve).Lifetime.Seconds;
            Assert.IsTrue(CascadeTimelineAuthoring.Apply(session, p, "Change Lifetime", so => so.FindProperty("InitialModule.startLifetime.scalar").floatValue = 8));
            tree.Rebuild(); tree.Ensure(session, CascadeTimelineTrack.Build(session), state);
            Assert.AreEqual(8, tree.Rows.First(r => r.Kind == CascadeParameterRowKind.Curve).Lifetime.Seconds);
            Assert.AreEqual(8, tree.Rows.First(r => r.Kind == CascadeParameterRowKind.Gradient).Lifetime.Seconds);
            Assert.AreEqual(1, size.size.curve.keys[1].time);
            Assert.AreEqual(1, color.color.gradient.colorKeys[1].time);
            Undo.PerformUndo(); Assert.AreEqual(before, CascadeLifetimeDomain.Build(p).Seconds);
        }

        [Test]
        public void InlineGradientConfigurationKeepsSelectionAfterKeyReordering()
        {
            var p = session.Emitters[0]; var color = p.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.green, 0.4f), new GradientColorKey(Color.blue, 1) },
                new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(0.5f, 0.4f), new GradientAlphaKey(1, 1) });
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var edit = new CascadeTimelineKeyEdit(session, p, "ColorModule.gradient.maxGradient", 0);
            edit.Configure(0.7f, 0, Color.yellow, 0, 0);
            Assert.AreEqual(1, edit.Index); Assert.IsTrue(edit.Commit());
            edit = new CascadeTimelineKeyEdit(session, p, "ColorModule.gradient.maxGradient", 0, true);
            edit.Configure(0.8f, 0.3f, Color.white, 0, 0);
            Assert.AreEqual(1, edit.Index); Assert.IsTrue(edit.Commit());
        }

        [Test]
        public void ParameterTreeFoldersOnlyShowEnabledModulesAndRespectParameterModes()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true; size.separateAxes = true;
            size.x = new ParticleSystem.MinMaxCurve(3, AnimationCurve.Linear(0, 0, 1, 1), AnimationCurve.Linear(0, 1, 1, 2));
            size.y = 2; size.z = new ParticleSystem.MinMaxCurve(2, 3);
            var color = p.colorOverLifetime; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(new Gradient(), new Gradient());
            session.MarkDirty(); session.Save();
            var tree = new CascadeTimelineParameters(); var state = new CascadeTimelineState(); var tracks = CascadeTimelineTrack.Build(session);
            tree.Ensure(session, tracks, state); Assert.AreEqual(2, tree.Rows.Count);
            CascadeTimelineParameters.ToggleEmitter(state, tracks[0].Key); tree.Ensure(session, tracks, state);
            Assert.AreEqual(2, tree.Rows.Count(r => r.Kind == CascadeParameterRowKind.Emitter));
            Assert.AreEqual(2, tree.Rows.Count(r => r.Kind == CascadeParameterRowKind.Curve));
            Assert.AreEqual(2, tree.Rows.Count(r => r.Kind == CascadeParameterRowKind.Gradient));
            Assert.AreEqual(1, tree.Rows.Count(r => r.Kind == CascadeParameterRowKind.Burst));
            Assert.IsFalse(tree.Rows.Any(r => r.Kind == CascadeParameterRowKind.Module && CascadeModules.All[r.Module].Path == "NoiseModule"));
            state.CollapsedModules.Add(tracks[0].Key + "/SizeModule"); state.TreeRevision++; tree.Ensure(session, tracks, state);
            Assert.IsFalse(tree.Rows.Any(r => r.Kind == CascadeParameterRowKind.Curve));
            Assert.IsFalse(session.Dirty); Assert.AreEqual(2, session.Root.transform.childCount);
        }

        [Test]
        public void TimelineModuleEnableDisableResetPreservesDataAndSupportsUndo()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.size = 7;
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            Assert.IsTrue(CascadeTimelineAuthoring.SetEnabled(session, p, "SizeModule", true));
            Assert.AreEqual(7, size.size.constant); Assert.IsTrue(size.enabled);
            Undo.PerformUndo(); Assert.IsFalse(size.enabled); Assert.AreEqual(7, size.size.constant);
            Undo.PerformRedo(); Assert.IsTrue(size.enabled);
            int sceneCount = EditorSceneManager.previewSceneCount;
            Assert.IsTrue(CascadeTimelineAuthoring.ResetModule(session, p, CascadeModules.All.First(m => m.Path == "SizeModule")));
            Undo.FlushUndoRecordObjects(); Assert.IsTrue(size.enabled); Assert.AreNotEqual(7, size.size.constant);
            Undo.PerformUndo(); Assert.AreEqual(7, size.size.constant); Assert.IsTrue(size.enabled);
            Undo.PerformRedo(); Assert.AreNotEqual(7, size.size.constant); Assert.IsTrue(size.enabled);
            Assert.AreEqual(sceneCount, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void CurveKeysPreserveTangentsWeightsWrapModesAndOnlyChangeChosenAxis()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true; size.separateAxes = true;
            var key = new Keyframe(0.4f, 0.6f, 2, 3, 0.2f, 0.3f) { weightedMode = WeightedMode.Both };
            var curve = new AnimationCurve(new Keyframe(0, 0), key, new Keyframe(1, 1)) { preWrapMode = WrapMode.Loop, postWrapMode = WrapMode.PingPong };
            size.x = new ParticleSystem.MinMaxCurve(3, curve); size.y = 8;
            session.MarkDirty(); session.Save(); Undo.ClearAll(); byte[] disk = File.ReadAllBytes(path);
            var edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", 1);
            edit.Move(0.7f, 0.9f); Assert.AreEqual(0.4f, size.x.curve.keys[1].time); Assert.IsTrue(edit.Commit());
            var result = size.x.curve;
            Assert.AreEqual(0.7f, result.keys[1].time); Assert.AreEqual(3, result.keys[1].outTangent);
            Assert.AreEqual(WeightedMode.Both, result.keys[1].weightedMode); Assert.AreEqual(0.3f, result.keys[1].outWeight);
            Assert.AreEqual(WrapMode.Loop, result.preWrapMode); Assert.AreEqual(WrapMode.PingPong, result.postWrapMode);
            Assert.AreEqual(3, size.x.curveMultiplier); Assert.AreEqual(8, size.y.constant);
            Undo.PerformUndo(); Assert.AreEqual(0.4f, size.x.curve.keys[1].time);
            Undo.PerformRedo(); Assert.AreEqual(0.7f, size.x.curve.keys[1].time);
            CollectionAssert.AreEqual(disk, File.ReadAllBytes(path)); session.Save(); session.Reload();
            Assert.AreEqual(0.7f, session.Emitters[0].sizeOverLifetime.x.curve.keys[1].time);
        }

        [Test]
        public void GradientKeysSupportBothGradientsColorAlphaAndUndoSave()
        {
            var p = session.Emitters[0]; var color = p.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient { mode = GradientMode.Fixed };
            gradient.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.green, 0.5f), new GradientColorKey(Color.blue, 1) },
                new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(0.7f, 0.5f), new GradientAlphaKey(1, 1) });
            color.color = new ParticleSystem.MinMaxGradient(gradient, new Gradient());
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            var edit = new CascadeTimelineKeyEdit(session, p, "ColorModule.gradient.minGradient", 1);
            edit.Configure(0.25f, 0, Color.yellow, 0, 0); Assert.IsTrue(edit.Commit());
            Assert.AreEqual(GradientMode.Fixed, color.color.gradientMin.mode);
            Assert.AreEqual(Color.yellow, color.color.gradientMin.colorKeys[1].color);
            Assert.AreEqual(0.25f, color.color.gradientMin.colorKeys[1].time, 0.001f);
            Assert.AreEqual(0.5f, color.color.gradientMin.alphaKeys[1].time, 0.001f);
            Undo.PerformUndo(); Assert.AreEqual(Color.green, color.color.gradientMin.colorKeys[1].color);
            Undo.PerformRedo(); Assert.AreEqual(Color.yellow, color.color.gradientMin.colorKeys[1].color);
            edit = new CascadeTimelineKeyEdit(session, p, "ColorModule.gradient.minGradient", 1, true);
            edit.Configure(0.8f, 0.2f, Color.white, 0, 0); Assert.IsTrue(edit.Commit());
            Assert.AreEqual(0.2f, color.color.gradientMin.alphaKeys[1].alpha, 0.005f);
            session.Save(); session.Reload();
            Assert.AreEqual(ParticleSystemGradientMode.TwoGradients, session.Emitters[0].colorOverLifetime.color.mode);
            Assert.AreEqual(0.8f, session.Emitters[0].colorOverLifetime.color.gradientMin.alphaKeys[1].time, 0.001f);
        }

        [Test]
        public void ParameterKeyAddDeleteCancelAndStaleEditAreSafe()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            session.MarkDirty(); session.Save();
            var edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", 0);
            Assert.IsFalse(edit.Commit()); Assert.IsFalse(session.Dirty);
            edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", 0); edit.Move(0.1f, 2); edit.Cancel(); Assert.IsFalse(edit.Commit());
            edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", -1); edit.Add(0.5f, 0.7f); Assert.IsTrue(edit.Commit()); Assert.AreEqual(3, size.size.curve.length);
            edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", 1); edit.Delete(); Assert.IsTrue(edit.Commit()); Assert.AreEqual(2, size.size.curve.length);
            edit = new CascadeTimelineKeyEdit(session, p, "SizeModule.curve.maxCurve", 0); edit.Move(0.1f, 0.2f);
            var main = p.main; main.startDelay = 3; Assert.IsFalse(edit.Commit());
            var color = p.colorOverLifetime; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(new Gradient());
            for (int i = 0; i < 7; i++)
            { edit = new CascadeTimelineKeyEdit(session, p, "ColorModule.gradient.maxGradient", -1); edit.Add((i + 1) / 10f, 0); edit.Commit(); }
            Assert.AreEqual(8, color.color.gradient.colorKeys.Length);
        }

        [Test]
        public void RelativeBurstTrackEditsEventDrivenEmittersAndKeepsArrayCountConsistent()
        {
            var parent = session.Emitters[0]; var p = session.Emitters[1]; var subs = parent.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(p, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            var main = p.main; main.simulationSpeed = 2;
            Assert.IsTrue(CascadeTimelineAuthoring.AddBurst(session, p, 0.5f));
            var track = CascadeTimelineTrack.Build(session).First(t => t.Emitter == p);
            Assert.IsTrue(track.EventDriven);
            var edit = CascadeTimelineEdit.BeginRelativeBurst(session, track, 0); edit.Move(0.5f); Assert.IsTrue(edit.Commit());
            Assert.AreEqual(1, p.emission.GetBurst(0).time, "Relative row uses emitter seconds, independent of Simulation Speed.");
            Undo.PerformUndo(); Assert.AreEqual(0.5f, p.emission.GetBurst(0).time);
            Assert.IsTrue(CascadeTimelineAuthoring.DeleteBurst(session, p, 0)); Assert.AreEqual(0, p.emission.burstCount);
            Undo.PerformUndo(); Assert.AreEqual(1, p.emission.burstCount);
        }

        [Test]
        public void TimelineParametersRejectNestedPrefabMutation()
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            string nestedPath = folder + "/Nested.prefab"; PrefabUtility.SaveAsPrefabAsset(p.gameObject, nestedPath);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
            var target = nested.GetComponent<ParticleSystem>(); string before = EditorJsonUtility.ToJson(target);
            Assert.IsFalse(CascadeTimelineAuthoring.SetEnabled(session, target, "ColorModule", true));
            Assert.IsFalse(CascadeTimelineAuthoring.AddBurst(session, target, 1));
            Assert.IsFalse(CascadeTimelineAuthoring.ResetModule(session, target, CascadeModules.All.First(m => m.Path == "SizeModule")));
            var edit = new CascadeTimelineKeyEdit(session, target, "SizeModule.curve.maxCurve", 0); edit.Move(0.2f, 1); Assert.IsFalse(edit.Commit());
            Assert.AreEqual(before, EditorJsonUtility.ToJson(target));
        }

    }
}
