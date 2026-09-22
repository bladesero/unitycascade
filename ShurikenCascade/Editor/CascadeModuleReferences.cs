using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    internal static class CascadeModuleReferences
    {
        internal static Type ReferenceType(string path)
        {
            if (path.StartsWith("SubModule.subEmitters.", StringComparison.Ordinal) && path.EndsWith(".emitter", StringComparison.Ordinal)) return typeof(ParticleSystem);
            if (path.StartsWith("CollisionModule.m_Planes.", StringComparison.Ordinal)) return typeof(Transform);
            if (path.StartsWith("TriggerModule.primitives.", StringComparison.Ordinal)) return typeof(Component);
            if (path.StartsWith("ExternalForcesModule.influenceList.", StringComparison.Ordinal)) return typeof(ParticleSystemForceField);
            if (path == "LightsModule.light") return typeof(Light);
            return null;
        }

        static bool Matches(string path, Object value)
        {
            Type type = ReferenceType(path);
            if (type == null) return true;
            if (path.StartsWith("TriggerModule.primitives.", StringComparison.Ordinal)) return value is Collider || value is Collider2D;
            return type.IsInstanceOfType(value);
        }

        internal static Component[] Candidates(CascadeSession session, string path)
        {
            if (session == null || !session.Root || ReferenceType(path) == null) return Array.Empty<Component>();
            return session.Root.GetComponentsInChildren<Component>(true).Where(c => c && Matches(path, c)).ToArray();
        }

        internal static bool ValidateReference(CascadeSession session, ParticleSystem emitter, string path, Object value, out string reason)
        {
            reason = null;
            if (!value || ReferenceType(path) == null) return true;
            if (!Matches(path, value)) { reason = "对象类型与模块字段不匹配。"; return false; }
            if (path == "LightsModule.light" && EditorUtility.IsPersistent(value)) return true;
            var component = value as Component;
            if (session == null || !session.Root || !component || !component.transform.IsChildOf(session.Root.transform))
            { reason = "请选择当前 Prefab 内的组件；不能引用场景或其他 Prefab 的实例。"; return false; }
            if (value == emitter && ReferenceType(path) == typeof(ParticleSystem))
            { reason = "发射器不能把自己设为 Sub Emitter。"; return false; }
            return true;
        }

        internal static bool ValidateSubEmitters(CascadeSession session, ParticleSystem emitter, SerializedObject pending, out string reason)
        {
            reason = null;
            var list = pending.FindProperty("SubModule.subEmitters");
            if (list == null) { reason = "Sub Emitters 字段不存在。"; return false; }
            for (int i = 0; i < list.arraySize; i++)
            {
                var reference = list.GetArrayElementAtIndex(i).FindPropertyRelative("emitter");
                var child = reference.objectReferenceValue as ParticleSystem;
                if (!ValidateReference(session, emitter, reference.propertyPath, reference.objectReferenceValue, out reason)) return false;
                if (!child) continue;
                // Include dormant links as well: enabling a previously disabled module must be safe.
                var visited = new HashSet<ParticleSystem>();
                var stack = new Stack<ParticleSystem>();
                stack.Push(child);
                while (stack.Count > 0)
                {
                    var next = stack.Pop();
                    if (next == emitter) { reason = "此 Sub Emitter 引用会形成循环；请先移除反向引用。"; return false; }
                    if (!next || !visited.Add(next)) continue;
                    var subs = next.subEmitters;
                    for (int j = 0; j < subs.subEmittersCount; j++)
                        if (subs.GetSubEmitterSystem(j)) stack.Push(subs.GetSubEmitterSystem(j));
                }
            }
            return true;
        }
    }

    public static partial class CascadeModules
    {
        static string referenceError;
        static int referenceErrorTarget;

        static void SetReferenceError(Object target, string message)
        {
            referenceErrorTarget = target ? target.GetInstanceID() : 0;
            referenceError = message;
        }

        static void DrawReferenceError(Object target)
        {
            if (target && target.GetInstanceID() == referenceErrorTarget && !string.IsNullOrEmpty(referenceError))
                EditorGUILayout.HelpBox(referenceError, MessageType.Warning);
        }

        static bool DrawModuleReference(SerializedProperty property, CascadeSession session, GUIContent label)
        {
            Type type = CascadeModuleReferences.ReferenceType(property.propertyPath);
            if (type == null) return false;
            var before = property.objectReferenceValue;
            Object candidate;
            using (new EditorGUILayout.HorizontalScope())
            {
                candidate = EditorGUILayout.ObjectField(label, before, type, true);
                var items = CascadeModuleReferences.Candidates(session, property.propertyPath);
                var names = new string[items.Length + 2];
                names[0] = "Prefab…"; names[1] = "None";
                for (int i = 0; i < items.Length; i++)
                    names[i + 2] = session.DisplayPath(items[i].transform).Replace('/', '›') + " [" + CascadeSession.Key(items[i].transform, session.Root.transform) + "] " + items[i].GetType().Name;
                int selected = EditorGUILayout.Popup(0, names, GUILayout.Width(82));
                if (selected == 1) candidate = null;
                else if (selected > 1) candidate = items[selected - 2];
            }
            if (candidate == before) return true;
            var emitter = property.serializedObject.targetObject as ParticleSystem;
            if (!CascadeModuleReferences.ValidateReference(session, emitter, property.propertyPath, candidate, out string reason))
            { SetReferenceError(emitter, reason); return true; }
            property.objectReferenceValue = candidate;
            if (type == typeof(ParticleSystem) && !CascadeModuleReferences.ValidateSubEmitters(session, emitter, property.serializedObject, out reason))
            {
                property.objectReferenceValue = before;
                SetReferenceError(emitter, reason);
            }
            else SetReferenceError(emitter, null);
            return true;
        }
    }
}
