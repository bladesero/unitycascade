using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        CascadeParameterRow NativeRow(bool gradient)
        {
            var p = session.Emitters[0]; var size = p.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(3, AnimationCurve.Linear(0, 0, 1, 1));
            var color = p.colorOverLifetime; color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(new Gradient(), new Gradient());
            return new CascadeParameterRow { Track = CascadeTimelineTrack.Build(session)[0],
                Kind = gradient ? CascadeParameterRowKind.Gradient : CascadeParameterRowKind.Curve,
                Path = gradient ? "ColorModule.gradient.minGradient" : "SizeModule.curve.maxCurve" };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeTimelineFieldCommitsChosenPropertyAndSupportsUndoSave(bool gradient)
        {
            var row = NativeRow(gradient); var p = row.Track.Emitter;
            session.MarkDirty(); session.Save(); Undo.ClearAll(); byte[] disk = File.ReadAllBytes(path);
            var field = new CascadeTimelineNativeField(); Assert.IsTrue(field.Bind(session, row));
            Assert.IsFalse(session.Dirty);
            Assert.IsFalse(field.Commit(session, field.Curve, field.Gradient, null));
            Assert.IsFalse(session.Dirty, "An unchanged native picker callback must not dirty the Prefab.");
            var curve = new AnimationCurve(new Keyframe(0, 2, 1, 2, 0.2f, 0.4f) { weightedMode = WeightedMode.Both }, new Keyframe(1, 3));
            var colors = new Gradient { mode = GradientMode.Fixed };
            colors.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.blue, 1) },
                new[] { new GradientAlphaKey(0.2f, 0), new GradientAlphaKey(0.8f, 1) });
            int changed = 0;
            Assert.IsTrue(field.Commit(session, curve, colors, () => { changed++; field.Clear(); }));
            Assert.AreEqual(p, field.Emitter, "Own change callback preserves the active native picker binding.");
            Assert.AreEqual(1, changed); Assert.IsTrue(session.Dirty);
            if (gradient)
            {
                Assert.AreEqual(GradientMode.Fixed, p.colorOverLifetime.color.gradientMin.mode);
                Assert.AreEqual(Color.red, p.colorOverLifetime.color.gradientMin.colorKeys[0].color);
                Assert.AreEqual(Color.white, p.colorOverLifetime.color.gradientMax.colorKeys[0].color);
            }
            else
            {
                Assert.AreEqual(2, p.sizeOverLifetime.size.curve.keys[0].value);
                Assert.AreEqual(WeightedMode.Both, p.sizeOverLifetime.size.curve.keys[0].weightedMode);
                Assert.AreEqual(3, p.sizeOverLifetime.size.curveMultiplier);
            }
            Undo.PerformUndo();
            if (gradient) Assert.AreEqual(Color.white, p.colorOverLifetime.color.gradientMin.colorKeys[0].color);
            else Assert.AreEqual(0, p.sizeOverLifetime.size.curve.keys[0].value);
            Undo.PerformRedo(); CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            session.Save(); session.Reload();
            if (gradient) Assert.AreEqual(Color.red, session.Emitters[0].colorOverLifetime.color.gradientMin.colorKeys[0].color);
            else Assert.AreEqual(2, session.Emitters[0].sizeOverLifetime.size.curve.keys[0].value);
        }

        [Test]
        public void NativeTimelineFieldRejectsStaleAndClosedDocumentCallbacks()
        {
            var row = NativeRow(false); var field = new CascadeTimelineNativeField(); Assert.IsTrue(field.Bind(session, row));
            var main = row.Track.Emitter.main; main.startDelay = 3;
            Assert.IsFalse(field.Commit(session, AnimationCurve.Constant(0, 1, 5), null, null));
            Assert.IsNull(field.Emitter);
            Assert.IsTrue(field.Bind(session, row)); session.Close();
            Assert.IsFalse(field.Commit(session, AnimationCurve.Constant(0, 1, 5), null, null));
        }

        [UnityTest]
        public IEnumerator TimelineDisplayOpensOfficialPickersWithoutEditingOrSeeking()
        {
            NativeRow(false); var main = session.Emitters[0].main; main.startDelay = 2;
            for (int i = 0; i < 9; i++) session.Duplicate(session.Emitters[0]);
            session.MarkDirty(); session.Save(); byte[] disk = File.ReadAllBytes(path);
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            var existing = Resources.FindObjectsOfTypeAll<EditorWindow>();
            bool Picker(EditorWindow w) => w && (w.GetType().Name == "CurveEditorWindow" || w.GetType().Name == "GradientPicker");
            void ClosePickers()
            {
                window.Timeline.Parameters.Native.Clear();
                foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => Picker(w) && !existing.Contains(w)))
                { w.Show(); DrawTestFrame(w); w.Close(); }
            }
            try
            {
                window.Show(); window.position = new Rect(0, 0, 1400, 1450); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                using (var so = new SerializedObject(window))
                {
                    var timeline = so.FindProperty("timelineState"); timeline.FindPropertyRelative("Expanded").boolValue = true;
                    timeline.FindPropertyRelative("Height").floatValue = 1000;
                    var folders = timeline.FindPropertyRelative("OpenEmitters"); folders.arraySize = 1;
                    folders.GetArrayElementAtIndex(0).stringValue = window.Timeline.Tracks[0].Key;
                    so.FindProperty("performanceExpanded").boolValue = false; so.ApplyModifiedPropertiesWithoutUndo();
                }
                DrawTestFrame(window); var p = window.Timeline.Tracks[0].Emitter;
                void Mouse(EventType type, Vector2 screen) => window.SendEvent(new Event { type = type, button = 0, mousePosition = screen - window.position.position });
                foreach (var kind in new[] { CascadeParameterRowKind.Curve, CascadeParameterRowKind.Gradient })
                {
                    DrawTestFrame(window);
                    var row = window.Timeline.Parameters.Rows.First(r => r.Kind == kind);
                    Rect graph = row.GraphScreenRect;
                    Assert.AreEqual(window.Timeline.FirstLaneScreenRect.x + window.Timeline.FirstLaneScreenRect.width * 0.2f, graph.x, 1f);
                    window.Preview.RequestSeek(1); CompleteSeek(window.Preview); int frame = window.Preview.CurrentFrame;
                    bool wasDirty = window.hasUnsavedChanges;
                    Mouse(EventType.MouseDown, graph.center);
                    Assert.AreEqual(row.Path, window.Timeline.Parameters.Native.Path, "Click binding: " + row.VisibleGraphScreenRect + " vs " + graph);
                    string expectedPicker = kind == CascadeParameterRowKind.Curve ? "CurveEditorWindow" : "GradientPicker";
                    Assert.IsTrue(Resources.FindObjectsOfTypeAll<EditorWindow>().Any(w => w && w.GetType().Name == expectedPicker), "Click must open Unity's " + expectedPicker);
                    Assert.AreEqual(row.Path, window.Timeline.Parameters.Native.Path);
                    Assert.IsFalse(window.Timeline.IsEditing); Assert.AreEqual(wasDirty, window.hasUnsavedChanges);
                    Assert.AreEqual(frame, window.Preview.CurrentFrame); Assert.IsFalse(window.Preview.IsSeeking);
                    // Detached native edits continue to target the same field even if virtual rows disappear.
                    using (var so = new SerializedObject(window))
                    { so.FindProperty("timelineState").FindPropertyRelative("Scroll").vector2Value = new Vector2(0, 400); so.ApplyModifiedPropertiesWithoutUndo(); }
                    DrawTestFrame(window); Assert.AreEqual(row.Path, window.Timeline.Parameters.Native.Path);
                    var native = window.Timeline.Parameters.Native;
                    if (kind == CascadeParameterRowKind.Curve)
                    {
                        var key = native.Curve.keys[0]; key.value = 7; native.Curve.MoveKey(0, key);
                        window.SendEvent(EditorGUIUtility.CommandEvent("CurveChanged"));
                        Assert.AreEqual(7, p.sizeOverLifetime.size.curve.keys[0].value, "Native curve command must reach its original property after scrolling.");
                    }
                    else
                    {
                        var keys = native.Gradient.colorKeys; keys[0].color = Color.red;
                        native.Gradient.SetKeys(keys, native.Gradient.alphaKeys);
                        window.SendEvent(EditorGUIUtility.CommandEvent("GradientPickerChanged"));
                        Assert.AreEqual(Color.red, p.colorOverLifetime.color.gradientMin.colorKeys[0].color);
                        Assert.AreEqual(Color.white, p.colorOverLifetime.color.gradientMax.colorKeys[0].color);
                    }
                    Assert.AreEqual(row.Path, native.Path); Assert.IsTrue(window.hasUnsavedChanges);
                    ClosePickers(); window.Focus();
                    Undo.PerformUndo();
                    if (kind == CascadeParameterRowKind.Curve) Assert.AreEqual(0, p.sizeOverLifetime.size.curve.keys[0].value);
                    else Assert.AreEqual(Color.white, p.colorOverLifetime.color.gradientMin.colorKeys[0].color);
                    Undo.PerformRedo();
                    using (var so = new SerializedObject(window))
                    { so.FindProperty("timelineState").FindPropertyRelative("Scroll").vector2Value = Vector2.zero; so.ApplyModifiedPropertiesWithoutUndo(); }
                    DrawTestFrame(window);
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete });
                    Assert.AreEqual(11, window.Timeline.Tracks.Count); Assert.AreEqual(2, p.sizeOverLifetime.size.curve.length);
                }
                using (var so = new SerializedObject(window))
                {
                    var folders = so.FindProperty("timelineState").FindPropertyRelative("OpenEmitters"); folders.arraySize = 11;
                    for (int i = 0; i < folders.arraySize; i++) folders.GetArrayElementAtIndex(i).stringValue = window.Timeline.Tracks[i].Key;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                window.Timeline.Parameters.Rebuild(); DrawTestFrame(window);
                Assert.Less(window.Timeline.Parameters.DrawnRows, window.Timeline.Parameters.Rows.Count(r => r.Kind != CascadeParameterRowKind.Emitter && r.Kind != CascadeParameterRowKind.Module));
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
                yield return null;
            }
            finally { ClosePickers(); window.DiscardChanges(); window.Close(); }
        }
    }
}
