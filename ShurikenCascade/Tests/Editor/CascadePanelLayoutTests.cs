using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        [Test]
        public void PanelGeometryFitsAvailableSpaceAndCollapsedPanelsReleaseTheirBodies()
        {
            foreach (var size in new[] { new Vector2(850, 528), new Vector2(1500, 978), new Vector2(850, 450) })
                foreach (bool timeline in new[] { false, true })
                    foreach (bool performance in new[] { false, true })
                    {
                        var area = new Rect(0, 22, size.x, size.y);
                        var layout = new CascadePanelLayout(area, 0.35f, 800, 900, timeline, performance);
                        Assert.AreEqual(area.yMax, layout.Performance.yMax, 0.01);
                        Assert.AreEqual(area.xMax, layout.Columns.xMax, 0.01);
                        Assert.AreEqual(layout.Preview.xMax, layout.HorizontalGrip.xMin, 0.01);
                        Assert.AreEqual(layout.Timeline.yMax, layout.PerformanceGrip.yMin, 0.01);
                        Assert.GreaterOrEqual(layout.TopHeight + 0.01f, layout.MinTop);
                        Assert.GreaterOrEqual(layout.Preview.width + 0.01f, layout.MinPreview);
                        Assert.GreaterOrEqual(layout.Columns.width + 0.01f, layout.MinColumns);
                        if (!timeline) { Assert.AreEqual(0, layout.TrackHeight); Assert.AreEqual(0, layout.TimelineGrip.height); }
                        if (!performance) { Assert.AreEqual(0, layout.StatsHeight); Assert.AreEqual(0, layout.PerformanceGrip.height); }
                    }
        }

        [Test]
        public void DividerResizesOnlyAdjacentPanelsAndUsesAbsoluteDragDistance()
        {
            var area = new Rect(0, 22, 1500, 950);
            var layout = new CascadePanelLayout(area, 0.35f, 170, 220, true, true);
            float ratio = 0.35f, track = 170, stats = 220;
            layout.Resize(1, 30, ref ratio, ref track, ref stats);
            var next = new CascadePanelLayout(area, ratio, track, stats, true, true);
            Assert.AreEqual(layout.TimelineGrip.y + 30, next.TimelineGrip.y, 0.01);
            Assert.AreEqual(layout.StatsHeight, next.StatsHeight, 0.01);
            Assert.AreEqual(layout.Performance.y, next.Performance.y, 0.01);
            layout.Resize(1, 30, ref ratio, ref track, ref stats);
            Assert.AreEqual(next.TrackHeight, track, 0.01, "Repeated identical pointer positions must not accumulate movement.");
            layout.Resize(2, 40, ref ratio, ref track, ref stats);
            next = new CascadePanelLayout(area, ratio, track, stats, true, true);
            Assert.AreEqual(layout.TopHeight, next.TopHeight, 0.01);
            Assert.AreEqual(layout.PerformanceGrip.y + 40, next.PerformanceGrip.y, 0.01);
            Assert.AreEqual(layout.TrackHeight + 40, next.TrackHeight, 0.01);
            layout.Resize(0, 100000, ref ratio, ref track, ref stats);
            next = new CascadePanelLayout(area, ratio, track, stats, true, true);
            Assert.AreEqual(next.MinColumns, next.Columns.width, 0.01);
        }

        [Test]
        public void DraggingConstrainedLayoutStartsWithoutJumpAndPreferencesSurviveWindowResize()
        {
            float ratio = 0.35f, track = 250, stats = 300;
            var smallArea = new Rect(0, 22, 850, 528);
            var small = new CascadePanelLayout(smallArea, ratio, track, stats, true, true);
            var large = new CascadePanelLayout(new Rect(0, 22, 1500, 978), ratio, track, stats, true, true);
            Assert.AreEqual(250, large.TrackHeight); Assert.AreEqual(300, large.StatsHeight);
            small.Resize(1, 0, ref ratio, ref track, ref stats);
            var after = new CascadePanelLayout(smallArea, ratio, track, stats, true, true);
            Assert.AreEqual(small.TopHeight, after.TopHeight, 0.01);
            Assert.AreEqual(small.TrackHeight, after.TrackHeight, 0.01);
            Assert.AreEqual(small.StatsHeight, after.StatsHeight, 0.01);
            var collapsed = new CascadePanelLayout(new Rect(0, 22, 1200, 780), ratio, track, stats, false, true);
            collapsed.Resize(2, 20, ref ratio, ref track, ref stats);
            after = new CascadePanelLayout(new Rect(0, 22, 1200, 780), ratio, track, stats, false, true);
            Assert.AreEqual(collapsed.TopHeight + Mathf.Min(20, collapsed.StatsHeight - collapsed.MinStats), after.TopHeight, 0.01);
            Assert.AreEqual(0, after.TrackHeight);
        }

        [Test]
        public void WindowDividersFollowPointerCancelAndDoNotDirtyPrefabOrUndo()
        {
            byte[] disk = File.ReadAllBytes(path);
            var errors = new List<string>();
            Application.LogCallback callback = (condition, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(condition); };
            Application.logMessageReceived += callback;
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.position = new Rect(0, 0, 1500, 1000); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                using (var so = new SerializedObject(window))
                {
                    so.FindProperty("previewRatio").floatValue = 0.35f;
                    so.FindProperty("performanceExpanded").boolValue = true;
                    so.FindProperty("performanceHeight").floatValue = 220;
                    so.FindProperty("timelineState").FindPropertyRelative("Height").floatValue = 170;
                    so.FindProperty("timelineState").FindPropertyRelative("Expanded").boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                DrawTestFrame(window);
                var sentinel = session.Emitters[0].transform;
                Vector3 originalPosition = sentinel.localPosition;
                Undo.IncrementCurrentGroup();
                Undo.RecordObject(sentinel, "Layout test undo sentinel");
                sentinel.localPosition = originalPosition + Vector3.one;
                Undo.FlushUndoRecordObjects();
                for (int divider = 0; divider < 3; divider++)
                {
                    var before = window.PanelLayout;
                    Rect grip = divider == 0 ? before.HorizontalGrip : divider == 1 ? before.TimelineGrip : before.PerformanceGrip;
                    Vector2 start = grip.center + window.GuiScreenOrigin - window.position.position, offset = divider == 0 ? new Vector2(30, 0) : new Vector2(0, 30);
                    window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = start });
                    window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = start + offset, delta = offset });
                    DrawTestFrame(window);
                    Rect moved = divider == 0 ? window.PanelLayout.HorizontalGrip : divider == 1 ? window.PanelLayout.TimelineGrip : window.PanelLayout.PerformanceGrip;
                    Assert.AreEqual(divider == 0 ? grip.x + 30 : grip.y + 30, divider == 0 ? moved.x : moved.y, 0.1);
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                    DrawTestFrame(window);
                    Assert.AreEqual(before.Preview, window.PanelLayout.Preview);
                    Assert.AreEqual(before.Timeline, window.PanelLayout.Timeline);
                    Assert.AreEqual(before.Performance, window.PanelLayout.Performance);
                    Assert.AreEqual(0, GUIUtility.hotControl);
                }
                var committed = window.PanelLayout.HorizontalGrip;
                Vector2 pointer = committed.center + window.GuiScreenOrigin - window.position.position;
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = pointer });
                window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = pointer + new Vector2(20, 0) });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = pointer + new Vector2(20, 0) });
                DrawTestFrame(window);
                Assert.AreEqual(committed.x + 20, window.PanelLayout.HorizontalGrip.x, 0.1);
                Assert.AreEqual(0, GUIUtility.hotControl);
                // Unity advances the group counter for mouse gestures even without Undo records.
                // The next real Undo must reach the pre-layout sentinel, not a layout mutation.
                Undo.PerformUndo();
                Assert.AreEqual(originalPosition, sentinel.localPosition);
                Assert.IsFalse(window.hasUnsavedChanges);
                window.SaveChanges(); CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            }
            finally { window.DiscardChanges(); window.Close(); Application.logMessageReceived -= callback; }
            Assert.IsEmpty(errors, string.Join("\n", errors));
        }
    }
}
