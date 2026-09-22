using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    // Embed the public Editor surface; never reflect into ParticleSystem's internal module UI.
    internal sealed class CascadeNativeInspector : IDisposable
    {
        Editor particleEditor, transformEditor;
        ParticleSystem target;
        CascadeSession session;
        readonly CascadeNativeChangeTracker changes;
        readonly bool ownsTracker;
        internal string Message => changes.Message;
        internal Editor ParticleEditor => particleEditor;
        internal Editor TransformEditor => transformEditor;
        internal ParticleSystem Target => target;
        internal bool ShowTransform = true, ShowCurves;

        internal CascadeNativeInspector(CascadeNativeChangeTracker sharedChanges = null)
        {
            ownsTracker = sharedChanges == null;
            changes = sharedChanges ?? new CascadeNativeChangeTracker();
        }

        internal void Bind(CascadeSession document, ParticleSystem emitter)
        {
            if (target == emitter && session == document && particleEditor) return;
            Dispose();
            if (!emitter || !document.Root) return;
            session = document; target = emitter;
            changes.Bind(document);
            try
            {
                transformEditor = Editor.CreateEditor(emitter.transform);
                particleEditor = Editor.CreateEditor(emitter);
                if (!particleEditor) throw new InvalidOperationException("Unity 未提供 Particle System Inspector。");
            }
            catch { Dispose(); throw; }
        }

        internal bool ObserveChanges() => changes.ObserveChanges();

        internal bool Draw(CascadeSession document, ParticleSystem emitter, bool observeChanges = true)
        {
            Bind(document, emitter);
            if (!particleEditor) return false;
            bool readOnly = document.IsReadOnly(emitter);
            if (readOnly) EditorGUILayout.HelpBox("嵌套 Prefab · 原生参数只读。", MessageType.Info);
            if (!string.IsNullOrEmpty(Message)) EditorGUILayout.HelpBox(Message, MessageType.Warning);
            float labelWidth = EditorGUIUtility.labelWidth, fieldWidth = EditorGUIUtility.fieldWidth;
            int indent = EditorGUI.indentLevel;
            bool wide = EditorGUIUtility.wideMode, hierarchy = EditorGUIUtility.hierarchyMode;
            Color color = GUI.color, background = GUI.backgroundColor, content = GUI.contentColor;
            bool enabled = GUI.enabled;
            try
            {
                EditorGUIUtility.wideMode = true;
                EditorGUIUtility.hierarchyMode = false;
                EditorGUIUtility.labelWidth = 145;
                EditorGUIUtility.fieldWidth = 50;
                EditorGUI.indentLevel = 0;
                using (new EditorGUI.DisabledScope(readOnly))
                {
                    ShowTransform = EditorGUILayout.Foldout(ShowTransform, "Transform", true);
                    if (ShowTransform && transformEditor) transformEditor.OnInspectorGUI();
                    particleEditor.OnInspectorGUI();
                    ShowCurves = EditorGUILayout.Foldout(ShowCurves, "Particle System Curves", true);
                    if (ShowCurves && particleEditor.HasPreviewGUI())
                        particleEditor.OnPreviewGUI(GUILayoutUtility.GetRect(100, 170, GUILayout.ExpandWidth(true)), EditorStyles.helpBox);
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = labelWidth; EditorGUIUtility.fieldWidth = fieldWidth; EditorGUI.indentLevel = indent;
                EditorGUIUtility.wideMode = wide; EditorGUIUtility.hierarchyMode = hierarchy;
                GUI.color = color; GUI.backgroundColor = background; GUI.contentColor = content; GUI.enabled = enabled;
            }
            return observeChanges && Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint && ObserveChanges();
        }

        internal void RefreshBaseline() => changes.RefreshBaseline();

        public void Dispose()
        {
            if (particleEditor) Object.DestroyImmediate(particleEditor);
            if (transformEditor) Object.DestroyImmediate(transformEditor);
            particleEditor = transformEditor = null;
            target = null; session = null;
            if (ownsTracker) changes.Dispose();
        }
    }
}
