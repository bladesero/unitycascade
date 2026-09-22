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
    public sealed class CascadePreviewCacheTestWindow : EditorWindow
    {
        internal CascadePreview Preview;
        void OnGUI() { Preview?.Draw(new Rect(0, 0, position.width, position.height)); }
    }

    public partial class CascadeSessionTests
    {
        static void DrawTestFrame(EditorWindow window)
        {
            window.SendEvent(new Event { type = EventType.Layout });
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                window.SendEvent(new Event { type = EventType.Repaint });
        }

        [Test]
        public void VisibleColumnRangeIncludesPartialColumnsAndClampsEnds()
        {
            Assert.AreEqual(new Vector2Int(0, 1), ShurikenCascadeWindow.VisibleColumns(0, 550, 11));
            Assert.AreEqual(new Vector2Int(0, 2), ShurikenCascadeWindow.VisibleColumns(400, 550, 11));
            Assert.AreEqual(new Vector2Int(8, 10), ShurikenCascadeWindow.VisibleColumns(3500, 950, 11));
            Assert.AreEqual(new Vector2Int(0, -1), ShurikenCascadeWindow.VisibleColumns(0, 550, 0));
            Assert.AreEqual(new Vector2Int(10, 10), ShurikenCascadeWindow.VisibleColumns(99999, 550, 11));
        }

        [Test]
        public void SharedNativeTrackerSkipsIdleSerializationAndCatchesDelayedCommits()
        {
            using (var tracker = new CascadeNativeChangeTracker())
            using (var first = new CascadeNativeInspector(tracker))
            using (var second = new CascadeNativeInspector(tracker))
            {
                first.Bind(session, session.Emitters[0]); second.Bind(session, session.Emitters[1]);
                Assert.AreEqual(1, tracker.BaselineCount, "Every column must share a single document baseline.");
                tracker.ObserveChanges(false);
                int checks = tracker.FullCheckCount;
                for (int i = 0; i < 120; i++) Assert.IsFalse(tracker.ObserveChanges(false));
                Assert.AreEqual(checks, tracker.FullCheckCount, "Idle polling must not serialize/hash the document.");
                var emitter = session.Emitters[1];
                second.Dispose(); // An offscreen editor was evicted while its authoring target survives.
                using (var so = new SerializedObject(emitter))
                {
                    so.FindProperty("InitialModule.startSpeed.scalar").floatValue = 13;
                    so.ApplyModifiedProperties();
                }
                Assert.IsTrue(tracker.ObserveChanges(false));
                Assert.IsTrue(session.Dirty); Assert.AreEqual(13, emitter.main.startSpeed.constant);
                checks = tracker.FullCheckCount;
                Assert.IsFalse(tracker.ObserveChanges(false));
                Assert.AreEqual(checks, tracker.FullCheckCount);
                session.Save();
                Assert.AreEqual(13, AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<ParticleSystem>(true)[1].main.startSpeed.constant);
            }
        }

        [Test]
        public void ElevenEmitterColumnsVirtualizeScrollAndReleaseOffscreenEditors()
        {
            // A contiguous particle hierarchy also exercises Unity's per-Inspector hierarchy setup.
            session.Root.AddComponent<ParticleSystem>();
            while (session.Emitters.Length < 11) session.Add();
            session.MarkDirty(); session.Save();
            byte[] disk = File.ReadAllBytes(path);
            int scenes = EditorSceneManager.previewSceneCount;
            int editors = Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector");
            var errors = new List<string>();
            Application.LogCallback callback = (condition, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(condition); };
            Application.logMessageReceived += callback;
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.position = new Rect(0, 0, 850, 550);
                window.Show(); window.position = new Rect(0, 0, 850, 550); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                DrawTestFrame(window);
                Assert.Greater(window.DrawnInspectorCount, 0);
                Assert.LessOrEqual(window.DrawnInspectorCount, 3, window.position.ToString());
                Assert.LessOrEqual(Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector") - editors, 3);
                foreach (int offset in new[] { 1900, 3800, 400, 0 })
                {
                    using (var so = new SerializedObject(window))
                    { so.FindProperty("columnsScroll").vector2Value = new Vector2(offset, 0); so.ApplyModifiedPropertiesWithoutUndo(); }
                    for (int i = 0; i < 4; i++) DrawTestFrame(window);
                    Assert.LessOrEqual(window.DrawnInspectorCount, 3, "Only viewport columns should invoke native OnInspectorGUI: " + window.position);
                    Assert.LessOrEqual(Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector") - editors, 4, "Offscreen editors must be evicted; the selected popup owner may remain.");
                }
                // Selection from timeline/statistics reveals the final emitter without rebuilding all 11.
                using (var so = new SerializedObject(window))
                {
                    var root = (GameObject)so.FindProperty("session").FindPropertyRelative("root").objectReferenceValue;
                    so.FindProperty("selected").objectReferenceValue = root.GetComponentsInChildren<ParticleSystem>(true).Last();
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                DrawTestFrame(window);
                using (var so = new SerializedObject(window)) Assert.Greater(so.FindProperty("columnsScroll").vector2Value.x, 3000);
                window.SaveChanges();
                Assert.IsFalse(window.hasUnsavedChanges);
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            }
            finally { window.DiscardChanges(); window.Close(); Application.logMessageReceived -= callback; }
            Assert.IsEmpty(errors, string.Join("\n", errors));
            Assert.AreEqual(editors, Resources.FindObjectsOfTypeAll<Editor>().Count(e => e.GetType().Name == "ParticleSystemInspector"));
            Assert.AreEqual(scenes, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void PausedPreviewReusesRenderUntilCameraVisibilityOrSimulationChanges()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Graphics device required.");
            var window = ScriptableObject.CreateInstance<CascadePreviewCacheTestWindow>();
            using (var preview = new CascadePreview())
            {
                try
                {
                    window.Preview = preview; window.position = new Rect(0, 0, 420, 320);
                    preview.Rebuild(session.Root); preview.Playing = false; window.Show();
                    DrawTestFrame(window);
                    int renders = preview.RenderSubmissionCount;
                    Assert.Greater(renders, 0);
                    for (int i = 0; i < 10; i++) DrawTestFrame(window);
                    Assert.AreEqual(renders, preview.RenderSubmissionCount);
                    preview.Orbit += Vector2.one; DrawTestFrame(window);
                    Assert.AreEqual(++renders, preview.RenderSubmissionCount);
                    preview.ApplyVisibility(); DrawTestFrame(window);
                    Assert.AreEqual(++renders, preview.RenderSubmissionCount);
                    preview.RequestSeekFrame(1); CompleteSeek(preview); DrawTestFrame(window);
                    Assert.AreEqual(++renders, preview.RenderSubmissionCount);
                    DrawTestFrame(window); Assert.AreEqual(renders, preview.RenderSubmissionCount);
                }
                finally { window.Preview = null; window.Close(); }
            }
        }
    }
}
