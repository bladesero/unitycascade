using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        [Test]
        public void BatchDeleteDeduplicatesSubtreesAndRestoresReferencesInOneUndo()
        {
            var parent = session.Emitters[0]; var survivor = session.Emitters[1];
            var node = new GameObject("SameName"); node.transform.SetParent(parent.transform, false);
            var child = node.AddComponent<ParticleSystem>();
            var separate = session.Add();
            var subs = survivor.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            subs.AddSubEmitter(separate, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            session.MarkDirty(); session.Save();
            byte[] disk = File.ReadAllBytes(path);
            session.DeleteMany(new[] { parent, child, separate, parent });
            Assert.AreEqual(1, session.Emitters.Length);
            Assert.AreEqual(0, survivor.subEmitters.subEmittersCount);
            CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            Undo.PerformUndo();
            Assert.AreEqual(4, session.Emitters.Length);
            Assert.AreEqual(2, survivor.subEmitters.subEmittersCount);
            Assert.IsTrue(survivor.subEmitters.GetSubEmitterSystem(0).transform.IsChildOf(session.Emitters[0].transform));
            Undo.PerformRedo();
            Assert.AreEqual(1, session.Emitters.Length);
            Assert.AreEqual(0, survivor.subEmitters.subEmittersCount);
            session.Save(); session.Reload(); Assert.AreEqual(1, session.Emitters.Length);
        }

        [Test]
        public void BatchDeleteRejectsEntireSelectionWhenAnySubtreeIsProtected()
        {
            string nestedPath = folder + "/Nested.prefab";
            PrefabUtility.SaveAsPrefabAsset(session.Emitters[1].gameObject, nestedPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
            var nested = instance.GetComponent<ParticleSystem>();
            session.MarkDirty(); session.Save();
            var before = session.Emitters;
            Assert.Throws<InvalidOperationException>(() => session.DeleteMany(new[] { before[0], nested }));
            CollectionAssert.AreEqual(before, session.Emitters); Assert.IsFalse(session.Dirty);
            var subs = nested.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(before[1], ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            session.MarkDirty(); session.Save();
            Assert.Throws<InvalidOperationException>(() => session.DeleteMany(new[] { before[0], before[1] }));
            CollectionAssert.AreEqual(before, session.Emitters); Assert.IsFalse(session.Dirty);
        }

        [Test]
        public void ShiftSelectionUsesColumnOrderAndBatchDeleteSelectsSurvivor()
        {
            session.Add(); session.Add(); session.Save();
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                GameObject root;
                using (var so = new SerializedObject(window)) root = (GameObject)so.FindProperty("session").FindPropertyRelative("root").objectReferenceValue;
                var items = root.GetComponentsInChildren<ParticleSystem>(true);
                window.SelectEmitter(items[3], false);
                window.SelectEmitter(items[1], true);
                CollectionAssert.AreEqual(new[] { items[1], items[2], items[3] }, window.SelectedEmitters);
                window.SelectEmitter(items[2], true);
                CollectionAssert.AreEqual(new[] { items[2], items[3] }, window.SelectedEmitters);
                Assert.IsFalse(window.hasUnsavedChanges);
                window.DeleteSelectedEmitters();
                Assert.AreEqual(2, root.GetComponentsInChildren<ParticleSystem>(true).Length);
                Assert.AreEqual(1, window.SelectedEmitters.Count);
                Assert.IsTrue(window.SelectedEmitters[0]); Assert.IsTrue(window.hasUnsavedChanges);
                Undo.PerformUndo(); Assert.AreEqual(4, root.GetComponentsInChildren<ParticleSystem>(true).Length);
                DrawTestFrame(window);
            }
            finally { window.DiscardChanges(); window.Close(); }
        }

        [Test]
        public void CheckboxesAddRemoveAndKeepEmptySelectionWithoutDirtying()
        {
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.position = new Rect(0, 0, 1500, 1000);
                window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path)); DrawTestFrame(window);
                void ClickCheckbox(int column)
                {
                    Vector2 point = new Vector2(window.PanelLayout.Columns.x + column * 404 + 14, window.PanelLayout.Columns.y + 34);
                    point += window.GuiScreenOrigin - window.position.position;
                    window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = point });
                    window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = point });
                    DrawTestFrame(window);
                }
                Assert.AreEqual(1, window.SelectedEmitters.Count);
                ClickCheckbox(1); Assert.AreEqual(2, window.SelectedEmitters.Count);
                ClickCheckbox(0); Assert.AreEqual(1, window.SelectedEmitters.Count);
                ClickCheckbox(1); Assert.AreEqual(0, window.SelectedEmitters.Count);
                DrawTestFrame(window); Assert.AreEqual(0, window.SelectedEmitters.Count);
                window.DeleteSelectedEmitters(); Assert.AreEqual(0, window.SelectedEmitters.Count);
                Assert.IsFalse(window.hasUnsavedChanges);
                ClickCheckbox(0); Assert.AreEqual(1, window.SelectedEmitters.Count);
            }
            finally { window.DiscardChanges(); window.Close(); }
        }

        [Test]
        public void ShiftColumnClickThenDeleteKeyRemovesRangeWithoutConfirmation()
        {
            session.Add(); session.Save();
            byte[] disk = File.ReadAllBytes(path);
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.position = new Rect(0, 0, 1500, 1000);
                window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path)); DrawTestFrame(window);
                GameObject root;
                using (var so = new SerializedObject(window)) root = (GameObject)so.FindProperty("session").FindPropertyRelative("root").objectReferenceValue;
                var items = root.GetComponentsInChildren<ParticleSystem>(true);
                window.SelectEmitter(items[0], false); DrawTestFrame(window);
                // Second column's selection checkbox: fixed 400px column + helpbox margins.
                Vector2 point = new Vector2(window.PanelLayout.Columns.x + 404 + 14, window.PanelLayout.Columns.y + 34);
                point += window.GuiScreenOrigin - window.position.position;
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = point, modifiers = EventModifiers.Shift });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = point, modifiers = EventModifiers.Shift });
                DrawTestFrame(window);
                CollectionAssert.AreEqual(new[] { items[0], items[1] }, window.SelectedEmitters);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete });
                DrawTestFrame(window);
                Assert.AreEqual(1, root.GetComponentsInChildren<ParticleSystem>(true).Length);
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
                Undo.PerformUndo(); Assert.AreEqual(3, root.GetComponentsInChildren<ParticleSystem>(true).Length);
            }
            finally { window.DiscardChanges(); window.Close(); }
        }
    }
}
