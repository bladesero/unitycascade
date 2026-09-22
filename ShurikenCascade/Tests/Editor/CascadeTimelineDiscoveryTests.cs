using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        CascadeTimelineParameters DiscoveredTree()
        {
            var tracks = CascadeTimelineTrack.Build(session); var state = new CascadeTimelineState();
            state.OpenEmitters.Add(tracks[0].Key);
            var tree = new CascadeTimelineParameters(); tree.Ensure(session, tracks, state); return tree;
        }

        static void SetCurveMode(SerializedObject so, string path, int mode = 1)
        {
            var property = so.FindProperty(path); Assert.NotNull(property, path);
            property.FindPropertyRelative("minMaxState").intValue = mode;
            property.FindPropertyRelative("maxCurve").animationCurveValue = AnimationCurve.Linear(0, 0, 1, 1);
            property.FindPropertyRelative("minCurve").animationCurveValue = AnimationCurve.Linear(0, 0.1f, 1, 0.5f);
        }

        [Test]
        public void DiscoveryFindsActiveCurveParametersAcrossAllSupportedModules()
        {
            string[] paths = { "InitialModule.startSpeed", "EmissionModule.rateOverTime", "VelocityModule.x", "ClampVelocityModule.magnitude",
                "RotationModule.curve", "NoiseModule.strength", "UVModule.frameOverTime", "TrailModule.widthOverTrail",
                "InheritVelocityModule.m_Curve", "LifetimeByEmitterSpeedModule.m_Curve", "ForceModule.z", "SizeBySpeedModule.curve",
                "RotationBySpeedModule.curve", "ExternalForcesModule.multiplierCurve", "CollisionModule.m_Bounce", "LightsModule.rangeCurve" };
            using (var so = new SerializedObject(session.Emitters[0]))
            {
                foreach (string path in paths)
                {
                    string module = path.Split('.')[0]; var enabled = so.FindProperty(module + ".enabled");
                    if (enabled != null) enabled.boolValue = true;
                    SetCurveMode(so, path);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            var tree = DiscoveredTree();
            foreach (string path in paths)
                Assert.AreEqual(1, tree.Rows.Count(r => r.Path == path + ".maxCurve"), path);
            Assert.AreEqual(tree.Rows.Where(r => r.Path != null).Select(r => r.Path).Distinct().Count(), tree.Rows.Count(r => r.Path != null));
            foreach (var module in tree.Rows.Where(r => r.Kind == CascadeParameterRowKind.Module && tree.Rows.Any(r2 => r2.Kind == CascadeParameterRowKind.Curve && r2.Module == r.Module)))
                Assert.IsTrue(module.HasChildren);
        }

        [Test]
        public void DiscoveryIgnoresDormantModesAndAxesAndRefreshesAfterUndo()
        {
            var p = session.Emitters[0];
            using (var so = new SerializedObject(p))
            {
                so.FindProperty("NoiseModule.enabled").boolValue = true;
                SetCurveMode(so, "NoiseModule.strength", 2);
                SetCurveMode(so, "NoiseModule.strengthY"); SetCurveMode(so, "NoiseModule.remap");
                so.FindProperty("NoiseModule.separateAxes").boolValue = false;
                so.FindProperty("NoiseModule.remapEnabled").boolValue = false;
                SetCurveMode(so, "VelocityModule.x"); so.FindProperty("VelocityModule.enabled").boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            var tree = DiscoveredTree();
            Assert.AreEqual(2, tree.Rows.Count(r => r.Kind == CascadeParameterRowKind.Curve));
            Assert.IsFalse(tree.Rows.Any(r => r.Path == "NoiseModule.strengthY.maxCurve" || r.Path == "NoiseModule.remap.maxCurve"));
            Assert.IsTrue(CascadeTimelineAuthoring.Apply(session, p, "Constant Noise", so => so.FindProperty("NoiseModule.strength.minMaxState").intValue = 3));
            Assert.IsFalse(DiscoveredTree().Rows.Any(r => r.Kind == CascadeParameterRowKind.Curve));
            Undo.PerformUndo(); Assert.AreEqual(2, DiscoveredTree().Rows.Count(r => r.Kind == CascadeParameterRowKind.Curve));
        }

        [Test]
        public void DiscoveryExpandsGradientsAndCustomDataAccordingToStreamModes()
        {
            var p = session.Emitters[0]; var custom = p.customData; custom.enabled = true;
            custom.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
            custom.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 2);
            custom.SetVector(ParticleSystemCustomData.Custom1, 0, new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1)));
            custom.SetVector(ParticleSystemCustomData.Custom1, 1, 2);
            custom.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
            custom.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(new Gradient(), new Gradient()));
            var color = p.colorBySpeed; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(new Gradient());
            using (var so = new SerializedObject(p))
            { SetCurveMode(so, "CustomDataModule.vector0_3"); so.ApplyModifiedPropertiesWithoutUndo(); }
            var tree = DiscoveredTree();
            Assert.IsTrue(tree.Rows.Any(r => r.Path == "CustomDataModule.vector0_0.maxCurve"));
            Assert.IsFalse(tree.Rows.Any(r => r.Path == "CustomDataModule.vector0_3.maxCurve"));
            Assert.AreEqual(2, tree.Rows.Count(r => r.Path != null && r.Path.StartsWith("CustomDataModule.color1.")));
            Assert.IsTrue(tree.Rows.Any(r => r.Path == "ColorBySpeedModule.gradient.maxGradient"));
        }

        [Test]
        public void DiscoveryTraversesBurstArraysAndShapeSpeedOnlyWhenApplicable()
        {
            var p = session.Emitters[0]; var emission = p.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(1, 2), new ParticleSystem.Burst(2, 3) });
            var shape = p.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Cone; shape.arcMode = ParticleSystemShapeMultiModeValue.Loop;
            using (var so = new SerializedObject(p))
            {
                SetCurveMode(so, "EmissionModule.m_Bursts.Array.data[1].countCurve", 2);
                SetCurveMode(so, "ShapeModule.arc.speed"); so.ApplyModifiedPropertiesWithoutUndo();
            }
            var tree = DiscoveredTree();
            Assert.AreEqual(2, tree.Rows.Count(r => r.Path != null && r.Path.StartsWith("EmissionModule.m_Bursts.Array.data[1].countCurve.")));
            Assert.IsTrue(tree.Rows.Any(r => r.Path == "ShapeModule.arc.speed.maxCurve"));
            shape.arcMode = ParticleSystemShapeMultiModeValue.Random;
            Assert.IsFalse(DiscoveredTree().Rows.Any(r => r.Path == "ShapeModule.arc.speed.maxCurve"));
        }

        [Test]
        public void DiscoveryKeepsEmitterTimeSpeedAndTrailLengthDomainsDistinct()
        {
            var p = session.Emitters[0]; var main = p.main; main.duration = 8; main.startLifetime = 2; main.startDelay = 2; main.simulationSpeed = 2;
            main.startSpeed = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            var color = p.colorBySpeed; color.enabled = true; color.range = new Vector2(3, 8); color.color = new ParticleSystem.MinMaxGradient(new Gradient());
            var trail = p.trails; trail.enabled = true; trail.widthOverTrail = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            var tree = DiscoveredTree();
            var start = tree.Rows.First(r => r.Path == "InitialModule.startSpeed.maxCurve");
            Assert.AreEqual(4, CascadeTimelineParameters.DisplaySpan(start)); Assert.AreEqual(1, CascadeTimelineParameters.DisplayOrigin(start));
            var speed = tree.Rows.First(r => r.Path == "ColorBySpeedModule.gradient.maxGradient");
            Assert.IsFalse(CascadeTimelineParameters.UsesGlobalTime(speed)); Assert.AreEqual(3, speed.AxisMin); Assert.AreEqual(8, speed.AxisMax);
            var width = tree.Rows.First(r => r.Path == "TrailModule.widthOverTrail.maxCurve");
            Assert.IsFalse(CascadeTimelineParameters.UsesGlobalTime(width)); Assert.AreEqual("拖尾长度", width.AxisLabel);
        }

        [Test]
        public void DiscoveredNestedCurveUsesNativeBindingAndOnlySavesOnExplicitSave()
        {
            var p = session.Emitters[0]; var emission = p.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(1, 2) });
            using (var so = new SerializedObject(p))
            { SetCurveMode(so, "EmissionModule.m_Bursts.Array.data[0].countCurve"); so.ApplyModifiedPropertiesWithoutUndo(); }
            session.MarkDirty(); session.Save(); Undo.ClearAll(); var disk = System.IO.File.ReadAllBytes(path);
            var row = DiscoveredTree().Rows.First(r => r.Path == "EmissionModule.m_Bursts.Array.data[0].countCurve.maxCurve");
            var field = new CascadeTimelineNativeField(); Assert.IsTrue(field.Bind(session, row));
            Assert.IsTrue(field.Commit(session, AnimationCurve.Constant(0, 1, 9), null, null));
            Assert.AreEqual(9, p.emission.GetBurst(0).count.curve.keys[0].value);
            Assert.AreEqual(1, p.emission.GetBurst(0).time);
            Undo.PerformUndo(); Assert.AreEqual(0, p.emission.GetBurst(0).count.curve.keys[0].value);
            Undo.PerformRedo(); CollectionAssert.AreEqual(disk, System.IO.File.ReadAllBytes(path));
            session.Save(); session.Reload(); Assert.AreEqual(9, session.Emitters[0].emission.GetBurst(0).count.curve.keys[0].value);
        }
    }
}
