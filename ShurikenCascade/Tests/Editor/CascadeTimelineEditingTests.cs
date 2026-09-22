using System.IO;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        CascadeTimelineEdit BeginTimelineEdit(ParticleSystem p, int burst = -1) =>
            CascadeTimelineEdit.Begin(session, CascadeTimelineTrack.Build(session).First(t => t.Emitter == p), burst);

        [Test]
        public void TimelineDelayDragSnapsPreviewFramesPreservesModeAndUndoesAsOneOperation()
        {
            var p = session.Emitters[0]; var main = p.main;
            main.simulationSpeed = 2; main.startDelay = new ParticleSystem.MinMaxCurve(1, 3);
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            byte[] source = File.ReadAllBytes(path);
            var edit = BeginTimelineEdit(p);
            edit.Move(0.5); edit.Move(0.509);
            Assert.AreEqual(1, main.startDelay.constantMin); Assert.IsFalse(session.Dirty);
            Assert.IsTrue(edit.Commit()); Assert.IsFalse(edit.Commit());
            Assert.AreEqual(1 + 31 / 30f, main.startDelay.constantMin, 0.0001f);
            Assert.AreEqual(3 + 31 / 30f, main.startDelay.constantMax, 0.0001f);
            Assert.AreEqual(ParticleSystemCurveMode.TwoConstants, main.startDelay.mode);
            Assert.IsTrue(session.Dirty); CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
            Undo.PerformUndo(); Assert.AreEqual(1, main.startDelay.constantMin); Assert.AreEqual(3, main.startDelay.constantMax);
            Undo.PerformRedo(); Assert.AreEqual(1 + 31 / 30f, main.startDelay.constantMin, 0.0001f);
            session.Save(); session.Reload();
            Assert.AreEqual(1 + 31 / 30f, session.Emitters[0].main.startDelay.constantMin, 0.0001f);
            Assert.AreEqual(0, session.Emitters[1].main.startDelay.constant, "Same-name sibling must stay untouched.");
        }

        [Test]
        public void TimelineDelayDragClampsRangeTogetherAndCancelOrNoOpDoesNotRecordUndo()
        {
            var p = session.Emitters[0]; var main = p.main;
            main.startDelay = new ParticleSystem.MinMaxCurve(1, 4);
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            // Sentinel proves cancellation adds no asset undo, independent of Unity's group counter.
            Undo.RegisterCompleteObjectUndo(p.transform, "Sentinel"); p.transform.localPosition = Vector3.right;
            Undo.IncrementCurrentGroup();
            var cancel = BeginTimelineEdit(p); cancel.Move(9); cancel.Cancel(); Assert.IsFalse(cancel.Commit());
            var noop = BeginTimelineEdit(p); noop.Move(0.001); Assert.IsFalse(noop.Commit());
            Assert.IsFalse(session.Dirty); Undo.PerformUndo(); Assert.AreEqual(Vector3.zero, p.transform.localPosition);
            var edit = BeginTimelineEdit(p); edit.Move(-50); Assert.IsTrue(edit.Commit());
            Assert.AreEqual(0, main.startDelay.constantMin); Assert.AreEqual(3, main.startDelay.constantMax);
            Undo.PerformUndo(); Assert.AreEqual(1, main.startDelay.constantMin); Assert.AreEqual(4, main.startDelay.constantMax);
        }

        [Test]
        public void TimelineBurstDragChangesOnlyTimeAndRetainsOtherBurstsReferencesAndSaveData()
        {
            var p = session.Emitters[0]; var main = p.main; main.simulationSpeed = 0.5f;
            var emission = p.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(1, new ParticleSystem.MinMaxCurve(2, 7), 4, 0.2f) { probability = 0.35f }, new ParticleSystem.Burst(3, 11) });
            var subs = p.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(session.Emitters[1], ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            var edit = BeginTimelineEdit(p, 0); edit.Move(2); Assert.IsTrue(edit.Commit());
            var burst = emission.GetBurst(0);
            Assert.AreEqual(2, burst.time); Assert.AreEqual(4, burst.cycleCount);
            Assert.AreEqual(0.2f, burst.repeatInterval); Assert.AreEqual(0.35f, burst.probability, 0.000001f);
            Assert.AreEqual(2, burst.count.constantMin); Assert.AreEqual(7, burst.count.constantMax);
            Assert.AreEqual(3, emission.GetBurst(1).time); Assert.AreEqual(0, main.startDelay.constant);
            Undo.PerformUndo(); Assert.AreEqual(1, emission.GetBurst(0).time);
            Undo.PerformRedo(); Assert.AreEqual(2, emission.GetBurst(0).time);
            session.Save(); session.Reload();
            Assert.AreEqual(2, session.Emitters[0].emission.GetBurst(0).time);
            Assert.AreSame(session.Emitters[1], session.Emitters[0].subEmitters.GetSubEmitterSystem(0));
        }

        [Test]
        public void TimelineEditRejectsNestedEventDrivenFrozenAndUnknownDelayTracks()
        {
            var p = session.Emitters[0]; var child = session.Emitters[1];
            var subs = p.subEmitters; subs.enabled = true;
            subs.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            Assert.IsNull(BeginTimelineEdit(child));
            var main = p.main; main.simulationSpeed = 0; Assert.IsNull(BeginTimelineEdit(p));
            main.simulationSpeed = 1; main.startDelay = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            Assert.IsNull(BeginTimelineEdit(p));
            main.startDelay = 2; main.prewarm = true;
            Assert.IsNull(BeginTimelineEdit(p));
            Assert.AreEqual(0, CascadeTimelineTrack.Build(session)[0].DelayMin, "Prewarm ignores Start Delay.");
            var emission = p.emission; emission.SetBursts(new[] { new ParticleSystem.Burst(1, 2) });
            Assert.IsNotNull(BeginTimelineEdit(p, 0), "Prewarm does not prevent editing Burst configuration.");
            string nestedPath = folder + "/Nested.prefab";
            PrefabUtility.SaveAsPrefabAsset(p.gameObject, nestedPath);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), session.Root.transform);
            Assert.IsNull(BeginTimelineEdit(nested.GetComponent<ParticleSystem>(), 0));
        }

        [Test]
        public void TimelineEditRejectsStaleConfigurationAndClosedSession()
        {
            var p = session.Emitters[0]; var edit = BeginTimelineEdit(p); edit.Move(1);
            var main = p.main; main.simulationSpeed = 2;
            Assert.IsFalse(edit.Commit()); Assert.AreEqual(0, main.startDelay.constant);
            edit = BeginTimelineEdit(p); edit.Move(1);
            var parent = session.Emitters[1].subEmitters; parent.enabled = true;
            parent.AddSubEmitter(p, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            Assert.IsFalse(edit.Commit());
            parent.enabled = false; edit = BeginTimelineEdit(p); edit.Move(1); session.Close();
            Assert.IsFalse(edit.Commit());
        }

        [UnityTest]
        public IEnumerator TimelineMouseDragCommitsDelayAndBurstOnceAndEscCancelsWithoutSeeking()
        {
            var p = session.Emitters[0]; var main = p.main; main.startDelay = 1; main.duration = 2;
            var emission = p.emission; emission.SetBursts(new[] { new ParticleSystem.Burst(0.5f, 8, 2, 0.5f) });
            session.MarkDirty(); session.Save();
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.position = new Rect(0, 0, 1400, 950);
                window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                using (var so = new SerializedObject(window))
                {
                    so.FindProperty("timelineState").FindPropertyRelative("Expanded").boolValue = true;
                    so.FindProperty("timelineState").FindPropertyRelative("Scroll").vector2Value = Vector2.zero;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    p = ((GameObject)so.FindProperty("session").FindPropertyRelative("root").objectReferenceValue).GetComponentsInChildren<ParticleSystem>(true)[0];
                }
                DrawTestFrame(window); main = p.main; emission = p.emission;
                window.Preview.RequestSeek(1); CompleteSeek(window.Preview);
                void Mouse(EventType type, Vector2 screen)
                { window.SendEvent(new Event { type = type, button = 0, mousePosition = screen - window.position.position }); }
                void Drag(float time, float seconds, bool cancel = false, int burstIndex = -1)
                {
                    Rect lane = window.Timeline.FirstLaneScreenRect;
                    Vector2 start = new Vector2(lane.x + lane.width * time / 10, lane.center.y);
                    Vector2 end = start + new Vector2(lane.width * seconds / 10, 0);
                    string before = EditorJsonUtility.ToJson(p);
                    Mouse(EventType.MouseDown, start); Assert.IsTrue(window.Timeline.IsEditing);
                    Assert.AreEqual(burstIndex, window.Timeline.EditingBurstIndex, "Correct source handle must receive the drag.");
                    Mouse(EventType.MouseDrag, end); DrawTestFrame(window);
                    Assert.AreEqual(before, EditorJsonUtility.ToJson(p), "Dragging shows a proposal without repeatedly rebuilding the document.");
                    if (cancel) window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                    else Mouse(EventType.MouseUp, new Vector2(end.x, window.GuiScreenOrigin.y + window.PanelLayout.Preview.y + 80));
                    Assert.IsFalse(window.Timeline.IsEditing); DrawTestFrame(window);
                }
                Undo.ClearAll(); byte[] source = File.ReadAllBytes(path);
                Drag(1.2f, 0.5f); Assert.AreEqual(1.5f, main.startDelay.constant, 0.0001f);
                Assert.IsFalse(window.Preview.Playing); Assert.IsFalse(window.Preview.IsSeeking);
                Assert.IsTrue(window.hasUnsavedChanges); CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
                Undo.PerformUndo(); Assert.AreEqual(1, main.startDelay.constant);
                Undo.PerformRedo(); Assert.AreEqual(1.5f, main.startDelay.constant, 0.0001f);
                // Authoring feedback is immediate; simulation rebuild is scheduled on editor update.
                Assert.AreEqual(1.5f, window.Timeline.Tracks[0].DelayMin, "Tracks must rebuild after Undo/Redo.");
                double deadline = EditorApplication.timeSinceStartup + 5;
                while (window.Preview.CurrentFrame != 0 && EditorApplication.timeSinceStartup < deadline) yield return null;
                DrawTestFrame(window);
                Assert.AreEqual(0, window.Preview.CurrentFrame); Assert.IsFalse(window.Preview.Playing);
                window.Preview.Playing = true;
                Drag(2.5f, 0.25f, burstIndex: 0); Assert.AreEqual(0.75f, emission.GetBurst(0).time, 0.0001f);
                Assert.IsTrue(window.Preview.Playing); Assert.IsFalse(window.Preview.IsSeeking);
                Assert.AreEqual(1.5f, main.startDelay.constant, "Marker must win over emission band hit.");
                Undo.PerformUndo(); Assert.AreEqual(0.5f, emission.GetBurst(0).time);
                yield return null; DrawTestFrame(window);
                Drag(1.7f, 0.75f, true); Assert.AreEqual(1.5f, main.startDelay.constant, 0.0001f);
                window.SaveChanges(); Assert.IsFalse(window.hasUnsavedChanges);
                Rect bounds = window.Timeline.FirstLaneScreenRect;
                Vector2 ruler = new Vector2(bounds.x + bounds.width * 0.8f, bounds.y - 10);
                Mouse(EventType.MouseDown, ruler); Mouse(EventType.MouseUp, ruler);
                Assert.IsFalse(window.Timeline.IsEditing); Assert.IsFalse(window.Preview.Playing);
                Assert.AreEqual(480, window.Preview.SeekTargetFrame);
                Assert.IsFalse(window.hasUnsavedChanges, "Scrubbing still stays outside asset Undo/save.");
            }
            finally { window.DiscardChanges(); window.Close(); }
        }
    }
}
