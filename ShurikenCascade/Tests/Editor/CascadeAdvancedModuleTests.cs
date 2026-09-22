using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        void ConfigureAdvancedModules(ParticleSystem p)
        {
            var support = new GameObject("Module References");
            support.transform.SetParent(p.transform, false);
            var light = support.AddComponent<Light>();
            var forceField = support.AddComponent<ParticleSystemForceField>();
            var collider = support.AddComponent<BoxCollider>();
            var support2D = new GameObject("2D Collider");
            support2D.transform.SetParent(support.transform, false);
            var collider2D = support2D.AddComponent<BoxCollider2D>();
            var subParticle = support.AddComponent<ParticleSystem>();
            var main = subParticle.main; main.playOnAwake = false;
            var curve = new ParticleSystem.MinMaxCurve(2, AnimationCurve.Linear(0, 0.2f, 1, 0.8f));
            var inherit = p.inheritVelocity; inherit.enabled = true; inherit.mode = ParticleSystemInheritVelocityMode.Current; inherit.curve = curve;
            var lifetime = p.lifetimeByEmitterSpeed; lifetime.enabled = true; lifetime.curve = curve; lifetime.range = new Vector2(2, 8);
            var force = p.forceOverLifetime; force.enabled = true; force.x = curve; force.y = 3; force.z = 5; force.space = ParticleSystemSimulationSpace.World; force.randomized = true;
            var color = p.colorBySpeed; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(Color.cyan, Color.yellow); color.range = new Vector2(1, 9);
            var size = p.sizeBySpeed; size.enabled = true; size.separateAxes = true; size.x = curve; size.y = 3; size.z = 4; size.range = new Vector2(2, 7);
            var rotation = p.rotationBySpeed; rotation.enabled = true; rotation.separateAxes = true; rotation.x = curve; rotation.y = 2; rotation.z = 3; rotation.range = new Vector2(1, 8);
            var external = p.externalForces; external.enabled = true; external.multiplierCurve = curve; external.influenceFilter = ParticleSystemGameObjectFilter.LayerMaskAndList; external.influenceMask = 1 << 7; external.AddInfluence(forceField);
            var collision = p.collision; collision.enabled = true; collision.type = ParticleSystemCollisionType.Planes; collision.AddPlane(support.transform); collision.dampen = new ParticleSystem.MinMaxCurve(0.1f, 0.4f); collision.bounce = 0.7f; collision.lifetimeLoss = 0.2f;
            var trigger = p.trigger; trigger.enabled = true; trigger.SetCollider(0, collider); trigger.SetCollider(1, collider2D); trigger.enter = ParticleSystemOverlapAction.Kill; trigger.colliderQueryMode = ParticleSystemColliderQueryMode.All;
            var subs = p.subEmitters; subs.enabled = true; subs.AddSubEmitter(subParticle, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritColor, 0.4f);
            var lights = p.lights; lights.enabled = true; lights.light = light; lights.ratio = 0.25f; lights.range = curve; lights.intensity = 2; lights.maxLights = 12;
            var custom = p.customData; custom.enabled = true;
            custom.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
            custom.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 3);
            custom.SetVector(ParticleSystemCustomData.Custom1, 0, curve);
            custom.SetVector(ParticleSystemCustomData.Custom1, 1, 4);
            custom.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
            custom.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(Color.red, Color.blue));
        }

        [TestCase("InheritVelocityModule")]
        [TestCase("LifetimeByEmitterSpeedModule")]
        [TestCase("ForceModule")]
        [TestCase("ColorBySpeedModule")]
        [TestCase("SizeBySpeedModule")]
        [TestCase("RotationBySpeedModule")]
        [TestCase("ExternalForcesModule")]
        [TestCase("CollisionModule")]
        [TestCase("TriggerModule")]
        [TestCase("SubModule")]
        [TestCase("LightsModule")]
        [TestCase("CustomDataModule")]
        public void AdvancedModulePasteUndoAndSaveRoundTrip(string modulePath)
        {
            var source = session.Emitters[0]; var target = session.Emitters[1];
            ConfigureAdvancedModules(source);
            string before = EditorJsonUtility.ToJson(target);
            string expected;
            using (var so = new SerializedObject(source)) expected = PropertySnapshot(so.FindProperty(modulePath));
            using (var clipboard = new CascadeModuleClipboard())
            {
                Assert.IsTrue(Module(modulePath).Supported);
                clipboard.Copy(source, Module(modulePath));
                clipboard.Paste(session, target, Module(modulePath));
                Undo.FlushUndoRecordObjects();
                string after = EditorJsonUtility.ToJson(target);
                Assert.AreNotEqual(before, after);
                using (var so = new SerializedObject(target)) Assert.AreEqual(expected, PropertySnapshot(so.FindProperty(modulePath)));
                Undo.PerformUndo(); Assert.AreEqual(before, EditorJsonUtility.ToJson(target));
                Undo.PerformRedo(); Assert.AreEqual(after, EditorJsonUtility.ToJson(target));
            }
            session.Save(); session.Reload();
            target = session.Root.transform.GetChild(1).GetComponent<ParticleSystem>();
            using (var so = new SerializedObject(target)) Assert.AreEqual(expected, PropertySnapshot(so.FindProperty(modulePath)));
            var support = session.Root.transform.GetChild(0).Find("Module References");
            if (modulePath == "CollisionModule") Assert.AreEqual(support, target.collision.GetPlane(0));
            if (modulePath == "ExternalForcesModule") Assert.AreEqual(support.GetComponent<ParticleSystemForceField>(), target.externalForces.GetInfluence(0));
            if (modulePath == "TriggerModule")
            {
                Assert.AreEqual(support.GetComponent<BoxCollider>(), target.trigger.GetCollider(0));
                Assert.AreEqual(support.GetComponentInChildren<BoxCollider2D>(), target.trigger.GetCollider(1));
            }
            if (modulePath == "LightsModule") Assert.AreEqual(support.GetComponent<Light>(), target.lights.light);
            if (modulePath == "SubModule") Assert.AreEqual(support.GetComponent<ParticleSystem>(), target.subEmitters.GetSubEmitterSystem(0));
        }

        [Test]
        public void AdvancedFieldsFollowModesWithoutClearingHiddenData()
        {
            ConfigureAdvancedModules(session.Emitters[0]);
            using (var so = new SerializedObject(session.Emitters[0]))
            {
                so.FindProperty("CollisionModule.type").intValue = (int)ParticleSystemCollisionType.Planes;
                Assert.IsTrue(CascadeModules.IsVisible(so, "CollisionModule.m_Planes"));
                Assert.IsFalse(CascadeModules.IsVisible(so, "CollisionModule.collidesWith"));
                so.FindProperty("CollisionModule.type").intValue = (int)ParticleSystemCollisionType.World;
                so.FindProperty("CollisionModule.quality").intValue = (int)ParticleSystemCollisionQuality.Low;
                Assert.IsFalse(CascadeModules.IsVisible(so, "CollisionModule.m_Planes"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "CollisionModule.voxelSize"));
                so.FindProperty("CollisionModule.collisionMode").intValue = (int)ParticleSystemCollisionMode.Collision2D;
                Assert.IsTrue(CascadeModules.IsVisible(so, "CollisionModule.voxelSize"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "CollisionModule.quality"));
                so.FindProperty("CollisionModule.quality").intValue = (int)ParticleSystemCollisionQuality.High;
                Assert.IsFalse(CascadeModules.IsVisible(so, "CollisionModule.voxelSize"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "CollisionModule.collidesWithDynamic"));
                so.FindProperty("ExternalForcesModule.influenceFilter").intValue = (int)ParticleSystemGameObjectFilter.List;
                Assert.IsFalse(CascadeModules.IsVisible(so, "ExternalForcesModule.influenceMask"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "ExternalForcesModule.influenceList"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "CustomDataModule.vector0_2"));
                Assert.IsFalse(CascadeModules.IsVisible(so, "CustomDataModule.vector0_3"));
                Assert.IsFalse(CascadeModules.IsVisible(so, "CustomDataModule.color0"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "CustomDataModule.color1"));
                Assert.IsFalse(CascadeModules.IsVisible(so, "CustomDataModule.vector1_0"));
                so.FindProperty("SizeBySpeedModule.separateAxes").boolValue = false;
                Assert.IsFalse(CascadeModules.IsVisible(so, "SizeBySpeedModule.y"));
                Assert.AreEqual(3, so.FindProperty("SizeBySpeedModule.y.scalar").floatValue);
            }
        }

        [Test]
        public void SubEmitterCyclesAndSelfPasteAreRejectedWithoutMutation()
        {
            var parent = session.Emitters[0]; var child = session.Emitters[1];
            var subs = parent.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            using (var pending = new SerializedObject(child))
            {
                CascadeModules.AddSubEmitterRow(pending.FindProperty("SubModule.subEmitters"));
                var reference = pending.FindProperty("SubModule.subEmitters").GetArrayElementAtIndex(0).FindPropertyRelative("emitter");
                reference.objectReferenceValue = parent;
                Assert.IsFalse(CascadeModuleReferences.ValidateSubEmitters(session, child, pending, out var reason));
                StringAssert.Contains("循环", reason);
                reference.objectReferenceValue = child;
                Assert.IsFalse(CascadeModuleReferences.ValidateSubEmitters(session, child, pending, out reason));
                StringAssert.Contains("自己", reason);
                reference.objectReferenceValue = null;
                Assert.IsTrue(CascadeModuleReferences.ValidateSubEmitters(session, child, pending, out _));
            }
            using (var clipboard = new CascadeModuleClipboard())
            {
                string before = EditorJsonUtility.ToJson(child);
                clipboard.Copy(parent, Module("SubModule"));
                Assert.IsFalse(clipboard.CanPaste(session, child, Module("SubModule"), out _));
                Assert.Throws<InvalidOperationException>(() => clipboard.Paste(session, child, Module("SubModule")));
                Assert.AreEqual(before, EditorJsonUtility.ToJson(child));
            }
        }

        [Test]
        public void ModuleReferencePickerFiltersTypesAndRejectsForeignObjects()
        {
            var source = session.Emitters[0]; ConfigureAdvancedModules(source);
            string triggerPath = "TriggerModule.primitives.Array.data[0]";
            var colliders = CascadeModuleReferences.Candidates(session, triggerPath);
            Assert.AreEqual(2, colliders.Length);
            Assert.IsTrue(colliders.Any(c => c is Collider));
            Assert.IsTrue(colliders.Any(c => c is Collider2D));
            Assert.IsFalse(CascadeModuleReferences.ValidateReference(session, source, triggerPath, source.transform, out _));
            var foreign = new GameObject("Foreign Collider");
            try { Assert.IsFalse(CascadeModuleReferences.ValidateReference(session, source, triggerPath, foreign.AddComponent<BoxCollider>(), out _)); }
            finally { Object.DestroyImmediate(foreign); }
            using (var so = new SerializedObject(source))
            {
                var list = so.FindProperty("TriggerModule.primitives");
                CascadeModules.RemoveReference(list, 0);
                so.ApplyModifiedProperties();
                Assert.AreEqual(1, source.trigger.colliderCount);
                Assert.IsInstanceOf<BoxCollider2D>(source.trigger.GetCollider(0));
            }
        }

        [Test]
        public void DisabledSubEmitterModuleDoesNotSuppressIndependentEmitter()
        {
            var parent = session.Emitters[0]; var child = session.Emitters[1];
            var emission = parent.emission; emission.rateOverTime = 0;
            var sub = parent.subEmitters;
            sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            sub.enabled = false;
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                for (int i = 0; i < 10; i++) preview.Tick(0.1f);
                Assert.Greater(preview.ParticleCount, 0);
            }
        }
    }
}
