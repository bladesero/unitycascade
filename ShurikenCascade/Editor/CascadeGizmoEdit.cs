using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal enum CascadeGizmoTool { View, Move, Rotate, Scale, Shape, ShapeMove, ShapeRotate, ShapeScale }

    // Detached drag proposal: the author and simulation objects are never moved during a drag.
    internal sealed class CascadeGizmoEdit
    {
        internal readonly ParticleSystem Emitter;
        internal readonly CascadeGizmoTool Tool;
        internal Vector3 Position, Scale, ShapePosition, ShapeRotation, ShapeScale;
        internal Quaternion Rotation;
        internal float Radius, Angle, Length, Arc, DonutRadius, Thickness;
        readonly CascadeSession session;
        readonly GameObject root;
        readonly string transformJson, particleJson;
        readonly Matrix4x4 parentMatrix;
        readonly Quaternion parentRotation;
        bool finished;

        internal static bool IsShape(CascadeGizmoTool tool) => tool >= CascadeGizmoTool.Shape;
        internal static bool CanEdit(CascadeSession session, ParticleSystem p, CascadeGizmoTool tool)
        {
            if (tool == CascadeGizmoTool.View || !CascadeTimelineAuthoring.Editable(session, p)) return false;
            var parent = p.transform.parent;
            if (parent && Mathf.Abs(parent.localToWorldMatrix.determinant) < 1e-10f) return false;
            if (IsShape(tool)) return p.shape.enabled && Mathf.Abs(p.transform.localToWorldMatrix.determinant) >= 1e-10f;
            // Moving an ancestor would also move its protected nested Prefab subtree.
            foreach (var t in p.GetComponentsInChildren<Transform>(true))
                if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) return false;
            return true;
        }

        internal static CascadeGizmoEdit Begin(CascadeSession session, ParticleSystem p, CascadeGizmoTool tool) =>
            CanEdit(session, p, tool) ? new CascadeGizmoEdit(session, p, tool) : null;

        CascadeGizmoEdit(CascadeSession session, ParticleSystem p, CascadeGizmoTool tool)
        {
            this.session = session; root = session.Root; Emitter = p; Tool = tool;
            var t = p.transform;
            transformJson = EditorJsonUtility.ToJson(t); particleJson = EditorJsonUtility.ToJson(p);
            parentMatrix = t.parent ? t.parent.localToWorldMatrix : Matrix4x4.identity;
            parentRotation = t.parent ? t.parent.rotation : Quaternion.identity;
            Position = t.localPosition; Rotation = t.localRotation; Scale = t.localScale;
            var shape = p.shape;
            ShapePosition = shape.position; ShapeRotation = shape.rotation; ShapeScale = shape.scale;
            Radius = shape.radius; Angle = shape.angle; Length = shape.length; Arc = shape.arc;
            DonutRadius = shape.donutRadius; Thickness = shape.radiusThickness;
        }

        internal Matrix4x4 TransformMatrix => parentMatrix * Matrix4x4.TRS(Position, Rotation, Scale);
        internal Quaternion WorldRotation => parentRotation * Rotation;
        internal Vector3 WorldPosition => parentMatrix.MultiplyPoint3x4(Position);
        internal void SetWorldPosition(Vector3 value) { Position = parentMatrix.inverse.MultiplyPoint3x4(value); }
        internal void SetWorldRotation(Quaternion value) { Rotation = Quaternion.Inverse(parentRotation) * value; }
        internal Matrix4x4 ShapeParentMatrix => Emitter.main.scalingMode == ParticleSystemScalingMode.Local
            ? Matrix4x4.TRS(WorldPosition, WorldRotation, Scale) : TransformMatrix;

        internal bool Valid => !finished && root && session.Root == root && CanEdit(session, Emitter, Tool) &&
            EditorJsonUtility.ToJson(Emitter) == particleJson && EditorJsonUtility.ToJson(Emitter.transform) == transformJson &&
            (Emitter.transform.parent ? Emitter.transform.parent.localToWorldMatrix : Matrix4x4.identity) == parentMatrix;
        internal void Cancel() { finished = true; }
        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        internal bool Commit()
        {
            if (!Valid) return false;
            finished = true;
            if (!Finite(Position) || !Finite(Scale) || !Finite(ShapePosition) || !Finite(ShapeRotation) || !Finite(ShapeScale) ||
                !Finite(Rotation.x) || !Finite(Rotation.y) || !Finite(Rotation.z) || !Finite(Rotation.w) ||
                !Finite(Radius) || !Finite(Angle) || !Finite(Length) || !Finite(Arc) || !Finite(DonutRadius) || !Finite(Thickness)) return false;
            using (var so = new SerializedObject(IsShape(Tool) ? (UnityEngine.Object)Emitter : Emitter.transform))
            {
                switch (Tool)
                {
                    case CascadeGizmoTool.Move: so.FindProperty("m_LocalPosition").vector3Value = Position; break;
                    case CascadeGizmoTool.Rotate: so.FindProperty("m_LocalRotation").quaternionValue = Rotation; break;
                    case CascadeGizmoTool.Scale: so.FindProperty("m_LocalScale").vector3Value = Scale; break;
                    case CascadeGizmoTool.ShapeMove: so.FindProperty("ShapeModule.m_Position").vector3Value = ShapePosition; break;
                    case CascadeGizmoTool.ShapeRotate: so.FindProperty("ShapeModule.m_Rotation").vector3Value = ShapeRotation; break;
                    case CascadeGizmoTool.ShapeScale: so.FindProperty("ShapeModule.m_Scale").vector3Value = ShapeScale; break;
                    case CascadeGizmoTool.Shape:
                        // Only changed dimensions are written; preserve all unrelated/hidden module data.
                        var shape = Emitter.shape;
                        SetFloat(so, "radius.value", Radius, shape.radius, 0, float.MaxValue);
                        SetFloat(so, "angle", Angle, shape.angle, 0, 90);
                        SetFloat(so, "length", Length, shape.length, 0, float.MaxValue);
                        SetFloat(so, "arc.value", Arc, shape.arc, 0, 360);
                        SetFloat(so, "donutRadius", DonutRadius, shape.donutRadius, 0, float.MaxValue);
                        SetFloat(so, "radiusThickness", Thickness, shape.radiusThickness, 0, 1);
                        if (ShapeScale != shape.scale) so.FindProperty("ShapeModule.m_Scale").vector3Value = ShapeScale;
                        break;
                }
                if (!so.hasModifiedProperties) return false;
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Particle Gizmo " + Tool);
                try
                {
                    if (!so.ApplyModifiedProperties()) return false;
                    Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
                }
                finally { Undo.IncrementCurrentGroup(); }
            }
            session.MarkDirty(); return true;
        }

        static void SetFloat(SerializedObject so, string path, float value, float original, float min, float max)
        {
            if (value != original) so.FindProperty("ShapeModule." + path).floatValue = Mathf.Clamp(value, min, max);
        }
    }
}
