using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    // A utility window keeps Unity's color/curve popups usable without closing the settings panel.
    internal sealed class CascadeEnvironmentWindow : EditorWindow
    {
        ShurikenCascadeWindow owner;
        CascadePreview preview;
        CascadePreviewEnvironment environment;
        Editor profileEditor;
        Vector2 scroll;

        internal static void Open(ShurikenCascadeWindow owner, CascadePreview preview)
        {
            foreach (var old in Resources.FindObjectsOfTypeAll<CascadeEnvironmentWindow>())
                if (old.owner == owner) { old.Focus(); return; }
            var window = CreateInstance<CascadeEnvironmentWindow>();
            window.owner = owner; window.preview = preview; window.environment = preview.Environment;
            window.environment.ReplacingProfile += window.DisposeEditors;
            window.titleContent = new GUIContent("预览环境"); window.minSize = new Vector2(330, 300);
            window.position = new Rect(owner.position.x + 40, owner.position.y + 80, 420, 600);
            if (!window.environment.Profile) window.environment.SetSource(window.environment.Source);
            window.ShowUtility();
        }
        void OnEnable() { EditorApplication.update += CheckOwner; Undo.undoRedoPerformed += OnUndoRedo; }
        void OnUndoRedo()
        {
            if (!owner || preview == null || environment == null) return;
            Changed(); Repaint();
        }
        void CheckOwner() { if (!owner || preview == null || owner.Preview != preview) Close(); }
        void OnDisable()
        {
            EditorApplication.update -= CheckOwner;
            Undo.undoRedoPerformed -= OnUndoRedo;
            if (environment != null) environment.ReplacingProfile -= DisposeEditors;
            DisposeEditors();
        }
        void DisposeEditors()
        { if (profileEditor) DestroyImmediate(profileEditor); profileEditor = null; }
        internal Editor ProfileEditor => profileEditor;
        void Changed() { environment.NotifyChanged(); preview.InvalidateRender(); owner.Repaint(); }
        void OnGUI()
        {
            if (!owner || preview == null || environment == null) return;
            EditorGUI.BeginChangeCheck();
            preview.Background = EditorGUILayout.ColorField(new GUIContent("背景颜色"), preview.Background, true, false, false);
            environment.Enabled = EditorGUILayout.Toggle("Volume 后处理", environment.Enabled);
            var source = (VolumeProfile)EditorGUILayout.ObjectField("Volume Profile", environment.Source, typeof(VolumeProfile), false);
            if (source != environment.Source) { environment.SetSource(source); preview.InvalidateRender(); }
            environment.Weight = EditorGUILayout.Slider("混合权重", environment.Weight, 0, 1);
            if (EditorGUI.EndChangeCheck()) Changed();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重新载入 / 重置")) { environment.SetSource(environment.Source); Changed(); GUIUtility.ExitGUI(); }
            }
            EditorGUILayout.HelpBox("参数在预览副本中调整，不改写 Profile 或特效 Prefab。关闭主窗口或编译后恢复所选 Profile。", MessageType.Info);
            if (!environment.Supported) EditorGUILayout.HelpBox("Volume 后处理需要 URP；当前管线仅显示背景颜色。", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            try
            {
                if (environment.Profile)
                {
                    // The Profile inspector initializes each native component editor (including
                    // custom URP inspectors) and owns their lifecycle and serialized Undo handling.
                    Editor.CreateCachedEditor(environment.Profile, null, ref profileEditor);
                    EditorGUI.BeginChangeCheck();
                    profileEditor.OnInspectorGUI();
                    if (EditorGUI.EndChangeCheck()) Changed();
                }
            }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }
}
