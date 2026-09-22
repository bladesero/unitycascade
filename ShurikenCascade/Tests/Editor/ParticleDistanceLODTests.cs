using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public class ParticleDistanceLODTests
    {
        GameObject root;
        ParticleSystem system;
        ParticleDistanceLOD lod;

        [SetUp] public void SetUp()
        {
            root = new GameObject("LOD test");
            root.SetActive(false);
            system = root.AddComponent<ParticleSystem>();
            var e = system.emission;
            e.rateOverTime = 100; e.rateOverDistance = 40;
            e.SetBursts(new[] { new ParticleSystem.Burst(0, (short)80) });
            var main = system.main; main.maxParticles = 1000;
            var noise = system.noise; noise.enabled = true;
            lod = root.AddComponent<ParticleDistanceLOD>();
        }

        [TearDown] public void TearDown() { Object.DestroyImmediate(root); }

        [TestCase(0, 0)] [TestCase(19.99f, 0)] [TestCase(20, 1)]
        [TestCase(49.99f, 1)] [TestCase(50, 2)] [TestCase(1000, 2)]
        public void DistanceBoundaries(float distance, int expected)
        { Assert.AreEqual(expected, lod.SelectLOD(distance)); }

        [Test] public void HysteresisAndLargeCameraJump()
        {
            Assert.AreEqual(1, lod.SelectLOD(19.5f, 1));
            Assert.AreEqual(0, lod.SelectLOD(18.9f, 1));
            Assert.AreEqual(0, lod.SelectLOD(1, 2));
            Assert.AreEqual(2, lod.SelectLOD(100, 0));
        }

        [Test] public void SwitchingDoesNotCompoundAndRestoresBurstsAndModules()
        {
            lod.SetLOD(1); lod.SetLOD(2); lod.SetLOD(1);
            Assert.AreEqual(50, system.emission.rateOverTimeMultiplier);
            Assert.AreEqual(20, system.emission.rateOverDistanceMultiplier);
            Assert.AreEqual(40, system.emission.GetBurst(0).count.constant);
            Assert.AreEqual(500, system.main.maxParticles);
            lod.SetLOD(2); Assert.IsFalse(system.noise.enabled);
            lod.RestoreOriginalSettings();
            Assert.AreEqual(100, system.emission.rateOverTimeMultiplier);
            Assert.AreEqual(80, system.emission.GetBurst(0).count.constant);
            Assert.AreEqual(1000, system.main.maxParticles);
            Assert.IsTrue(system.noise.enabled);
        }

        [Test] public void BurstCurvesAreScaledWithoutModifyingSharedCurve()
        {
            var curve = AnimationCurve.Linear(0, 10, 1, 20);
            var e = system.emission;
            e.SetBursts(new[] { new ParticleSystem.Burst(0, new ParticleSystem.MinMaxCurve(4, curve)) });
            lod.SetLOD(1);
            Assert.AreEqual(2, e.GetBurst(0).count.curveMultiplier);
            Assert.AreEqual(20, curve.Evaluate(1));
            lod.RestoreOriginalSettings();
            Assert.AreEqual(4, e.GetBurst(0).count.curveMultiplier);
        }

        [Test] public void EmptyLevelsRestoreAndNestedControllerOwnsChildren()
        {
            var child = new GameObject("nested"); child.transform.SetParent(root.transform);
            var nested = child.AddComponent<ParticleSystem>();
            child.AddComponent<ParticleDistanceLOD>();
            var e = nested.emission; e.rateOverTime = 33;
            lod.SetLOD(2);
            Assert.AreEqual(33, e.rateOverTimeMultiplier);
            lod.levels = new ParticleLODLevel[0]; lod.SetLOD(0);
            Assert.AreEqual(100, system.emission.rateOverTimeMultiplier);
            Assert.AreEqual(-1, lod.CurrentLOD);
        }

        [Test] public void ActivationRechecksCameraAndManualModeUsesDirectLOD()
        {
            var cameraObject = new GameObject("LOD camera");
            try
            {
                lod.distanceCamera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = new Vector3(0, 0, 60);
                lod.method = ParticleLODMethod.ActivateAutomatic;
                lod.ActivateLOD(); Assert.AreEqual(2, lod.CurrentLOD);
                cameraObject.transform.position = Vector3.zero;
                TickController(); Assert.AreEqual(2, lod.CurrentLOD);
                lod.ActivateLOD(); Assert.AreEqual(0, lod.CurrentLOD);
                lod.method = ParticleLODMethod.DirectSet; lod.directLOD = 1;
                lod.ActivateLOD(); Assert.AreEqual(1, lod.CurrentLOD);
                lod.method = ParticleLODMethod.Automatic; lod.distanceCheckTime = 0;
                lod.ActivateLOD();
                cameraObject.transform.position = new Vector3(0, 0, 60);
                TickController(); Assert.AreEqual(2, lod.CurrentLOD);
            }
            finally { Object.DestroyImmediate(cameraObject); }
        }

        void TickController()
        {
            // EditMode does not dispatch MonoBehaviour messages; exercise our scheduler directly.
            typeof(ParticleDistanceLOD).GetMethod("Update", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).Invoke(lod, null);
        }

        [Test] public void ExcessiveHysteresisStillAllowsReturnToHighestQuality()
        {
            lod.hysteresis = 1000;
            Assert.AreEqual(0, lod.SelectLOD(0, 2));
        }
    }

    public partial class CascadeSessionTests
    {
        [Test] public void DisabledDistanceLODDoesNotAffectPreview()
        {
            var lod = session.Root.AddComponent<ParticleDistanceLOD>();
            lod.enabled = false;
            float rate = session.Emitters[0].emission.rateOverTimeMultiplier;
            using (var preview = new CascadePreview { PreviewLOD = 2 })
            {
                preview.Rebuild(session.Root);
                Assert.IsNull(preview.Error);
                Assert.AreEqual(rate, preview.PreviewSystems[0].emission.rateOverTimeMultiplier);
            }
        }

        [Test] public void DistanceLODPreviewIsIsolatedAndConfigurationSurvivesSave()
        {
            var lod = Undo.AddComponent<ParticleDistanceLOD>(session.Root);
            lod.levels[1].distance = 42;
            var originalRate = session.Emitters[0].emission.rateOverTimeMultiplier;
            session.MarkDirty();
            using (var preview = new CascadePreview { PreviewLOD = 1 })
            {
                preview.Rebuild(session.Root);
                Assert.IsNull(preview.Error);
                Assert.AreEqual(originalRate * .5f, preview.PreviewSystems[0].emission.rateOverTimeMultiplier);
                Assert.AreEqual(originalRate, session.Emitters[0].emission.rateOverTimeMultiplier);
            }
            session.Save(); session.Reload();
            Assert.AreEqual(42, session.Root.GetComponent<ParticleDistanceLOD>().levels[1].distance);
            Assert.AreEqual(originalRate, session.Emitters[0].emission.rateOverTimeMultiplier);
        }
    }
}
