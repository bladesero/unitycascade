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
        [Test]
        public void NativeInspectorUsesUnityEditorAndDoesNotActivateAuthoringObjects()
        {
            var emitter = session.Emitters[0];
            string before = EditorJsonUtility.ToJson(emitter);
            Assert.IsTrue(emitter.gameObject.activeSelf);
            Assert.IsFalse(emitter.gameObject.activeInHierarchy);
            Assert.IsTrue(session.IsActiveInDocument(emitter.transform));
            using (var inspector = new CascadeNativeInspector())
            {
                inspector.Bind(session, emitter);
                Assert.AreEqual("ParticleSystemInspector", inspector.ParticleEditor.GetType().Name);
                Assert.AreSame(emitter, inspector.ParticleEditor.target);
                Assert.AreSame(emitter.transform, inspector.TransformEditor.target);
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root); preview.Tick(0.1f);
                    Assert.IsTrue(preview.PreviewSystems[0].gameObject.activeInHierarchy);
                }
                Assert.IsFalse(inspector.ObserveChanges());
                Assert.AreEqual(before, EditorJsonUtility.ToJson(emitter));
            }
            Assert.IsFalse(session.Dirty);
        }

        [Test]
        public void NativeSerializedChangesSupportDirtyUndoRedoAndSave()
        {
            var source = session.Emitters[0];
            float before = source.main.startSpeed.constant;
            byte[] disk = File.ReadAllBytes(path);
            using (var inspector = new CascadeNativeInspector())
            {
                inspector.Bind(session, source);
                Undo.IncrementCurrentGroup();
                var so = inspector.ParticleEditor.serializedObject;
                so.Update(); so.FindProperty("InitialModule.startSpeed.scalar").floatValue = 7;
                so.ApplyModifiedProperties(); Undo.FlushUndoRecordObjects();
                Assert.IsTrue(inspector.ObserveChanges()); Assert.IsTrue(session.Dirty);
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
                Undo.PerformUndo(); Assert.AreEqual(before, source.main.startSpeed.constant);
                Assert.IsTrue(inspector.ObserveChanges());
                Undo.PerformRedo(); Assert.AreEqual(7, source.main.startSpeed.constant);
                Assert.IsTrue(inspector.ObserveChanges());
                inspector.Dispose(); // Native Editors must be released before replacing the loaded document.
                session.Save(); session.Reload();
            }
            Assert.AreEqual(7, session.Emitters[0].main.startSpeed.constant);
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<GameObject>(path).activeSelf);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(path).transform.parent);
        }

        [Test]
        public void NativeTransformChangesAreObservedWithoutTouchingPrefabUntilSave()
        {
            using (var inspector = new CascadeNativeInspector())
            {
                inspector.Bind(session, session.Emitters[0]);
                var so = inspector.TransformEditor.serializedObject;
                so.Update(); so.FindProperty("m_LocalPosition").vector3Value = new Vector3(4, 5, 6); so.ApplyModifiedProperties();
                Assert.IsTrue(inspector.ObserveChanges());
                Assert.AreEqual(new Vector3(4, 5, 6), session.Emitters[0].transform.localPosition);
            }
            Assert.IsTrue(session.Dirty);
        }

        [Test]
        public void NativeLinkedChangesCannotModifyNestedPrefabParameters()
        {
            string nestedPath = folder + "/NativeNested.prefab";
            PrefabUtility.SaveAsPrefabAsset(session.Emitters[1].gameObject, nestedPath);
            var child = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Emitters[0].transform);
            var nested = child.GetComponent<ParticleSystem>();
            session.MarkDirty(); session.Save();
            string before = EditorJsonUtility.ToJson(nested);
            using (var inspector = new CascadeNativeInspector())
            {
                inspector.Bind(session, session.Emitters[0]);
                // Unity's PlayOnAwakeChanged broadcasts to systems in the same effect hierarchy.
                using (var so = new SerializedObject(session.Emitters[0])) { so.FindProperty("playOnAwake").boolValue = true; so.ApplyModifiedProperties(); }
                using (var so = new SerializedObject(nested)) { so.FindProperty("playOnAwake").boolValue = true; so.ApplyModifiedProperties(); }
                bool changed = inspector.ObserveChanges();
                Assert.AreEqual(before, EditorJsonUtility.ToJson(nested));
                Assert.IsTrue(changed);
                Assert.IsTrue(session.Emitters[0].main.playOnAwake);
                Assert.IsTrue(session.Dirty);
            }
        }

        [Test]
        public void NativeReferenceEditsRejectSceneReferencesAndSubEmitterCycles()
        {
            var foreign = new GameObject("Outside document");
            try
            {
                using (var inspector = new CascadeNativeInspector())
                {
                    var emitter = session.Emitters[0]; inspector.Bind(session, emitter);
                    using (var so = new SerializedObject(emitter))
                    {
                        so.FindProperty("moveWithTransform").intValue = (int)ParticleSystemSimulationSpace.Custom;
                        so.FindProperty("moveWithCustomTransform").objectReferenceValue = foreign.transform;
                        so.ApplyModifiedProperties();
                    }
                    Assert.IsTrue(inspector.ObserveChanges()); // The valid switch to Custom space remains.
                    using (var so = new SerializedObject(emitter)) Assert.AreEqual(0, so.FindProperty("moveWithCustomTransform").objectReferenceInstanceIDValue);
                    using (var so = new SerializedObject(emitter))
                    {
                        var list = so.FindProperty("SubModule.subEmitters"); CascadeModules.AddSubEmitterRow(list);
                        so.FindProperty("SubModule.enabled").boolValue = true;
                        list.GetArrayElementAtIndex(0).FindPropertyRelative("emitter").objectReferenceValue = emitter;
                        so.ApplyModifiedProperties();
                    }
                    inspector.ObserveChanges();
                    Assert.IsFalse(emitter.subEmitters.GetSubEmitterSystem(0));
                    Assert.IsNotEmpty(inspector.Message);
                }
            }
            finally { Object.DestroyImmediate(foreign); }
        }

        [Test]
        public void NativeEditorsAreReusedAndDestroyedBeforeTargetsUnload()
        {
            int scenes = EditorSceneManager.previewSceneCount;
            var emitter = session.Emitters[0];
            var inspector = new CascadeNativeInspector();
            inspector.Bind(session, emitter);
            var first = inspector.ParticleEditor; var transform = inspector.TransformEditor;
            inspector.Bind(session, emitter); Assert.AreSame(first, inspector.ParticleEditor);
            inspector.Bind(session, session.Emitters[1]); Assert.IsFalse(first); Assert.IsFalse(transform);
            var second = inspector.ParticleEditor;
            inspector.Dispose(); Assert.IsFalse(second);
            Assert.AreEqual(scenes, EditorSceneManager.previewSceneCount);
            session.Reload(); // No native callback should retain the old unloaded objects.
        }

        [Test]
        public void NativeColumnsFlushChangesFromDifferentEmittersAndRefreshReferenceBaselines()
        {
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            var foreign = new GameObject("Outside column document");
            try
            {
                window.Show(); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                window.SendEvent(new Event { type = EventType.Layout });
                GameObject document;
                using (var so = new SerializedObject(window))
                    document = (GameObject)so.FindProperty("session").FindPropertyRelative("root").objectReferenceValue;
                var emitters = document.GetComponentsInChildren<ParticleSystem>(true);
                var hosts = Resources.FindObjectsOfTypeAll<Editor>().Where(e => e.GetType().Name == "ParticleSystemInspector" && emitters.Contains(e.target as ParticleSystem)).ToArray();
                Assert.AreEqual(2, hosts.Length);
                foreach (var host in hosts)
                {
                    var so = host.serializedObject;
                    so.Update(); so.FindProperty("InitialModule.startSpeed.scalar").floatValue = host.target == emitters[0] ? 3 : 7;
                    so.FindProperty("moveWithTransform").intValue = (int)ParticleSystemSimulationSpace.Custom;
                    so.FindProperty("moveWithCustomTransform").objectReferenceValue = document.transform;
                    so.ApplyModifiedProperties();
                }
                window.SaveChanges();
                // The second column commits a popup change after the first document baseline was saved.
                using (var so = new SerializedObject(emitters[1]))
                {
                    so.FindProperty("moveWithCustomTransform").objectReferenceValue = foreign.transform;
                    so.FindProperty("InitialModule.startSpeed.scalar").floatValue = 9;
                    so.ApplyModifiedProperties();
                }
                window.SaveChanges();
                Assert.AreEqual(document.transform, emitters[1].main.customSimulationSpace);
                Assert.IsFalse(window.hasUnsavedChanges);
                var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var savedEmitters = saved.GetComponentsInChildren<ParticleSystem>(true);
                Assert.AreEqual(3, savedEmitters[0].main.startSpeed.constant);
                Assert.AreEqual(9, savedEmitters[1].main.startSpeed.constant);
                Assert.AreEqual(saved.transform, savedEmitters[1].main.customSimulationSpace);
            }
            finally { window.DiscardChanges(); window.Close(); Object.DestroyImmediate(foreign); }
        }

        [Test]
        public void NativeEmitterColumnsDrawWithoutMutationOrLeakedEditors()
        {
            byte[] disk = File.ReadAllBytes(path);
            var errors = new List<string>();
            Application.LogCallback callback = (condition, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(condition); };
            int editors = Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector");
            int scenes = EditorSceneManager.previewSceneCount;
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            Application.logMessageReceived += callback;
            try
            {
                window.Show(); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                foreach (bool expanded in new[] { true, false, true })
                {
                    using (var so = new SerializedObject(window)) { so.FindProperty("performanceExpanded").boolValue = expanded; so.ApplyModifiedPropertiesWithoutUndo(); }
                    window.SendEvent(new Event { type = EventType.Layout });
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) window.SendEvent(new Event { type = EventType.Repaint });
                }
                Assert.AreEqual(editors + 2, Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector"), "One native Inspector per emitter column, including offscreen columns.");
                var nativeEditors = Resources.FindObjectsOfTypeAll<Editor>().Where(e => e.GetType().Name == "ParticleSystemInspector").ToArray();
                window.SendEvent(new Event { type = EventType.Layout });
                CollectionAssert.AreEquivalent(nativeEditors, Resources.FindObjectsOfTypeAll<Editor>().Where(e => e.GetType().Name == "ParticleSystemInspector").ToArray());
                Assert.IsTrue(window.CanOpenPrefabDrop(new Vector2(20, 10)));
                Assert.IsFalse(window.CanOpenPrefabDrop(new Vector2(window.position.width - 30, 180)), "Native object fields must handle their own drops.");
                window.SaveChanges(); // Also flush delayed native edits; an untouched Inspector must stay clean.
                Assert.IsFalse(window.hasUnsavedChanges);
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            }
            finally { window.DiscardChanges(); window.Close(); Application.logMessageReceived -= callback; }
            Assert.IsEmpty(errors, string.Join("\n", errors));
            Assert.AreEqual(editors, Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector"));
            Assert.AreEqual(scenes, EditorSceneManager.previewSceneCount);
        }
    }
}
