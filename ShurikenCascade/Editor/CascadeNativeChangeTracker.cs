using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    // One validator per authoring document, shared by every visible native Inspector.
    internal sealed class CascadeNativeChangeTracker : IDisposable
    {
        sealed class ProtectedObject
        {
            internal Object Target;
            internal string Json;
            internal Transform Parent;
            internal int Sibling;
        }

        CascadeSession session;
        GameObject root;
        readonly List<ProtectedObject> protectedObjects = new List<ProtectedObject>();
        readonly Dictionary<Object, Dictionary<string, Object>> references = new Dictionary<Object, Dictionary<string, Object>>();
        readonly Dictionary<Object, int> versions = new Dictionary<Object, int>();
        internal string Message { get; private set; }
        internal int FullCheckCount { get; private set; }
        internal int BaselineCount { get; private set; }

        internal void Bind(CascadeSession document)
        {
            if (session == document && root == document.Root) return;
            Dispose(); session = document; root = document.Root;
            RefreshBaseline(); BaselineCount++;
        }

        void CaptureProtection()
        {
            protectedObjects.Clear();
            foreach (var node in session.Root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(node.gameObject)) continue;
                protectedObjects.Add(new ProtectedObject { Target = node.gameObject, Json = EditorJsonUtility.ToJson(node.gameObject) });
                foreach (var component in node.GetComponents<Component>())
                    if (component) protectedObjects.Add(new ProtectedObject {
                        Target = component, Json = EditorJsonUtility.ToJson(component), Parent = node.parent, Sibling = node.GetSiblingIndex()
                    });
            }
        }

        void RestoreProtectedObjects()
        {
            foreach (var item in protectedObjects)
            {
                if (!item.Target) continue;
                if (item.Target is Transform transform && (transform.parent != item.Parent || transform.GetSiblingIndex() != item.Sibling))
                {
                    transform.SetParent(item.Parent, false); transform.SetSiblingIndex(item.Sibling);
                }
                if (EditorJsonUtility.ToJson(item.Target) == item.Json) continue;
                EditorJsonUtility.FromJsonOverwrite(item.Json, item.Target);
                Message = "Unity 的联动修改涉及嵌套 Prefab，已保留嵌套内容的原始数据。";
            }
        }

        static bool IsEditableReference(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.ObjectReference &&
                property.name != "m_GameObject" && property.name != "m_CorrespondingSourceObject" &&
                property.name != "m_PrefabInstance" && property.name != "m_PrefabAsset" && property.name != "m_Script";
        }

        void CaptureReferences()
        {
            references.Clear();
            foreach (var emitter in session.Emitters)
            {
                Capture(emitter);
                var renderer = emitter.GetComponent<ParticleSystemRenderer>();
                if (renderer) Capture(renderer);
            }
        }

        void Capture(Object obj)
        {
            var values = new Dictionary<string, Object>();
            using (var so = new SerializedObject(obj))
            {
                var property = so.GetIterator();
                while (property.Next(true)) if (IsEditableReference(property)) values[property.propertyPath] = property.objectReferenceValue;
            }
            references[obj] = values;
        }

        void ValidateReferences()
        {
            foreach (var pair in references)
            {
                if (!pair.Key) continue;
                using (var so = new SerializedObject(pair.Key))
                {
                    var property = so.GetIterator();
                    var emitter = pair.Key as ParticleSystem;
                    bool subChanged = false;
                    while (property.Next(true))
                    {
                        if (!IsEditableReference(property)) continue;
                        pair.Value.TryGetValue(property.propertyPath, out var before);
                        var value = property.objectReferenceValue;
                        if (value == before) continue;
                        if (property.propertyPath.StartsWith("SubModule.", StringComparison.Ordinal)) subChanged = true;
                        bool valid = CascadeModuleReferences.ValidateReference(session, emitter, property.propertyPath, value, out string reason);
                        if (valid && value && !EditorUtility.IsPersistent(value))
                        {
                            var go = value as GameObject;
                            if (value is Component component) go = component.gameObject;
                            valid = go && go.transform.IsChildOf(session.Root.transform);
                            if (!valid) reason = "不能引用当前场景或其他工作副本的对象。";
                        }
                        if (valid) continue;
                        property.objectReferenceValue = before;
                        Message = reason;
                    }
                    if (emitter && subChanged && !CascadeModuleReferences.ValidateSubEmitters(session, emitter, so, out string subReason))
                    {
                        var list = so.FindProperty("SubModule.subEmitters");
                        for (int i = 0; i < list.arraySize; i++)
                        {
                            var reference = list.GetArrayElementAtIndex(i).FindPropertyRelative("emitter");
                            pair.Value.TryGetValue(reference.propertyPath, out var before);
                            reference.objectReferenceValue = before;
                        }
                        // Array row removal/reordering can make the old positional mapping invalid.
                        if (!CascadeModuleReferences.ValidateSubEmitters(session, emitter, so, out _))
                            for (int i = 0; i < list.arraySize; i++) list.GetArrayElementAtIndex(i).FindPropertyRelative("emitter").objectReferenceValue = null;
                        Message = subReason;
                    }
                    if (so.hasModifiedProperties) so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        // Poll cheaply by dirty versions; save/close and explicit callers can force a full check.
        // Native popup commits are not necessarily delivered through this window's OnGUI.
        internal bool ObserveChanges(bool force = true)
        {
            if (session == null || !session.Root || (!force && !VersionsChanged())) return false;
            FullCheckCount++;
            if (!session.HasUnobservedChanges()) { CaptureVersions(); return false; }
            RestoreProtectedObjects();
            ValidateReferences();
            bool changed = session.CheckUndo();
            if (changed) session.MarkDirty();
            CaptureReferences(); CaptureVersions();
            return changed;
        }

        internal void RefreshBaseline()
        {
            if (session == null || !session.Root) return;
            CaptureProtection(); CaptureReferences(); CaptureVersions();
        }

        void CaptureVersions()
        {
            versions.Clear();
            foreach (var node in session.Root.GetComponentsInChildren<Transform>(true))
            {
                versions[node.gameObject] = EditorUtility.GetDirtyCount(node.gameObject);
                foreach (var component in node.GetComponents<Component>())
                    if (component) versions[component] = EditorUtility.GetDirtyCount(component);
            }
        }

        bool VersionsChanged()
        {
            foreach (var pair in versions)
                if (!pair.Key || EditorUtility.GetDirtyCount(pair.Key) != pair.Value) return true;
            return false;
        }

        public void Dispose()
        {
            session = null; root = null; Message = null;
            protectedObjects.Clear(); references.Clear(); versions.Clear();
        }
    }
}
