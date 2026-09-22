using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal sealed class CascadeLODPopup : EditorWindow
    {
        CascadeSession session;
        Action changed;
        GameObject document;
        Vector2 scroll;

        public static void Open(CascadeSession session, Action changed)
        {
            var window = CreateInstance<CascadeLODPopup>();
            window.session = session;
            window.document = session.Root;
            window.changed = changed;
            window.titleContent = new GUIContent("Distance LOD");
            window.minSize = new Vector2(720, 400);
            window.ShowUtility();
        }

        void OnGUI()
        {
            if (session == null || !document || session.Root != document) { Close(); return; }
            EditorGUILayout.HelpBox("按特效根节点到相机的世界距离选择档位。配置随主窗口“保存”写入 Prefab；预览档位只影响模拟副本。", MessageType.Info);
            var lod = session.Root.GetComponent<ParticleDistanceLOD>();
            if (!lod)
            {
                if (GUILayout.Button("添加 Distance LOD（0 / 20 / 50）"))
                {
                    Undo.AddComponent<ParticleDistanceLOD>(session.Root);
                    changed?.Invoke();
                }
                return;
            }
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                var serialized = new SerializedObject(lod);
                serialized.Update();
                foreach (string property in new[] { "method", "distanceCamera", "distanceCheckTime", "hysteresis", "directLOD", "levels" })
                    EditorGUILayout.PropertyField(serialized.FindProperty(property), true);
                if (serialized.ApplyModifiedProperties()) changed?.Invoke();
                DrawQualityMatrix(lod);
            }
            EditorGUILayout.HelpBox("LOD 0 通常保持乘数 1。后续距离按升序设置；乱序阈值运行时按前一阈值取最大值。关闭 Emission 让存量粒子自然结束；降低 Max Particles 可能截断存量。", MessageType.None);
        }

        void DrawQualityMatrix(ParticleDistanceLOD lod)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("画质分级 · Emitter 启用", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("勾选表示该画质下启用。未配置、新增画质默认启用。嵌套 Prefab 和其他 LOD 控制器管理的发射器只读。取消勾选会立即停止并清空粒子。", MessageType.Info);
            var names = QualitySettings.names;
            var emitters = session.Emitters;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Emitter", GUILayout.Width(180));
                foreach (string name in names)
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(85)))
                    {
                        GUILayout.Label(name, EditorStyles.miniBoldLabel, GUILayout.Width(85));
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("全开", GUILayout.Width(40))) EditQuality(lod, emitters, name, true);
                            if (GUILayout.Button("全关", GUILayout.Width(40))) EditQuality(lod, emitters, name, false);
                        }
                    }
                }
            }
            foreach (var emitter in emitters)
                using (new EditorGUI.DisabledScope(!CanEditQuality(session, lod, emitter)))
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(emitter.name, AnimationUtility.CalculateTransformPath(emitter.transform, document.transform)), GUILayout.Width(180));
                    foreach (string name in names)
                    {
                        bool allowed = lod.IsEmitterAllowed(emitter, name);
                        bool next = GUILayout.Toggle(allowed, GUIContent.none, GUILayout.Width(85));
                        if (next != allowed) EditQuality(lod, new[] { emitter }, name, next);
                    }
                }
        }

        void EditQuality(ParticleDistanceLOD lod, IEnumerable<ParticleSystem> emitters, string name, bool enabled)
        {
            if (SetEmitterQuality(session, lod, emitters, name, enabled)) changed?.Invoke();
        }

        internal static bool CanEditQuality(CascadeSession session, ParticleDistanceLOD lod, ParticleSystem emitter)
        {
            return session.Root && lod && lod.gameObject == session.Root && emitter && !session.IsReadOnly(emitter) &&
                emitter.GetComponentInParent<ParticleDistanceLOD>(true) == lod;
        }

        internal static bool SetEmitterQuality(CascadeSession session, ParticleDistanceLOD lod,
            IEnumerable<ParticleSystem> emitters, string name, bool enabled)
        {
            var targets = emitters.Where(p => CanEditQuality(session, lod, p) && lod.IsEmitterAllowed(p, name) != enabled).Distinct().ToArray();
            if (targets.Length == 0) return false;
            Undo.RecordObject(lod, "Change emitter quality");
            if (lod.emitterQuality == null) lod.emitterQuality = new List<ParticleEmitterQuality>();
            foreach (var target in targets)
            {
                var rule = lod.emitterQuality.FirstOrDefault(r => r != null && r.emitter == target);
                if (rule == null) { rule = new ParticleEmitterQuality { emitter = target }; lod.emitterQuality.Add(rule); }
                if (!enabled)
                {
                    if (rule.disabledQualities == null) rule.disabledQualities = new List<string>();
                    if (!rule.disabledQualities.Contains(name)) rule.disabledQualities.Add(name);
                }
                else foreach (var match in lod.emitterQuality)
                    if (match != null && match.emitter == target) match.disabledQualities?.RemoveAll(q => q == name);
            }
            EditorUtility.SetDirty(lod);
            session.MarkDirty();
            return true;
        }
    }
}
