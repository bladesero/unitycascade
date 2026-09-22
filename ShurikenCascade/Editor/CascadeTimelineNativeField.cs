using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    // One permanent pair of public native controls, before virtualized rows/Inspectors.
    // Their command IDs do not depend on which emitter rows are currently visible.
    internal sealed class CascadeTimelineNativeField
    {
        internal ParticleSystem Emitter { get; private set; }
        internal string Path { get; private set; }
        internal AnimationCurve Curve { get; private set; }
        internal Gradient Gradient { get; private set; }
        GameObject root;
        string expectedJson;
        Rect screenRect;
        bool committing;

        internal bool Bind(CascadeSession session, CascadeParameterRow row)
        {
            if (!CascadeTimelineAuthoring.Editable(session, row.Track.Emitter)) return false;
            Clear(); Emitter = row.Track.Emitter; Path = row.Path; root = session.Root;
            expectedJson = EditorJsonUtility.ToJson(Emitter); screenRect = row.GraphScreenRect;
            using (var so = new SerializedObject(Emitter))
            {
                var property = so.FindProperty(Path);
                if (row.Kind == CascadeParameterRowKind.Curve) Curve = CascadeTimelineKeyEdit.Clone(property.animationCurveValue);
                else Gradient = CascadeTimelineKeyEdit.Clone(property.gradientValue);
            }
            return true;
        }

        internal void Clear()
        {
            if (committing) return;
            Emitter = null; root = null; Path = null; expectedJson = null; Curve = null; Gradient = null;
        }

        internal bool Commit(CascadeSession session, AnimationCurve curve, Gradient gradient, Action changed)
        {
            if (!Emitter || root != session.Root || !CascadeTimelineAuthoring.Editable(session, Emitter) || EditorJsonUtility.ToJson(Emitter) != expectedJson)
            { Clear(); return false; }
            using (var so = new SerializedObject(Emitter))
            {
                var property = so.FindProperty(Path);
                if (Curve != null)
                {
                    if (curve == null) return false;
                    var current = property.animationCurveValue;
                    if (Same(current.keys, curve.keys) && current.preWrapMode == curve.preWrapMode && current.postWrapMode == curve.postWrapMode) return false;
                }
                else
                {
                    if (gradient == null) return false;
                    var current = property.gradientValue;
                    if (Same(current.colorKeys, gradient.colorKeys) && Same(current.alphaKeys, gradient.alphaKeys) && current.mode == gradient.mode) return false;
                }
            }
            bool applied = CascadeTimelineAuthoring.Apply(session, Emitter, "Edit Particle Curve / Gradient", so => {
                var property = so.FindProperty(Path);
                if (Curve != null) property.animationCurveValue = CascadeTimelineKeyEdit.Clone(curve);
                else property.gradientValue = CascadeTimelineKeyEdit.Clone(gradient);
            });
            if (!applied) return false;
            // Keep the native gradient object: its picker retains this reference between commands.
            if (Curve != null) Curve = curve; else Gradient = gradient;
            expectedJson = EditorJsonUtility.ToJson(Emitter);
            committing = true;
            try { changed?.Invoke(); }
            finally { committing = false; }
            return true;
        }

        static bool Same<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
            return true;
        }

        internal void Draw(CascadeSession session, Vector2 screenOrigin, bool opening, Action changed)
        {
            if (!Emitter || root != session.Root || EditorApplication.isPlayingOrWillChangePlaymode) Clear();
            Event e = Event.current;
            // Freeze the opening rect for this binding, even when its virtual row scrolls away.
            Rect anchor = new Rect(screenRect.position - screenOrigin, new Vector2(Mathf.Max(2, screenRect.width), Mathf.Max(2, screenRect.height)));
            if (!Emitter || e.type == EventType.Repaint) anchor.position = new Vector2(-10000, -10000);
            bool commands = e.type == EventType.ExecuteCommand || e.type == EventType.ValidateCommand || e.type == EventType.Repaint || e.type == EventType.Layout;
            GUI.BeginGroup(anchor);
            try
            {
                if (Curve != null)
                using (new EditorGUI.DisabledScope(!opening && !commands))
                {
                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUI.CurveField(new Rect(Vector2.zero, anchor.size), Curve, Color.cyan, new Rect());
                    if (EditorGUI.EndChangeCheck() && Curve != null) Commit(session, value, null, changed);
                }
                else GUIUtility.GetControlID(FocusType.Passive);
                if (Gradient != null)
                using (new EditorGUI.DisabledScope(!opening && !commands))
                {
                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUI.GradientField(new Rect(Vector2.zero, anchor.size), GUIContent.none, Gradient, true);
                    if (EditorGUI.EndChangeCheck() && Gradient != null) Commit(session, null, value, changed);
                }
                else GUIUtility.GetControlID(FocusType.Passive);
            }
            finally { GUI.EndGroup(); }
        }
    }
}
