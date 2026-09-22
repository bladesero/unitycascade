using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    // A native serialized snapshot preserves curves, gradients, arrays and asset references.
    // Only the chosen module is transferred; an emitter's identity/hierarchy is never copied.
    internal sealed class CascadeModuleClipboard : IDisposable
    {
        struct Reference
        {
            public string path;
            public Object value;
            public int self; // 1=GameObject, 2=Transform, 3=ParticleSystem, 4=Renderer
        }

        Scene scene;
        ParticleSystem snapshot;
        string[] paths;
        readonly List<Reference> references = new List<Reference>();
        public string ModulePath { get; private set; }
        public string Label { get; private set; }

        internal static Object Target(ParticleSystem emitter, string path)
        {
            if (!emitter) return null;
            if (path == "$transform") return emitter.transform;
            if (path == "$renderer") return emitter.GetComponent<ParticleSystemRenderer>();
            return emitter;
        }

        static string[] CopyPaths(SerializedObject so, string path)
        {
            if (path == "$transform") return new[] { "m_LocalPosition", "m_LocalRotation", "m_LocalScale" };
            if (path == "InitialModule") return new[] { path }.Concat(CascadeModules.MainFields).ToArray();
            if (path != "$renderer") return new[] { path };
            var result = new List<string>();
            var field = so.GetIterator();
            if (field.NextVisible(true)) do
            {
                if (field.name != "m_ObjectHideFlags" && field.name != "m_GameObject" && field.name != "m_Script") result.Add(field.propertyPath);
            } while (field.NextVisible(false));
            return result.ToArray();
        }

        public void Copy(ParticleSystem source, CascadeModules.Module module)
        {
            if (!source || !module.Supported || !Target(source, module.Path)) throw new InvalidOperationException("当前模块不可复制。" );
            Dispose();
            try
            {
                scene = EditorSceneManager.NewPreviewScene();
                var go = new GameObject("Cascade Module Clipboard") { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false);
                SceneManager.MoveGameObjectToScene(go, scene);
                snapshot = go.AddComponent<ParticleSystem>();
                ModulePath = module.Path;
                Label = source.name + " / " + module.Name;
                using (var from = new SerializedObject(Target(source, ModulePath)))
                using (var to = new SerializedObject(Target(snapshot, ModulePath)))
                {
                    paths = CopyPaths(from, ModulePath);
                    foreach (string path in paths)
                    {
                        var field = from.FindProperty(path);
                        if (field == null || to.FindProperty(path) == null) throw new InvalidOperationException("模块字段不存在：" + path);
                        to.CopyFromSerializedProperty(field);
                    }
                    to.ApplyModifiedPropertiesWithoutUndo();
                    var it = from.GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference || !it.objectReferenceValue || !Included(it.propertyPath)) continue;
                        var value = it.objectReferenceValue;
                        int self = value == source.gameObject ? 1 : value == source.transform ? 2 : value == source ? 3 : value == source.GetComponent<ParticleSystemRenderer>() ? 4 : 0;
                        references.Add(new Reference { path = it.propertyPath, value = value, self = self });
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        bool Included(string path) => paths.Any(p => path == p || path.StartsWith(p + ".", StringComparison.Ordinal));

        static Object Resolve(Reference reference, ParticleSystem emitter)
        {
            switch (reference.self)
            {
                case 1: return emitter.gameObject;
                case 2: return emitter.transform;
                case 3: return emitter;
                case 4: return emitter.GetComponent<ParticleSystemRenderer>();
                default: return reference.value;
            }
        }

        public bool CanPaste(CascadeSession session, ParticleSystem emitter, CascadeModules.Module module, out string reason)
        {
            reason = null;
            if (!snapshot) reason = "先复制一个模块。";
            else if (!module.Supported || module.Path != ModulePath) reason = "只能粘贴到相同类型的模块。";
            else if (session == null || !session.Root || !emitter || !emitter.transform.IsChildOf(session.Root.transform)) reason = "目标不属于当前 Prefab。";
            else if (session.IsReadOnly(emitter)) reason = "嵌套 Prefab 为只读，不能粘贴。";
            else if (!Target(emitter, ModulePath)) reason = "目标组件不存在。";
            if (reason != null) return false;
            foreach (var reference in references)
            {
                var value = Resolve(reference, emitter);
                if (!value) { reason = "复制的对象引用已失效，请重新复制：" + reference.path; return false; }
                if (!CascadeModuleReferences.ValidateReference(session, emitter, reference.path, value, out reason)) return false;
                if (EditorUtility.IsPersistent(value)) continue;
                var go = value as GameObject;
                if (value is Component component) go = component.gameObject;
                if (!go || !go.transform.IsChildOf(session.Root.transform))
                { reason = "模块含其他 Prefab 或场景的临时对象引用，不能粘贴：" + reference.path; return false; }
            }
            if (ModulePath == "SubModule")
            {
                using (var from = new SerializedObject(snapshot))
                using (var pending = new SerializedObject(emitter))
                {
                    pending.CopyFromSerializedProperty(from.FindProperty(ModulePath));
                    foreach (var reference in references) pending.FindProperty(reference.path).objectReferenceValue = Resolve(reference, emitter);
                    if (!CascadeModuleReferences.ValidateSubEmitters(session, emitter, pending, out reason)) return false;
                }
            }
            return true;
        }

        public void Paste(CascadeSession session, ParticleSystem emitter, CascadeModules.Module module)
        {
            if (!CanPaste(session, emitter, module, out string reason)) throw new InvalidOperationException(reason);
            using (var from = new SerializedObject(Target(snapshot, ModulePath)))
            using (var to = new SerializedObject(Target(emitter, ModulePath)))
            {
                foreach (string path in paths)
                    if (to.FindProperty(path) == null) throw new InvalidOperationException("目标模块字段不存在：" + path);
                foreach (string path in paths) to.CopyFromSerializedProperty(from.FindProperty(path));
                foreach (var reference in references) to.FindProperty(reference.path).objectReferenceValue = Resolve(reference, emitter);
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Paste " + module.Name);
                if (to.ApplyModifiedProperties()) session.MarkDirty();
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
        }

        public void Dispose()
        {
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            scene = default;
            snapshot = null;
            paths = null;
            references.Clear();
            ModulePath = Label = null;
        }
    }
}
