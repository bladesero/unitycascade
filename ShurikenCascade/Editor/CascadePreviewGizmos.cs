using System;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    internal sealed class CascadePreviewGizmos : IDisposable
    {
        internal CascadeGizmoTool Tool = CascadeGizmoTool.View;
        internal bool Local = true;
        internal bool IsDragging => dragControl != 0;
        internal Vector2 OriginScreen { get; private set; }
        internal Vector2 XHandleScreen { get; private set; }
        internal Rect ViewScreen { get; private set; }
        internal Vector2 ShapeRadiusScreen { get; private set; }
        internal RenderTexture Overlay => overlay;
        internal CascadeGizmoEdit Proposal => proposal;
        static readonly string[] toolNames = { "浏览", "移动 W", "旋转 E", "缩放 R", "Shape 尺寸", "Shape 移动", "Shape 旋转", "Shape 缩放" };
        readonly BoxBoundsHandle box = new BoxBoundsHandle();
        readonly SphereBoundsHandle sphere = new SphereBoundsHandle();
        readonly ArcHandle arc = new ArcHandle();
        CascadeGizmoEdit proposal;
        int dragControl;
        bool proposalChanged;
        int pickedControl;
        string status;
        RenderTexture overlay;
        static readonly Color wire = new Color(0.4f, 0.9f, 1f, 1f);

        internal void Cancel()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl)
            { GUIUtility.hotControl = 0; EditorGUIUtility.SetWantsMouseJumping(0); }
            dragControl = 0; pickedControl = 0; proposalChanged = false; proposal?.Cancel(); proposal = null;
        }

        public void Dispose() { Cancel(); if (overlay) UnityEngine.Object.DestroyImmediate(overlay); overlay = null; }

        internal void Toolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var next = (CascadeGizmoTool)EditorGUILayout.Popup((int)Tool, toolNames, EditorStyles.toolbarPopup);
                bool local;
                using (new EditorGUI.DisabledScope(Tool != CascadeGizmoTool.Move && Tool != CascadeGizmoTool.Rotate))
                    local = GUILayout.Toggle(Local, Local ? "局部" : "世界", EditorStyles.toolbarButton, GUILayout.Width(42));
                if (next != Tool || local != Local) { Cancel(); Tool = next; Local = local; }
            }
        }

        internal void HandleEarlyInput(ParticleSystem selected)
        {
            var e = Event.current;
            if (proposal != null && (proposal.Emitter != selected || proposal.Tool != Tool)) Cancel();
            if (IsDragging && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            { Cancel(); e.Use(); }
            if (IsDragging && (e.type == EventType.KeyDown || e.type == EventType.ValidateCommand || e.type == EventType.ExecuteCommand))
            {
                // Do not let shortcuts delete/save a half-finished proposal.
                if (e.keyCode == KeyCode.Delete) e.Use();
            }
        }

        internal void Draw(Rect rect, Vector2 windowSize, Camera camera, CascadeSession session, ParticleSystem selected, Action changed)
        {
            if (rect.width < 1 || rect.height < 1) return;
            status = null;
            var e = Event.current;
            bool inside = rect.Contains(e.mousePosition);
            if (inside && !IsDragging && e.type == EventType.KeyDown && !EditorGUIUtility.editingTextField && !e.alt && !e.control && !e.command)
            {
                CascadeGizmoTool next = e.keyCode == KeyCode.W ? CascadeGizmoTool.Move : e.keyCode == KeyCode.E ? CascadeGizmoTool.Rotate :
                    e.keyCode == KeyCode.R ? CascadeGizmoTool.Scale : e.keyCode == KeyCode.Q ? CascadeGizmoTool.View : Tool;
                if (next != Tool) { Cancel(); Tool = next; GUIUtility.keyboardControl = 0; e.Use(); }
            }
            if (Tool == CascadeGizmoTool.View || !camera || !selected) { Dispose(); return; }
            if (!CascadeGizmoEdit.CanEdit(session, selected, Tool))
            {
                Cancel();
                status = !selected.shape.enabled && CascadeGizmoEdit.IsShape(Tool) ? "请在 Inspector 启用 Shape" : "当前变换只读：嵌套保护或零缩放";
                return;
            }
            if (proposal == null || proposal.Emitter != selected || proposal.Tool != Tool || (!IsDragging && e.type == EventType.MouseDown))
                proposal = CascadeGizmoEdit.Begin(session, selected, Tool);
            if (proposal == null) return;

            // Reserve all handle IDs on every event, including events outside the preview.
            // Disabled handles cannot steal a click from another panel or camera navigation.
            bool interactive = IsDragging || (inside && !e.alt && !e.shift && (e.button == 0 || !e.isMouse));
            var previousCamera = Camera.current;
            var target = camera.targetTexture; var pixelRect = camera.pixelRect; float aspect = camera.aspect;
            var activeTexture = RenderTexture.active;
            var zTest = Handles.zTest;
            int before = GUIUtility.hotControl;
            EventType raw = e.rawType;
            if (raw == EventType.MouseDown && interactive) HandleUtility.nearestControl = pickedControl;
            // Handles expects root-window coordinates (including its ray conversion). Project into
            // the preview sub-rect, then crop a transparent overlay so wires never cover other panels.
            float dpi = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.Max(1, Mathf.CeilToInt(windowSize.x * dpi)), height = Mathf.Max(1, Mathf.CeilToInt(windowSize.y * dpi));
            bool repaint = e.type == EventType.Repaint;
            if (repaint && (!overlay || overlay.width != width || overlay.height != height))
            {
                if (overlay) UnityEngine.Object.DestroyImmediate(overlay);
                overlay = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave, name = "Cascade Gizmo Overlay" };
                overlay.Create();
            }
            if (repaint) GL.PushMatrix();
            try
            {
                camera.targetTexture = overlay;
                camera.aspect = rect.width / rect.height;
                camera.ResetProjectionMatrix();
                var viewport = Matrix4x4.identity;
                viewport.m00 = rect.width / windowSize.x; viewport.m11 = rect.height / windowSize.y;
                viewport.m03 = (2 * rect.x + rect.width) / windowSize.x - 1;
                viewport.m13 = 1 - (2 * rect.y + rect.height) / windowSize.y;
                var mappedProjection = viewport * camera.projectionMatrix;
                camera.pixelRect = new Rect(0, 0, width, height); camera.projectionMatrix = mappedProjection;
                if (repaint) { RenderTexture.active = overlay; GL.Clear(true, true, Color.clear); }
                Handles.SetCamera(camera);
                Handles.zTest = CompareFunction.Always;
                using (new Handles.DrawingScope(wire, Matrix4x4.identity))
                using (new EditorGUI.DisabledScope(!interactive))
                {
                    EditorGUI.BeginChangeCheck();
                    if (e.type == EventType.Repaint)
                    {
                        OriginScreen = GUIUtility.GUIToScreenPoint(HandleUtility.WorldToGUIPoint(proposal.WorldPosition));
                        Vector3 axis = (Local ? proposal.WorldRotation : Quaternion.identity) * Vector3.right;
                        XHandleScreen = GUIUtility.GUIToScreenPoint(HandleUtility.WorldToGUIPoint(proposal.WorldPosition + axis * HandleUtility.GetHandleSize(proposal.WorldPosition) * 0.65f));
                        ViewScreen = new Rect(GUIUtility.GUIToScreenPoint(rect.position), rect.size);
                    }
                    if (CascadeGizmoEdit.IsShape(Tool)) DrawShape();
                    else
                    {
                        DrawShapeWire();
                        Quaternion orientation = Local ? proposal.WorldRotation : Quaternion.identity;
                        float size = HandleUtility.GetHandleSize(proposal.WorldPosition);
                        switch (Tool)
                        {
                            case CascadeGizmoTool.Move:
                                proposal.SetWorldPosition(Handles.PositionHandle(proposal.WorldPosition, orientation)); break;
                            case CascadeGizmoTool.Rotate:
                                EditorGUI.BeginChangeCheck();
                                var rotation = Handles.RotationHandle(orientation, proposal.WorldPosition);
                                if (EditorGUI.EndChangeCheck()) proposal.SetWorldRotation(rotation * Quaternion.Inverse(orientation) * proposal.WorldRotation);
                                break;
                            case CascadeGizmoTool.Scale:
                                // Local scale is authored per-axis, even when the position tool uses world axes.
                                proposal.Scale = Handles.ScaleHandle(proposal.Scale, proposal.WorldPosition, proposal.WorldRotation, size); break;
                        }
                    }
                    proposalChanged |= EditorGUI.EndChangeCheck();
                }
            }
            finally
            {
                Handles.zTest = zTest;
                camera.targetTexture = target; camera.pixelRect = pixelRect; camera.aspect = aspect; camera.ResetProjectionMatrix();
                Camera.SetupCurrent(previousCamera);
                if (repaint) { RenderTexture.active = activeTexture; GL.PopMatrix(); }
            }
            if (raw == EventType.Layout || raw == EventType.MouseMove) pickedControl = HandleUtility.nearestControl;
            if (raw == EventType.MouseDown && before == 0 && GUIUtility.hotControl != 0)
            { dragControl = GUIUtility.hotControl; GUIUtility.keyboardControl = 0; }
            if (IsDragging && raw == EventType.MouseUp)
            {
                var edit = proposal; dragControl = 0; proposal = null;
                if (GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
                bool apply = proposalChanged; proposalChanged = false;
                if (apply && edit.Commit()) changed?.Invoke();
                if (e.type != EventType.Used) e.Use();
            }
            bool referenceShape = Tool == CascadeGizmoTool.Shape && ((int)selected.shape.shapeType == 6 ||
                (int)selected.shape.shapeType == 13 || (int)selected.shape.shapeType == 14 || (int)selected.shape.shapeType >= 19);
            status = selected.name + (IsDragging ? " · 松手应用 / Esc 取消" : referenceShape ? " · 包围盒参考，使用 Shape 移动/旋转/缩放" : " · 拖动手柄编辑 / Alt 拖动旋转视角");
        }

        internal void Composite(Rect rect, Vector2 windowSize)
        {
            if (Tool == CascadeGizmoTool.View) return;
            if (Event.current.type == EventType.Repaint && overlay && proposal != null)
                GUI.DrawTextureWithTexCoords(rect, overlay,
                    new Rect(rect.x / windowSize.x, 1 - rect.yMax / windowSize.y, rect.width / windowSize.x, rect.height / windowSize.y), true);
            if (!string.IsNullOrEmpty(status)) GUI.Label(new Rect(rect.x + 8, rect.yMax - 36, rect.width - 16, 34), status, EditorStyles.whiteMiniLabel);
        }

        Matrix4x4 ShapeMatrix(bool scale = true) => proposal.ShapeParentMatrix * Matrix4x4.TRS(proposal.ShapePosition,
            Quaternion.Euler(proposal.ShapeRotation), scale ? proposal.ShapeScale : Vector3.one);

        void DrawShape()
        {
            var p = proposal;
            if (Tool != CascadeGizmoTool.Shape)
            {
                DrawShapeWire();
                using (new Handles.DrawingScope(p.ShapeParentMatrix))
                {
                    Quaternion orientation = Quaternion.Euler(p.ShapeRotation);
                    switch (Tool)
                    {
                        case CascadeGizmoTool.ShapeMove: p.ShapePosition = Handles.PositionHandle(p.ShapePosition, orientation); break;
                        case CascadeGizmoTool.ShapeRotate:
                            EditorGUI.BeginChangeCheck(); var rotation = Handles.RotationHandle(orientation, p.ShapePosition);
                            if (EditorGUI.EndChangeCheck()) p.ShapeRotation = rotation.eulerAngles;
                            break;
                        case CascadeGizmoTool.ShapeScale:
                            p.ShapeScale = Handles.ScaleHandle(p.ShapeScale, p.ShapePosition, orientation, HandleUtility.GetHandleSize(p.ShapePosition)); break;
                    }
                }
                return;
            }
            int type = (int)p.Emitter.shape.shapeType;
            bool isBox = type == 5 || type == 15 || type == 16 || type == 18;
            using (new Handles.DrawingScope(ShapeMatrix(!isBox)))
            {
                if (Event.current.type == EventType.Repaint)
                    ShapeRadiusScreen = GUIUtility.GUIToScreenPoint(HandleUtility.WorldToGUIPoint(new Vector3(p.Radius, 0, 0)));
                if (isBox)
                {
                    box.center = Vector3.zero; box.size = p.ShapeScale;
                    box.axes = type == 18 ? PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Y : PrimitiveBoundsHandle.Axes.All;
                    box.SetColor(wire); EditorGUI.BeginChangeCheck(); box.DrawHandle();
                    if (EditorGUI.EndChangeCheck()) p.ShapeScale = type == 18 ? new Vector3(box.size.x, box.size.y, p.ShapeScale.z) : box.size;
                }
                else if (type == 0 || type == 1 || type == 2 || type == 3)
                {
                    DrawPrimitive(type);
                    // A hemisphere uses a positive-Z cap; only its radial XY extent is editable here.
                    sphere.center = Vector3.zero; sphere.radius = p.Radius;
                    sphere.axes = type == 2 || type == 3 ? PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Y : PrimitiveBoundsHandle.Axes.All;
                    sphere.SetColor(wire); sphere.DrawHandle(); p.Radius = Mathf.Max(0, sphere.radius);
                    DrawThickness();
                }
                else if (type == 4 || type == 7 || type == 8 || type == 9 || type == 10 || type == 11 || type == 17)
                {
                    DrawPrimitive(type);
                    arc.angle = p.Arc; arc.radius = p.Radius; arc.SetColorWithRadiusHandle(wire, 0.04f);
                    // ArcHandle lives in XZ; Shuriken emits along +Z, with the arc in XY.
                    using (new Handles.DrawingScope(Handles.matrix * Matrix4x4.Rotate(
                        Quaternion.AngleAxis(90, Vector3.right) * Quaternion.AngleAxis(90, Vector3.up)))) arc.DrawHandle();
                    p.Arc = Mathf.Clamp(arc.angle, 0, 360); p.Radius = Mathf.Max(0, arc.radius);
                    DrawThickness();
                    if (type == 4 || type == 7 || type == 8 || type == 9)
                    {
                        bool volume = type == 8 || type == 9;
                        float length = volume ? Mathf.Max(0.01f, p.Length) : Mathf.Max(1, p.Radius);
                        if (volume) p.Length = SliderValue(new Vector3(0, 0, p.Length), Vector3.forward, 2);
                        Vector3 tip = new Vector3(p.Radius + Mathf.Tan(Mathf.Min(89, p.Angle) * Mathf.Deg2Rad) * length, 0, length);
                        EditorGUI.BeginChangeCheck(); var result = Slider(tip, Vector3.right);
                        if (EditorGUI.EndChangeCheck()) p.Angle = Mathf.Clamp(Mathf.Atan2(result.x - p.Radius, length) * Mathf.Rad2Deg, 0, 90);
                    }
                    if (type == 17)
                    {
                        Vector3 edge = Slider(new Vector3(p.Radius, 0, p.DonutRadius), Vector3.forward);
                        p.DonutRadius = Mathf.Max(0, edge.z);
                    }
                }
                else if (type == 12)
                {
                    DrawPrimitive(type); p.Radius = SliderValue(new Vector3(p.Radius, 0, 0), Vector3.right, 0);
                }
                else DrawPrimitive(type);
            }
        }

        static Vector3 Slider(Vector3 position, Vector3 axis) => Handles.Slider(position, axis,
            HandleUtility.GetHandleSize(position) * 0.06f, Handles.DotHandleCap, 0);
        static float SliderValue(Vector3 position, Vector3 axis, int component) => Mathf.Max(0, Slider(position, axis)[component]);
        void DrawThickness()
        {
            if (proposal.Radius <= 0) return;
            using (new Handles.DrawingScope(new Color(0.6f, 0.7f, 0.7f)))
            {
                float inner = proposal.Radius * (1 - proposal.Thickness);
                EditorGUI.BeginChangeCheck(); var point = Slider(new Vector3(0, -inner, 0), Vector3.down);
                if (EditorGUI.EndChangeCheck()) proposal.Thickness = 1 - Mathf.Clamp01(-point.y / proposal.Radius);
                Handles.DrawWireDisc(Vector3.zero, Vector3.forward, inner);
            }
        }

        void DrawShapeWire()
        {
            if (!proposal.Emitter.shape.enabled) return;
            using (new Handles.DrawingScope(ShapeMatrix())) DrawPrimitive((int)proposal.Emitter.shape.shapeType);
        }

        void DrawPrimitive(int type)
        {
            if (Event.current.type != EventType.Repaint) return;
            var p = proposal;
            if (type == 5 || type == 15 || type == 16 || type == 18)
                Handles.DrawWireCube(Vector3.zero, type == 18 ? new Vector3(1, 1, 0) : Vector3.one);
            else if (type == 12) Handles.DrawLine(Vector3.left * p.Radius, Vector3.right * p.Radius);
            else if (type <= 4 || type == 7 || type == 8 || type == 9 || type == 10 || type == 11 || type == 17)
            {
                Handles.DrawWireArc(Vector3.zero, Vector3.forward, Vector3.right, p.Arc, p.Radius);
                if (type <= 3)
                {
                    float angle = type >= 2 ? 180 : 360;
                    Handles.DrawWireArc(Vector3.zero, Vector3.up, Vector3.left, angle, p.Radius);
                    Handles.DrawWireArc(Vector3.zero, Vector3.right, Vector3.up, angle, p.Radius);
                }
                if (type == 4 || type == 7 || type == 8 || type == 9)
                {
                    float length = type == 8 || type == 9 ? p.Length : Mathf.Max(1, p.Radius);
                    float top = p.Radius + Mathf.Tan(Mathf.Min(89, p.Angle) * Mathf.Deg2Rad) * length;
                    Handles.DrawWireArc(Vector3.forward * length, Vector3.forward, Vector3.right, p.Arc, top);
                    for (int i = 0; i <= 4; i++)
                    { Vector3 dir = Quaternion.AngleAxis(p.Arc * i / 4, Vector3.forward) * Vector3.right; Handles.DrawLine(dir * p.Radius, dir * top + Vector3.forward * length); }
                }
                if (type == 17)
                    for (int i = 0; i < 8; i++)
                    { var dir = Quaternion.AngleAxis(p.Arc * i / 8, Vector3.forward) * Vector3.right; Handles.DrawWireDisc(dir * p.Radius, Vector3.Cross(dir, Vector3.forward), p.DonutRadius); }
            }
            else
            {
                var shape = p.Emitter.shape;
                // Bounds are a reference, never a mesh/texture asset editing surface.
                Mesh mesh = shape.mesh;
                if (type == 13 && shape.meshRenderer) mesh = shape.meshRenderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (type == 14 && shape.skinnedMeshRenderer) mesh = shape.skinnedMeshRenderer.sharedMesh;
                Sprite sprite = type == 20 && shape.spriteRenderer ? shape.spriteRenderer.sprite : shape.sprite;
                if (mesh) Handles.DrawWireCube(mesh.bounds.center, mesh.bounds.size);
                else if (sprite) Handles.DrawWireCube(sprite.bounds.center, sprite.bounds.size);
            }
        }
    }
}
