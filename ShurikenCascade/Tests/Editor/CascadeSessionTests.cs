using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        string folder;
        string path;
        CascadeSession session;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/CascadeTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            path = folder + "/Effect.prefab";
            var root = new GameObject("Effect");
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("SameName");
                go.transform.SetParent(root.transform, false);
                var p = go.AddComponent<ParticleSystem>();
                var main = p.main;
                main.startLifetime = 2;
                main.startSpeed = 1;
                main.loop = true;
                main.playOnAwake = false;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            session = new CascadeSession();
            session.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        [TearDown]
        public void TearDown()
        {
            session?.Close();
            AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void OpenCloseWithoutChangesLeavesAssetUntouched()
        {
            var before = File.ReadAllBytes(path);
            session.Save(); session.Close();
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }

        [Test]
        public void RoundTripPreservesCurvesGradientsBurstsAndSameNameIdentities()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            long[] ids = asset.GetComponentsInChildren<ParticleSystem>().Select(LocalId).ToArray();
            var p = session.Emitters[1];
            var main = p.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2, AnimationCurve.Linear(0, 1, 1, 3));
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.blue, 1) }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            main.startColor = new ParticleSystem.MinMaxGradient(gradient);
            var emission = p.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0.2f, 5) });
            var force = p.forceOverLifetime;
            force.enabled = true; force.x = 3;
            var sub = session.Emitters[0].subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(p, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            session.MarkDirty(); session.Save(); session.Reload();
            p = session.Emitters[1];
            Assert.AreEqual(ParticleSystemCurveMode.Curve, p.main.startLifetime.mode);
            Assert.AreEqual(3, p.main.startLifetime.curve.Evaluate(1), 0.0001);
            Assert.AreEqual(Color.blue, p.main.startColor.gradient.colorKeys[1].color);
            Assert.AreEqual(1, p.emission.burstCount);
            Assert.AreEqual(5, p.emission.GetBurst(0).count.constant);
            Assert.IsTrue(p.forceOverLifetime.enabled);
            Assert.AreEqual(3, p.forceOverLifetime.x.constant);
            Assert.AreEqual(p, session.Emitters[0].subEmitters.GetSubEmitterSystem(0));
            CollectionAssert.AreEqual(ids, AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>().Select(LocalId).ToArray());
        }

        [Test]
        public void RenamePreservesExistingLocalIds()
        {
            long id = LocalId(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>()[1]);
            session.Emitters[1].name = "Renamed";
            session.MarkDirty(); session.Save();
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.AreEqual(id, LocalId(asset.GetComponentsInChildren<ParticleSystem>()[1]));
        }

        [Test]
        public void StructuralUndoAndDeleteReferences()
        {
            var p = session.Add();
            Undo.FlushUndoRecordObjects();
            Assert.AreEqual(3, session.Emitters.Length);
            Undo.PerformUndo(); Assert.AreEqual(2, session.Emitters.Length);
            Undo.PerformRedo(); Assert.AreEqual(3, session.Emitters.Length);
            p = session.Emitters.Last();
            var sub = session.Emitters[0].subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(p, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            session.Delete(p); Undo.FlushUndoRecordObjects();
            Assert.AreEqual(0, session.Emitters[0].subEmitters.subEmittersCount);
            Undo.PerformUndo();
            Assert.AreEqual(3, session.Emitters.Length);
            Assert.AreEqual(session.Emitters.Last(), session.Emitters[0].subEmitters.GetSubEmitterSystem(0));
        }

        [Test]
        public void DuplicateRemapsInternalSubEmitterReferences()
        {
            var original = session.Emitters[0];
            var child = new GameObject("Child"); child.transform.SetParent(original.transform, false);
            var subParticle = child.AddComponent<ParticleSystem>();
            var sub = original.subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(subParticle, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            var duplicate = session.Duplicate(original);
            Assert.AreNotEqual(original.name, duplicate.name);
            Assert.AreEqual(duplicate.transform.GetChild(0).GetComponent<ParticleSystem>(), duplicate.subEmitters.GetSubEmitterSystem(0));
        }

        [Test]
        public void ExternalChangesBlockSaveAndKeepEdits()
        {
            session.Emitters[0].name = "Unsaved"; session.MarkDirty();
            File.AppendAllText(path, "\n");
            Assert.Throws<InvalidOperationException>(() => session.Save());
            Assert.IsTrue(session.Dirty);
            Assert.AreEqual("Unsaved", session.Emitters[0].name);
        }

        [Test]
        public void PreviewDoesNotMutateDocumentAndActuallySimulates()
        {
            string before = EditorJsonUtility.ToJson(session.Emitters[0]);
            int scenes = EditorSceneManager.previewSceneCount;
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                Assert.IsNull(preview.Error);
                for (int i = 0; i < 15; i++) preview.Tick(0.1f);
                Assert.Greater(preview.ParticleCount, 0);
                preview.Hidden.Add("0"); preview.Solo = "1"; preview.ApplyVisibility();
                preview.Playing = false; float time = preview.Time; preview.Tick(0.1f);
                Assert.AreEqual(time, preview.Time);
                preview.Restart(); Assert.AreEqual(0, preview.Time);
                Assert.AreEqual(before, EditorJsonUtility.ToJson(session.Emitters[0]));
                Assert.IsTrue(session.Emitters[0].GetComponent<ParticleSystemRenderer>().enabled);
                Assert.IsFalse(session.Dirty);
            }
            Assert.AreEqual(scenes, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void ModuleMappingsExistInThisUnityVersion()
        {
            using (var so = new SerializedObject(session.Emitters[0]))
                foreach (var module in CascadeModules.All.Where(m => !m.Path.StartsWith("$")))
                    Assert.IsNotNull(so.FindProperty(module.Path), module.Path);
        }

        [Test]
        public void DiskRecoveryRestoresHierarchyAndReferences()
        {
            long[] ids = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>().Select(LocalId).ToArray();
            var sub = session.Emitters[0].subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(session.Emitters[1], ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            session.Emitters[0].name = "Recovered";
            session.MarkDirty(); session.WriteRecovery();
            EditorSceneManager.ClosePreviewScene(session.Root.scene);
            Assert.IsTrue(session.RestoreRecovery());
            Assert.AreEqual("Recovered", session.Emitters[0].name);
            Assert.AreEqual(session.Emitters[1], session.Emitters[0].subEmitters.GetSubEmitterSystem(0));
            session.Save(); session.Reload();
            Assert.AreEqual("Recovered", session.Emitters[0].name);
            CollectionAssert.AreEqual(ids, AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>().Select(LocalId).ToArray());
        }

        [Test]
        public void NestedPrefabSubtreeIsReadOnlyAndVariantRejected()
        {
            string nestedPath = folder + "/Nested.prefab";
            PrefabUtility.SaveAsPrefabAsset(session.Emitters[0].gameObject, nestedPath);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
            var emitter = nested.GetComponent<ParticleSystem>();
            Assert.IsTrue(session.IsReadOnly(emitter));
            Assert.IsFalse(session.CanChangeSubtree(emitter));
            nested.transform.SetParent(session.Emitters[0].transform, false);
            Assert.IsFalse(session.CanChangeSubtree(session.Emitters[0]));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            string variantPath = folder + "/Variant.prefab";
            PrefabUtility.SaveAsPrefabAsset(instance, variantPath); Object.DestroyImmediate(instance);
            Assert.IsNotNull(CascadeSession.ValidateAsset(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath)));
        }

        static long LocalId(Object obj)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id);
            return id;
        }

        static CascadeModules.Module Module(string path) => CascadeModules.All.First(m => m.Path == path);

        [Test]
        public void ShapePanelShowsOnlyRelevantGeometryAndPreservesInactiveValues()
        {
            var p = session.Emitters[0];
            var shape = p.shape;
            shape.shapeType = ParticleSystemShapeType.ConeVolume;
            shape.length = 7;
            using (var so = new SerializedObject(p))
            {
                CollectionAssert.Contains(CascadeModules.ShapeFields(so), "length");
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "m_Mesh");
                shape.shapeType = ParticleSystemShapeType.Sphere; so.Update();
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "length");
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "angle");
                CollectionAssert.Contains(CascadeModules.ShapeFields(so), "radius.value");
                shape.shapeType = ParticleSystemShapeType.Box; so.Update();
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "radius.value");
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "boxThickness");
                shape.shapeType = ParticleSystemShapeType.BoxShell; so.Update();
                CollectionAssert.Contains(CascadeModules.ShapeFields(so), "boxThickness");
                shape.shapeType = ParticleSystemShapeType.Mesh; so.Update();
                CollectionAssert.Contains(CascadeModules.ShapeFields(so), "m_Mesh");
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "m_SkinnedMeshRenderer");
                CollectionAssert.DoesNotContain(CascadeModules.ShapeFields(so), "m_MeshMaterialIndex");
                shape.useMeshMaterialIndex = true; so.Update();
                CollectionAssert.Contains(CascadeModules.ShapeFields(so), "m_MeshMaterialIndex");
                shape.shapeType = ParticleSystemShapeType.ConeVolume;
                Assert.AreEqual(7, shape.length);
            }
        }

        [Test]
        public void ConditionalFieldsFollowAxisRemapLoopAndAnimationModes()
        {
            using (var so = new SerializedObject(session.Emitters[0]))
            {
                so.FindProperty("InitialModule.size3D").boolValue = false;
                so.FindProperty("InitialModule.startSizeY.scalar").floatValue = 17;
                Assert.IsFalse(CascadeModules.IsVisible(so, "InitialModule.startSizeY"));
                so.FindProperty("InitialModule.size3D").boolValue = true;
                Assert.IsTrue(CascadeModules.IsVisible(so, "InitialModule.startSizeY"));
                Assert.AreEqual(17, so.FindProperty("InitialModule.startSizeY.scalar").floatValue);
                so.FindProperty("NoiseModule.remapEnabled").boolValue = false;
                so.FindProperty("NoiseModule.separateAxes").boolValue = true;
                Assert.IsFalse(CascadeModules.IsVisible(so, "NoiseModule.remapY"));
                so.FindProperty("NoiseModule.remapEnabled").boolValue = true;
                Assert.IsTrue(CascadeModules.IsVisible(so, "NoiseModule.remapY"));
                so.FindProperty("looping").boolValue = false;
                Assert.IsFalse(CascadeModules.IsVisible(so, "prewarm"));
                so.FindProperty("autoRandomSeed").boolValue = true;
                Assert.IsFalse(CascadeModules.IsVisible(so, "randomSeed"));
                so.FindProperty("moveWithTransform").intValue = (int)ParticleSystemSimulationSpace.World;
                Assert.IsFalse(CascadeModules.IsVisible(so, "moveWithCustomTransform"));
                so.FindProperty("UVModule.mode").intValue = (int)ParticleSystemAnimationMode.Sprites;
                Assert.IsFalse(CascadeModules.IsVisible(so, "UVModule.tilesX"));
                Assert.IsTrue(CascadeModules.IsVisible(so, "UVModule.sprites"));
                so.FindProperty("UVModule.timeMode").intValue = (int)ParticleSystemAnimationTimeMode.FPS;
                Assert.IsTrue(CascadeModules.IsVisible(so, "UVModule.fps"));
                Assert.IsFalse(CascadeModules.IsVisible(so, "UVModule.frameOverTime"));
            }
        }

        [TestCase("ColorModule")]
        [TestCase("SizeModule")]
        [TestCase("NoiseModule")]
        [TestCase("EmissionModule")]
        public void ModulePasteIsIsolatedSnapshotAndOneUndo(string path)
        {
            var source = session.Emitters[0]; var target = session.Emitters[1];
            var color = source.colorOverLifetime; color.enabled = true; color.color = new ParticleSystem.MinMaxGradient(Color.red, Color.blue);
            var size = source.sizeOverLifetime; size.enabled = true; size.separateAxes = true; size.y = new ParticleSystem.MinMaxCurve(3, AnimationCurve.Linear(0, 1, 1, 2));
            var noise = source.noise; noise.enabled = true; noise.strength = new ParticleSystem.MinMaxCurve(1, 4); noise.remapEnabled = true;
            var emission = source.emission; emission.SetBursts(new[] { new ParticleSystem.Burst(0.5f, 17) { repeatInterval = 0.25f } });
            string before = EditorJsonUtility.ToJson(target);
            var unaffected = new Dictionary<string, string>();
            using (var so = new SerializedObject(target))
                foreach (var other in CascadeModules.All.Where(m => !m.Path.StartsWith("$") && m.Path != path))
                    unaffected[other.Path] = PropertySnapshot(so.FindProperty(other.Path));
            string expected;
            using (var so = new SerializedObject(source)) expected = PropertySnapshot(so.FindProperty(path));
            using (var clipboard = new CascadeModuleClipboard())
            {
                clipboard.Copy(source, Module(path));
                // Copy is a snapshot, not a live pointer to the source emitter.
                color.color = Color.green; size.y = 99; noise.strength = 99; emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
                clipboard.Paste(session, target, Module(path));
                Undo.FlushUndoRecordObjects();
                using (var so = new SerializedObject(target)) Assert.AreEqual(expected, PropertySnapshot(so.FindProperty(path)));
                string after = EditorJsonUtility.ToJson(target);
                Assert.AreNotEqual(before, after);
                Assert.IsTrue(session.Dirty);
                Undo.PerformUndo(); Assert.AreEqual(before, EditorJsonUtility.ToJson(target));
                Undo.PerformRedo(); Assert.AreEqual(after, EditorJsonUtility.ToJson(target));
                using (var so = new SerializedObject(target))
                    foreach (var other in unaffected) Assert.AreEqual(other.Value, PropertySnapshot(so.FindProperty(other.Key)), other.Key);
            }
        }

        static string PropertySnapshot(SerializedProperty property)
        {
            // Native property content comparison, including the MinMaxCurve/Gradient child fields.
            var result = new System.Text.StringBuilder();
            var it = property.Copy(); var end = it.GetEndProperty();
            bool enterChildren = true;
            while (it.Next(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                // Reference identity is asserted separately after reload; native instance IDs change.
                enterChildren = it.propertyType != SerializedPropertyType.ObjectReference;
                result.Append(it.propertyPath).Append(':');
                switch (it.propertyType)
                {
                    case SerializedPropertyType.Float: result.Append(it.doubleValue); break;
                    case SerializedPropertyType.Integer: case SerializedPropertyType.Enum: result.Append(it.intValue); break;
                    case SerializedPropertyType.Boolean: result.Append(it.boolValue); break;
                    case SerializedPropertyType.Color: result.Append(it.colorValue); break;
                    case SerializedPropertyType.Vector2: result.Append(it.vector2Value); break;
                    case SerializedPropertyType.String: result.Append(it.stringValue); break;
                    case SerializedPropertyType.AnimationCurve:
                        foreach (var key in it.animationCurveValue.keys) result.Append(key.time).Append(',').Append(key.value).Append(',').Append(key.inTangent).Append(',').Append(key.outTangent);
                        break;
                    case SerializedPropertyType.Gradient:
                        foreach (var key in it.gradientValue.colorKeys) result.Append(key.time).Append(key.color);
                        foreach (var key in it.gradientValue.alphaKeys) result.Append(key.time).Append(key.alpha);
                        break;
                }
            }
            return result.ToString();
        }

        [Test]
        public void ClipboardRejectsWrongModuleAndNestedTargetWithoutMutation()
        {
            using (var clipboard = new CascadeModuleClipboard())
            {
                clipboard.Copy(session.Emitters[0], Module("NoiseModule"));
                string before = EditorJsonUtility.ToJson(session.Emitters[1]);
                Assert.Throws<InvalidOperationException>(() => clipboard.Paste(session, session.Emitters[1], Module("SizeModule")));
                Assert.AreEqual(before, EditorJsonUtility.ToJson(session.Emitters[1]));
                string nestedPath = folder + "/Nested.prefab";
                PrefabUtility.SaveAsPrefabAsset(session.Emitters[0].gameObject, nestedPath);
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
                Assert.IsFalse(clipboard.CanPaste(session, nested.GetComponent<ParticleSystem>(), Module("NoiseModule"), out _));
            }
        }

        [Test]
        public void ClipboardRemapsSelfReferencesAndRejectsForeignDocumentReferences()
        {
            var source = session.Emitters[0]; var main = source.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom; main.customSimulationSpace = source.transform;
            using (var clipboard = new CascadeModuleClipboard())
            {
                clipboard.Copy(source, Module("InitialModule"));
                clipboard.Paste(session, session.Emitters[1], Module("InitialModule"));
                Assert.AreEqual(session.Emitters[1].transform, session.Emitters[1].main.customSimulationSpace);
                main.customSimulationSpace = session.Root.transform;
                clipboard.Copy(source, Module("InitialModule"));
                var other = new CascadeSession();
                try
                {
                    other.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    string before = EditorJsonUtility.ToJson(other.Emitters[0]);
                    Assert.Throws<InvalidOperationException>(() => clipboard.Paste(other, other.Emitters[0], Module("InitialModule")));
                    Assert.AreEqual(before, EditorJsonUtility.ToJson(other.Emitters[0]));
                }
                finally { other.Close(); }
            }
        }

        [Test]
        public void ClipboardSurvivesSourceDeletionAndReleasesItsPreviewScene()
        {
            int count = EditorSceneManager.previewSceneCount;
            using (var clipboard = new CascadeModuleClipboard())
            {
                var color = session.Emitters[0].colorOverLifetime; color.enabled = true; color.color = Color.cyan;
                clipboard.Copy(session.Emitters[0], Module("ColorModule"));
                session.Delete(session.Emitters[0]);
                clipboard.Paste(session, session.Emitters[0], Module("ColorModule"));
                Assert.AreEqual(Color.cyan, session.Emitters[0].colorOverLifetime.color.color);
            }
            Assert.AreEqual(count, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void BurstRemovalPreservesOtherRowsAndSupportsUndo()
        {
            var p = session.Emitters[0]; var emission = p.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0.2f, 5), new ParticleSystem.Burst(0.8f, 12) });
            using (var so = new SerializedObject(p))
            {
                Undo.IncrementCurrentGroup();
                CascadeModules.RemoveBurst(so.FindProperty("EmissionModule.m_Bursts"), 0);
                so.ApplyModifiedProperties();
            }
            Undo.FlushUndoRecordObjects();
            Assert.AreEqual(1, p.emission.burstCount);
            Assert.AreEqual(12, p.emission.GetBurst(0).count.constant);
            Undo.PerformUndo();
            Assert.AreEqual(2, p.emission.burstCount);
            Assert.AreEqual(5, p.emission.GetBurst(0).count.constant);
        }

        [TestCase("$transform")]
        [TestCase("$renderer")]
        public void ComponentPastePreservesIdentityAndAssetReferences(string modulePath)
        {
            var source = session.Emitters[0]; var target = session.Emitters[1];
            source.transform.localPosition = new Vector3(3, 2, 1);
            var material = new Material(Shader.Find("Particles/Standard Unlit"));
            AssetDatabase.CreateAsset(material, folder + "/Particle.mat");
            source.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            string materialBefore = EditorJsonUtility.ToJson(material);
            var parent = target.transform.parent; string name = target.name;
            using (var clipboard = new CascadeModuleClipboard())
            {
                clipboard.Copy(source, Module(modulePath));
                clipboard.Paste(session, target, Module(modulePath));
                Assert.AreEqual(name, target.name);
                Assert.AreEqual(parent, target.transform.parent);
                if (modulePath == "$transform") Assert.AreEqual(source.transform.localPosition, target.transform.localPosition);
                else Assert.AreEqual(material, target.GetComponent<ParticleSystemRenderer>().sharedMaterial);
                Assert.AreEqual(materialBefore, EditorJsonUtility.ToJson(material));
            }
        }

        [Test]
        public void DeletedReferencedObjectBlocksPasteWithoutChangingTarget()
        {
            var source = session.Emitters[0]; var main = source.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = session.Emitters[1].transform;
            using (var clipboard = new CascadeModuleClipboard())
            {
                clipboard.Copy(source, Module("InitialModule"));
                session.Delete(session.Emitters[1]);
                string before = EditorJsonUtility.ToJson(source);
                Assert.IsFalse(clipboard.CanPaste(session, source, Module("InitialModule"), out _));
                Assert.Throws<InvalidOperationException>(() => clipboard.Paste(session, source, Module("InitialModule")));
                Assert.AreEqual(before, EditorJsonUtility.ToJson(source));
            }
        }

        [Test]
        public void BurstArrayEditingUsesValidDefaultsAndCount()
        {
            var p = session.Emitters[0];
            using (var so = new SerializedObject(p))
            {
                CascadeModules.ResizeArray(so.FindProperty("EmissionModule.m_Bursts"), 2);
                so.ApplyModifiedProperties();
            }
            Assert.AreEqual(2, p.emission.burstCount);
            Assert.AreEqual(30, p.emission.GetBurst(0).count.constant);
            Assert.AreEqual(1, p.emission.GetBurst(0).probability);
            Assert.AreEqual(1, p.emission.GetBurst(0).cycleCount);
            session.MarkDirty(); session.Save(); session.Reload();
            Assert.AreEqual(2, session.Emitters[0].emission.burstCount);
        }

        [Test]
        public void RecoveryPreservesAddedDeletedAndRepeatedNestedNodes()
        {
            string nestedPath = folder + "/Nested.prefab";
            PrefabUtility.SaveAsPrefabAsset(session.Emitters[0].gameObject, nestedPath);
            for (int i = 0; i < 2; i++)
                PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
            session.MarkDirty(); session.Save(); session.Reload();
            long survivorId = LocalId(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>()[1]);
            session.Delete(session.Emitters[0]);
            session.Emitters[0].name = "Survivor";
            session.Add().name = "Added";
            session.MarkDirty(); session.WriteRecovery();
            EditorSceneManager.ClosePreviewScene(session.Root.scene);
            Assert.IsTrue(session.RestoreRecovery());
            Assert.AreEqual(4, session.Emitters.Length);
            Assert.AreEqual(2, session.Emitters.Count(session.IsReadOnly));
            Assert.AreEqual("Survivor", session.Emitters[0].name);
            Assert.AreEqual("Added", session.Emitters.Last().name);
            session.Save(); session.Reload();
            Assert.AreEqual(survivorId, LocalId(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>()[0]));
            Assert.AreEqual(2, session.Emitters.Count(session.IsReadOnly));
        }

        [Test]
        public void SubEmitterIsTriggeredOnlyByItsParent()
        {
            var parent = session.Emitters[0];
            var child = session.Emitters[1];
            child.transform.SetParent(parent.transform, false);
            var emission = parent.emission; emission.rateOverTime = 0;
            var sub = parent.subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                for (int i = 0; i < 10; i++) preview.Tick(0.1f);
                Assert.AreEqual(0, preview.ParticleCount, "Child must not emit independently.");
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var childEmission = child.emission;
                childEmission.rateOverTime = 0;
                childEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 5) });
                preview.Rebuild(session.Root);
                preview.Tick(0.1f);
                Assert.GreaterOrEqual(preview.ParticleCount, 6, "Parent event must simulate the sub-emitter exactly once.");
                Assert.AreEqual(6, preview.ParticleCount);
            }
        }

        [Test]
        public void NativeColumnsAndModuleConfigurationsDrawWithoutExceptions()
        {
            ConfigureAdvancedModules(session.Emitters[0]);
            var emission = session.Emitters[0].emission;
            emission.SetBursts(new[] {
                new ParticleSystem.Burst(0, new ParticleSystem.MinMaxCurve(30)),
                new ParticleSystem.Burst(0.2f, new ParticleSystem.MinMaxCurve(5, 15)),
                new ParticleSystem.Burst(0.4f, new ParticleSystem.MinMaxCurve(10, AnimationCurve.Constant(0, 1, 1))),
                new ParticleSystem.Burst(0.6f, new ParticleSystem.MinMaxCurve(10, AnimationCurve.Constant(0, 1, 1), AnimationCurve.Constant(0, 1, 2)))
            });
            session.MarkDirty(); session.Save();
            var errors = new List<string>();
            Application.LogCallback callback = (condition, stack, type) => { if (type == LogType.Exception || type == LogType.Error) errors.Add(condition); };
            Application.logMessageReceived += callback;
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.position = new Rect(0, 0, 1200, 800);
                window.Show();
                window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                for (int i = 0; i < CascadeModules.All.Length; i++)
                {
                    using (var so = new SerializedObject(window)) { so.FindProperty("moduleIndex").intValue = i; so.ApplyModifiedPropertiesWithoutUndo(); }
                    window.SendEvent(new Event { type = EventType.Layout });
                    // Without a graphics device the utility cannot provide a RenderTexture.
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                        window.SendEvent(new Event { type = EventType.Repaint });
                }
                // Exercise conditional branches with populated references and both custom stream types.
                using (var so = new SerializedObject(window))
                {
                    var particle = (ParticleSystem)so.FindProperty("selected").objectReferenceValue;
                    string[] modes = { "CollisionModule.type", "CollisionModule.collisionMode", "ExternalForcesModule.influenceFilter", "CustomDataModule.mode0", "CustomDataModule.mode1" };
                    foreach (string modePath in modes)
                    {
                        string modulePath = modePath.Substring(0, modePath.IndexOf('.'));
                        so.FindProperty("moduleIndex").intValue = Array.FindIndex(CascadeModules.All, m => m.Path == modulePath);
                        so.ApplyModifiedPropertiesWithoutUndo();
                        for (int mode = 0; mode < (modulePath == "CollisionModule" ? 2 : 3); mode++)
                        {
                            using (var ps = new SerializedObject(particle)) { ps.FindProperty(modePath).intValue = mode; ps.ApplyModifiedPropertiesWithoutUndo(); }
                            string before = EditorJsonUtility.ToJson(particle);
                            window.SendEvent(new Event { type = EventType.Layout });
                            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) window.SendEvent(new Event { type = EventType.Repaint });
                            Assert.AreEqual(before, EditorJsonUtility.ToJson(particle), modePath + "=" + mode);
                        }
                    }
                }
                using (var so = new SerializedObject(window))
                {
                    so.FindProperty("moduleIndex").intValue = Array.FindIndex(CascadeModules.All, m => m.Path == "ShapeModule");
                    so.ApplyModifiedPropertiesWithoutUndo();
                    var particle = (ParticleSystem)so.FindProperty("selected").objectReferenceValue;
                    var shape = particle.shape;
                    foreach (ParticleSystemShapeType shapeType in Enum.GetValues(typeof(ParticleSystemShapeType)))
                    {
                        shape.shapeType = shapeType;
                        string before = EditorJsonUtility.ToJson(particle);
                        window.SendEvent(new Event { type = EventType.Layout });
                        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) window.SendEvent(new Event { type = EventType.Repaint });
                        Assert.AreEqual(before, EditorJsonUtility.ToJson(particle), shapeType.ToString());
                    }
                }
                Assert.IsEmpty(errors, string.Join("\n", errors));
                Assert.IsFalse(window.hasUnsavedChanges, "Drawing a module must not modify the prefab.");
            }
            finally { window.DiscardChanges(); window.Close(); Application.logMessageReceived -= callback; }
        }

        [Test]
        public void PreviewRendersVisibleParticles()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Graphics device required.");
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var material = pipeline && pipeline.defaultParticleMaterial
                ? new Material(pipeline.defaultParticleMaterial)
                : new Material(Shader.Find(pipeline ? "Universal Render Pipeline/Particles/Unlit" : "Particles/Standard Unlit"));
            try
            {
                foreach (var p in session.Emitters) p.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root);
                    for (int i = 0; i < 15; i++) preview.Tick(0.1f);
                    preview.Frame();
                    var texture = preview.RenderSnapshot(640, 480);
                    try
                    {
                        Assert.IsNotNull(texture);
                        Color[] pixels = texture.GetPixels();
                        Color background = pixels[0];
                        Assert.Greater(pixels.Count(c => Mathf.Abs(c.r - background.r) + Mathf.Abs(c.g - background.g) + Mathf.Abs(c.b - background.b) > 0.1f), 100);
                        Assert.Less(pixels.Count(c => c.r > 0.9f && c.b > 0.9f && c.g < 0.1f), 100, "Pink error-shader pixels are not a successful particle render.");
                        File.WriteAllBytes("preview-validation.png", texture.EncodeToPNG());
                    }
                    finally { Object.DestroyImmediate(texture); }
                }
            }
            finally { Object.DestroyImmediate(material); }
        }

        // Also usable in a minimal offline validation project, without modifying the user's open scene.
        public static void RunBatch()
        {
            var results = new List<string>();
            foreach (var method in typeof(CascadeSessionTests).GetMethods().Where(m => Attribute.IsDefined(m, typeof(TestAttribute))))
            {
                var suite = new CascadeSessionTests();
                try { suite.SetUp(); method.Invoke(suite, null); results.Add("PASS " + method.Name); }
                catch (Exception ex)
                {
                    var cause = ex.InnerException ?? ex;
                    results.Add((cause is IgnoreException ? "SKIP " : "FAIL ") + method.Name + "\n" + cause);
                }
                finally { suite.TearDown(); }
            }
            File.WriteAllLines("validation-results.txt", results);
            Debug.Log(string.Join("\n", results));
            EditorApplication.Exit(results.Any(r => r.StartsWith("FAIL")) ? 1 : 0);
        }
    }
}
