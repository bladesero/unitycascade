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
        [TestCase((int)CascadeGizmoTool.Move)]
        [TestCase((int)CascadeGizmoTool.Rotate)]
        [TestCase((int)CascadeGizmoTool.Scale)]
        [TestCase((int)CascadeGizmoTool.ShapeMove)]
        [TestCase((int)CascadeGizmoTool.ShapeRotate)]
        [TestCase((int)CascadeGizmoTool.ShapeScale)]
        [TestCase((int)CascadeGizmoTool.Shape)]
        public void GizmoProposalIsDetachedAndCommitsOneUndoWithExplicitSave(int toolValue)
        {
            var tool = (CascadeGizmoTool)toolValue;
            var p = session.Emitters[0];
            session.Root.transform.localPosition = new Vector3(3, -2, 1);
            session.Root.transform.localRotation = Quaternion.Euler(20, 40, 10);
            session.Root.transform.localScale = new Vector3(2, 3, 0.5f);
            session.MarkDirty(); session.Save(); Undo.ClearAll();
            byte[] disk = File.ReadAllBytes(path);
            string transform = EditorJsonUtility.ToJson(p.transform), particle = EditorJsonUtility.ToJson(p);
            var edit = CascadeGizmoEdit.Begin(session, p, tool); Assert.NotNull(edit);
            edit.SetWorldPosition(edit.WorldPosition + new Vector3(1, 2, 3));
            edit.SetWorldRotation(Quaternion.Euler(13, 25, 41)); edit.Scale = new Vector3(2, 3, 4);
            edit.ShapePosition = new Vector3(1, 2, 3); edit.ShapeRotation = new Vector3(4, 5, 6);
            edit.ShapeScale = new Vector3(3, 4, 5); edit.Radius = 4; edit.Angle = 35;
            Assert.AreEqual(transform, EditorJsonUtility.ToJson(p.transform));
            Assert.AreEqual(particle, EditorJsonUtility.ToJson(p)); Assert.IsFalse(session.Dirty);
            Assert.IsTrue(edit.Commit()); Assert.IsFalse(edit.Commit()); Assert.IsTrue(session.Dirty);
            string committedTransform = EditorJsonUtility.ToJson(p.transform), committedParticle = EditorJsonUtility.ToJson(p);
            if (CascadeGizmoEdit.IsShape(tool)) Assert.AreEqual(transform, committedTransform);
            else Assert.AreEqual(particle, committedParticle);
            Undo.PerformUndo(); Assert.AreEqual(transform, EditorJsonUtility.ToJson(p.transform)); Assert.AreEqual(particle, EditorJsonUtility.ToJson(p));
            Undo.PerformRedo(); Assert.AreEqual(committedTransform, EditorJsonUtility.ToJson(p.transform)); Assert.AreEqual(committedParticle, EditorJsonUtility.ToJson(p));
            CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
            session.Save(); session.Reload(); var restored = session.Emitters[0];
            if (tool == CascadeGizmoTool.Shape) Assert.AreEqual(4, restored.shape.radius);
            if (tool == CascadeGizmoTool.ShapeScale) Assert.AreEqual(new Vector3(3, 4, 5), restored.shape.scale);
            if (tool == CascadeGizmoTool.Move) Assert.Less((restored.transform.position - new Vector3(4, 0, 4)).magnitude, 0.0001f);
        }

        [Test]
        public void GizmoRejectsCancelledUnchangedStaleInvalidAndProtectedEdits()
        {
            var p = session.Emitters[0];
            var edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Move);
            Assert.IsFalse(edit.Commit()); Assert.IsFalse(session.Dirty);
            edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Move); edit.Position = Vector3.one; edit.Cancel();
            Assert.IsFalse(edit.Commit()); Assert.IsFalse(session.Dirty);
            edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Move); edit.Position = Vector3.one;
            session.Root.transform.localPosition = Vector3.one; Assert.IsFalse(edit.Commit());
            edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Shape); edit.Radius = float.NaN; Assert.IsFalse(edit.Commit());
            edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Shape); edit.Radius = 3;
            var shape = p.shape; shape.radius = 2; Assert.IsFalse(edit.Commit());
            shape.enabled = false; Assert.IsNull(CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Shape)); shape.enabled = true;
            string nestedPath = folder + "/Nested.prefab"; PrefabUtility.SaveAsPrefabAsset(p.gameObject, nestedPath);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath), p.transform);
            Assert.IsNull(CascadeGizmoEdit.Begin(session, nested.GetComponent<ParticleSystem>(), CascadeGizmoTool.Move));
            Assert.IsNull(CascadeGizmoEdit.Begin(session, nested.GetComponent<ParticleSystem>(), CascadeGizmoTool.Shape));
            Assert.IsNull(CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Move), "Ancestor movement must not move protected nested content.");
            Assert.NotNull(CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Shape));
            edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.Shape); session.Close(); Assert.IsFalse(edit.Commit());
        }

        [TestCase(ParticleSystemScalingMode.Hierarchy)]
        [TestCase(ParticleSystemScalingMode.Local)]
        [TestCase(ParticleSystemScalingMode.Shape)]
        public void GizmoShapeCoordinatesFollowParticleScalingMode(ParticleSystemScalingMode mode)
        {
            var p = session.Emitters[0]; session.Root.transform.localScale = new Vector3(2, 3, 4);
            session.Root.transform.rotation = Quaternion.Euler(0, 30, 0);
            p.transform.localPosition = new Vector3(1, 2, 3); p.transform.localScale = new Vector3(3, 2, 1);
            var main = p.main; main.scalingMode = mode;
            var edit = CascadeGizmoEdit.Begin(session, p, CascadeGizmoTool.ShapeMove);
            Matrix4x4 expected = mode == ParticleSystemScalingMode.Local
                ? Matrix4x4.TRS(p.transform.position, p.transform.rotation, p.transform.localScale) : p.transform.localToWorldMatrix;
            for (int i = 0; i < 16; i++) Assert.AreEqual(expected[i], edit.ShapeParentMatrix[i], 0.00001f);
        }

        [UnityTest]
        public IEnumerator GizmoWindowMouseDragCommitsCancelsAndLeavesCameraAndAssetAlone()
        {
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.position = new Rect(30, 30, 1400, 900); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                window.Preview.Playing = false;
                window.Preview.Orbit = new Vector2(20, -15); window.Preview.Distance = 8; window.Preview.Pivot = Vector3.zero;
                window.Gizmos.Tool = CascadeGizmoTool.Move; DrawTestFrame(window);
                var p = window.Timeline.Tracks[0].Emitter; Vector3 original = p.transform.localPosition;
                byte[] disk = File.ReadAllBytes(path); Vector2 orbit = window.Preview.Orbit;
                void Mouse(EventType type, Vector2 screen, Vector2 delta = default) => window.SendEvent(new Event {
                    type = type, button = 0, mousePosition = screen - window.position.position, delta = delta });
                Vector2 start = window.Gizmos.XHandleScreen, end = start + new Vector2(40, 0);
                Mouse(EventType.Layout, start); Mouse(EventType.MouseMove, start);
                Mouse(EventType.MouseDown, start);
                Assert.IsTrue(window.Gizmos.IsDragging, "The X handle must capture the click instead of orbit: " + start + " / " + window.Gizmos.ViewScreen);
                Mouse(EventType.MouseDrag, end, new Vector2(40, 0)); DrawTestFrame(window);
                Assert.AreEqual(original, p.transform.localPosition, "No author mutation during drag.");
                Assert.IsFalse(window.hasUnsavedChanges);
                Mouse(EventType.MouseUp, end); Assert.IsFalse(window.Gizmos.IsDragging);
                Assert.Greater((p.transform.localPosition - original).magnitude, 0.01f);
                Assert.AreEqual(original.y, p.transform.localPosition.y, 0.0001f);
                Assert.AreEqual(original.z, p.transform.localPosition.z, 0.0001f);
                Assert.IsTrue(window.hasUnsavedChanges); Assert.AreEqual(orbit, window.Preview.Orbit);
                Vector3 moved = p.transform.localPosition;
                Undo.PerformUndo(); Assert.AreEqual(original, p.transform.localPosition);
                Undo.PerformRedo(); Assert.AreEqual(moved, p.transform.localPosition);
                DrawTestFrame(window); start = window.Gizmos.XHandleScreen;
                Mouse(EventType.Layout, start); Mouse(EventType.MouseMove, start); Mouse(EventType.MouseDown, start);
                Mouse(EventType.MouseDrag, start + new Vector2(20, 0), new Vector2(20, 0));
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                Assert.IsFalse(window.Gizmos.IsDragging); Assert.AreEqual(moved, p.transform.localPosition);
                // All shape tools must render through the same camera, without dirtying on repaint.
                foreach (var type in new[] { ParticleSystemShapeType.Sphere, ParticleSystemShapeType.Hemisphere, ParticleSystemShapeType.Cone,
                    ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Box, ParticleSystemShapeType.Rectangle,
                    ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut, ParticleSystemShapeType.SingleSidedEdge, ParticleSystemShapeType.Mesh })
                {
                    var shape = p.shape; shape.shapeType = type; window.Gizmos.Cancel(); window.Gizmos.Tool = CascadeGizmoTool.Shape;
                    string before = EditorJsonUtility.ToJson(p); DrawTestFrame(window); Assert.AreEqual(before, EditorJsonUtility.ToJson(p));
                }
                CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
                yield return null;
            }
            finally { window.DiscardChanges(); window.Close(); }
        }

        [UnityTest]
        public IEnumerator GizmoShapeRadiusDragRendersAndReleasesOverlay()
        {
            var authoredShape = session.Emitters[0].shape; authoredShape.shapeType = ParticleSystemShapeType.Sphere; authoredShape.radius = 1;
            session.MarkDirty(); session.Save();
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            int overlays = Resources.FindObjectsOfTypeAll<RenderTexture>().Count(t => t.name == "Cascade Gizmo Overlay");
            try
            {
                window.Show(); window.position = new Rect(50, 40, 1300, 900); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                window.Preview.Playing = false; window.Preview.Pivot = Vector3.zero;
                window.Preview.Orbit = Vector2.zero; window.Preview.Distance = 6;
                var p = window.Timeline.Tracks[0].Emitter;
                window.Gizmos.Tool = CascadeGizmoTool.Shape; DrawTestFrame(window);
                int submissions = window.Preview.RenderSubmissionCount;
                DrawTestFrame(window); Assert.AreEqual(submissions, window.Preview.RenderSubmissionCount, "Gizmos must not invalidate a paused particle render.");
                var image = new Texture2D(window.Gizmos.Overlay.width, window.Gizmos.Overlay.height, TextureFormat.RGBA32, false);
                var old = RenderTexture.active;
                try
                {
                    RenderTexture.active = window.Gizmos.Overlay;
                    image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0); image.Apply();
                    Assert.Greater(image.GetPixels32().Count(c => c.a > 32 && c.g > 80), 50, "Handles must actually render into the transparent overlay.");
                    File.WriteAllBytes("gizmo-overlay.png", image.EncodeToPNG());
                }
                finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
                void Mouse(EventType type, Vector2 screen, Vector2 delta = default) => window.SendEvent(new Event {
                    type = type, button = 0, mousePosition = screen - window.position.position, delta = delta });
                Vector2 start = window.Gizmos.ShapeRadiusScreen;
                Mouse(EventType.Layout, start); Mouse(EventType.MouseMove, start); Mouse(EventType.MouseDown, start);
                Assert.IsTrue(window.Gizmos.IsDragging, "Sphere radius handle must be interactive: " + start + " / " + window.Gizmos.ViewScreen);
                Mouse(EventType.MouseDrag, start + Vector2.right * 35, Vector2.right * 35);
                Assert.AreEqual(1, p.shape.radius); Mouse(EventType.MouseUp, start + Vector2.right * 35);
                Assert.Greater(p.shape.radius, 1.01f); float radius = p.shape.radius;
                Undo.PerformUndo(); Assert.AreEqual(1, p.shape.radius); Undo.PerformRedo(); Assert.AreEqual(radius, p.shape.radius);
                Assert.AreEqual(Vector3.zero, p.transform.localPosition);
                yield return null;
            }
            finally { window.DiscardChanges(); window.Close(); }
            Assert.AreEqual(overlays, Resources.FindObjectsOfTypeAll<RenderTexture>().Count(t => t.name == "Cascade Gizmo Overlay"));
        }
    }
}
