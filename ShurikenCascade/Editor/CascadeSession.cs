using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    // The loaded prefab is the authoring document. Nothing in the preview may mutate it.
    [Serializable]
    public sealed class CascadeSession
    {
        [SerializeField] GameObject root;
        [SerializeField] string path;
        [SerializeField] string sourceHash;
        [SerializeField] bool dirty;
        [SerializeField] string recoveryPath;
        [SerializeField] string observedState;
        [Serializable] sealed class Origin { public Object target; public string key; }
        [Serializable] sealed class RecoveryOrigin { public string key; public int component; public string originalKey; }
        [SerializeField] List<Origin> origins = new List<Origin>();
        [SerializeField] List<RecoveryOrigin> recoveryOrigins = new List<RecoveryOrigin>();
        public GameObject Root => root;
        public string Path => path;
        public bool Dirty => dirty;
        public ParticleSystem[] Emitters => root ? root.GetComponentsInChildren<ParticleSystem>(true) : Array.Empty<ParticleSystem>();

        public static string ValidateAsset(GameObject asset)
        {
            if (!asset || !EditorUtility.IsPersistent(asset)) return "请选择 Project 中的 Prefab 资产。";
            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Regular) return "首版仅支持普通 Prefab，暂不支持 Variant 或 Model Prefab。";
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return "请选择 Assets 下可编辑的 Prefab。";
            if (!asset.GetComponentInChildren<ParticleSystem>(true)) return "此 Prefab 中没有 ParticleSystem。";
            return null;
        }

        public void Open(GameObject asset)
        {
            string error = ValidateAsset(asset);
            if (error != null) throw new InvalidOperationException(error);
            string nextPath = AssetDatabase.GetAssetPath(asset);
            GameObject nextRoot = PrefabUtility.LoadPrefabContents(nextPath);
            Close();
            root = nextRoot;
            IsolateDocument();
            path = nextPath;
            sourceHash = Hash(path);
            dirty = false;
            origins = FindOrigins(root, path);
            observedState = Fingerprint();
        }

        public void MarkDirty() { dirty = true; observedState = Fingerprint(); }
        internal bool HasUnobservedChanges() => root && Fingerprint() != observedState;

        // The native ParticleSystem Inspector automatically plays active targets. An inactive parent
        // keeps the authoring hierarchy inert without changing any authored activeSelf value.
        void IsolateDocument()
        {
            var host = new GameObject("Cascade Authoring Container") { hideFlags = HideFlags.HideAndDontSave };
            host.SetActive(false);
            SceneManager.MoveGameObjectToScene(host, root.scene);
            root.transform.SetParent(host.transform, false);
        }

        internal bool IsActiveInDocument(Transform node)
        {
            while (node)
            {
                if (!node.gameObject.activeSelf) return false;
                if (node == root.transform) return true;
                node = node.parent;
            }
            return false;
        }

        public bool CheckUndo()
        {
            string state = Fingerprint();
            if (state == observedState) return false;
            observedState = state;
            return true;
        }

        string Fingerprint()
        {
            if (!root) return "";
            var text = new System.Text.StringBuilder();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                text.Append(EditorJsonUtility.ToJson(t.gameObject));
                foreach (var component in t.GetComponents<Component>()) if (component) text.Append(EditorJsonUtility.ToJson(component));
            }
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        }

        public bool IsReadOnly(ParticleSystem emitter)
        {
            return !emitter || !root || PrefabUtility.IsPartOfPrefabInstance(emitter.gameObject);
        }

        public bool CanChangeSubtree(ParticleSystem emitter)
        {
            return emitter && emitter.gameObject != root && !IsReadOnly(emitter) &&
                   !emitter.GetComponentsInChildren<Transform>(true).Any(t => PrefabUtility.IsPartOfPrefabInstance(t.gameObject));
        }

        public ParticleSystem Add()
        {
            if (!root) return null;
            var go = new GameObject(GameObjectUtility.GetUniqueNameForSibling(root.transform, "Emitter"));
            SceneManager.MoveGameObjectToScene(go, root.scene);
            go.transform.SetParent(root.transform, false);
            var emitter = go.AddComponent<ParticleSystem>();
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = pipeline
                ? pipeline.defaultParticleMaterial
                : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");
            Undo.RegisterCreatedObjectUndo(go, "Add emitter");
            MarkDirty();
            return emitter;
        }

        public ParticleSystem Duplicate(ParticleSystem emitter)
        {
            if (!CanChangeSubtree(emitter)) throw new InvalidOperationException("根发射器或包含嵌套 Prefab 的子树不能复制。" );
            var copy = Object.Instantiate(emitter.gameObject, emitter.transform.parent);
            copy.name = GameObjectUtility.GetUniqueNameForSibling(emitter.transform.parent, emitter.name);
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate emitter subtree");
            // Rules live on the controller, not on the copied emitter. Clone external rules
            // using hierarchy order (names need not be unique); controllers inside the copy
            // already had their references remapped by Instantiate.
            var originals = emitter.GetComponentsInChildren<ParticleSystem>(true);
            var duplicates = copy.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var lod in root.GetComponentsInChildren<ParticleDistanceLOD>(true))
            {
                if (PrefabUtility.IsPartOfPrefabInstance(lod) || lod.transform.IsChildOf(emitter.transform) ||
                    lod.transform.IsChildOf(copy.transform) || lod.emitterQuality == null) continue;
                var rules = lod.emitterQuality.Where(r => r != null && Array.IndexOf(originals, r.emitter) >= 0).ToArray();
                if (rules.Length == 0) continue;
                Undo.RecordObject(lod, "Duplicate emitter quality");
                foreach (var rule in rules)
                    lod.emitterQuality.Add(new ParticleEmitterQuality {
                        emitter = duplicates[Array.IndexOf(originals, rule.emitter)],
                        disabledQualities = rule.disabledQualities == null ? new List<string>() : new List<string>(rule.disabledQualities)
                    });
            }
            MarkDirty();
            return copy.GetComponent<ParticleSystem>();
        }

        public string DescribeDelete(ParticleSystem emitter)
        {
            var removed = new HashSet<ParticleSystem>(emitter.GetComponentsInChildren<ParticleSystem>(true));
            var nodes = emitter.GetComponentsInChildren<Transform>(true).Select(t => "• " + DisplayPath(t));
            var references = new List<string>();
            foreach (var other in Emitters)
            {
                var sub = other.subEmitters;
                for (int i = 0; i < sub.subEmittersCount; i++)
                    if (removed.Contains(sub.GetSubEmitterSystem(i))) references.Add("• " + DisplayPath(other.transform) + " → " + sub.GetSubEmitterSystem(i).name);
            }
            return "将删除以下节点：\n" + string.Join("\n", nodes) +
                   (references.Count == 0 ? "\n\n没有子发射器引用。" : "\n\n将移除以下子发射器引用：\n" + string.Join("\n", references));
        }

        public void Delete(ParticleSystem emitter) => DeleteMany(new[] { emitter });

        public void DeleteMany(IEnumerable<ParticleSystem> selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            var targets = selection.Distinct().ToArray();
            if (targets.Length == 0) return;
            foreach (var target in targets)
                if (!target || !root || !target.transform.IsChildOf(root.transform) || !CanChangeSubtree(target))
                    throw new InvalidOperationException("选中项包含根节点、嵌套 Prefab 子树或不属于当前文档的发射器，未执行删除。");
            // An ancestor and its selected descendants form one deletion, not repeated destruction.
            var roots = targets.Where(target => !targets.Any(other => other != target && target.transform.IsChildOf(other.transform))).ToArray();
            var removed = new HashSet<ParticleSystem>(roots.SelectMany(target => target.GetComponentsInChildren<ParticleSystem>(true)));
            var survivors = Emitters.Where(emitter => !removed.Contains(emitter)).ToArray();
            // Validate the entire batch before changing any references or destroying any object.
            foreach (var other in survivors.Where(IsReadOnly))
            {
                var sub = other.subEmitters;
                for (int i = 0; i < sub.subEmittersCount; i++)
                    if (removed.Contains(sub.GetSubEmitterSystem(i)))
                        throw new InvalidOperationException("嵌套 Prefab 引用了选中的发射器子树，未执行删除。");
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete emitter selection");
            try
            {
                foreach (var lod in root.GetComponentsInChildren<ParticleDistanceLOD>(true))
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(lod) || lod.emitterQuality == null ||
                        !lod.emitterQuality.Any(r => r != null && removed.Contains(r.emitter))) continue;
                    Undo.RecordObject(lod, "Remove emitter quality references");
                    lod.emitterQuality.RemoveAll(r => r != null && removed.Contains(r.emitter));
                }
                foreach (var other in survivors)
                {
                    var sub = other.subEmitters;
                    bool recorded = false;
                    for (int i = sub.subEmittersCount - 1; i >= 0; i--)
                        if (removed.Contains(sub.GetSubEmitterSystem(i)))
                        {
                            if (!recorded) Undo.RecordObject(other, "Remove sub-emitter reference");
                            recorded = true;
                            sub.RemoveSubEmitter(i);
                        }
                }
                Undo.FlushUndoRecordObjects();
                foreach (var target in roots) Undo.DestroyObjectImmediate(target.gameObject);
                Undo.CollapseUndoOperations(group);
                MarkDirty();
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        public void Save()
        {
            if (!root || !dirty) return;
            if (Hash(path) != sourceHash) throw new InvalidOperationException("源 Prefab 已在外部修改。请先还原并重新加载，避免覆盖外部修改。当前编辑仍保留。" );
            if (!AssetDatabase.IsOpenForEdit(path)) throw new IOException("Prefab 当前不可写，请检查文件权限或版本控制状态。" );
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            if (!success) throw new IOException("Prefab 保存失败；未保存编辑仍保留。" );
            sourceHash = Hash(path);
            origins = FindOrigins(root, path);
            dirty = false;
            DeleteRecovery();
            observedState = Fingerprint();
        }

        public void Reload()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Open(asset);
        }

        public string DisplayPath(Transform node)
        {
            if (!root || node == root.transform) return node.name;
            return AnimationUtility.CalculateTransformPath(node, root.transform);
        }

        // Sibling indices distinguish identically named emitters without changing prefab names.
        public static string Key(Transform node, Transform rootTransform)
        {
            var indices = new List<int>();
            while (node && node != rootTransform) { indices.Add(node.GetSiblingIndex()); node = node.parent; }
            indices.Reverse();
            return string.Join("/", indices);
        }

        public void WriteRecovery()
        {
            if (!root || !dirty) return;
            if (string.IsNullOrEmpty(recoveryPath)) recoveryPath = "Library/ShurikenCascade/" + Guid.NewGuid().ToString("N") + ".snapshot";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(recoveryPath));
            var objects = new List<Object>();
            recoveryOrigins.Clear();
            var ids = origins.Where(o => o.target).ToDictionary(o => o.target, o => o.key);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                objects.Add(t.gameObject);
                string key = Key(t, root.transform);
                if (ids.TryGetValue(t.gameObject, out string goId)) recoveryOrigins.Add(new RecoveryOrigin { key = key, component = -1, originalKey = goId });
                var components = t.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (!components[i]) continue;
                    objects.Add(components[i]);
                    if (ids.TryGetValue(components[i], out string id)) recoveryOrigins.Add(new RecoveryOrigin { key = key, component = i, originalKey = id });
                }
            }
            InternalEditorUtility.SaveToSerializedFileAndForget(objects.ToArray(), recoveryPath, true);
        }

        public bool RestoreRecovery()
        {
            // Native authoring objects normally survive a domain reload; the disk copy is a fallback.
            if (root) return true;
            if (string.IsNullOrEmpty(recoveryPath) || !File.Exists(recoveryPath)) return false;
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(recoveryPath);
            var snapshotRoot = objects.OfType<GameObject>().FirstOrDefault(go => !go.transform.parent);
            if (!snapshotRoot) throw new IOException("无法恢复 Shuriken Cascade 临时快照：" + recoveryPath);
            Scene scene = EditorSceneManager.NewPreviewScene();
            SceneManager.MoveGameObjectToScene(snapshotRoot, scene);
            if (Hash(path) != sourceHash)
            {
                // Keep the user's edits inspectable. Save's conflict check requires a reload.
                root = snapshotRoot;
                IsolateDocument();
                dirty = true;
                return true;
            }
            GameObject fresh = null;
            try
            {
                fresh = PrefabUtility.LoadPrefabContents(path);
                var freshOrigins = FindOrigins(fresh, path);
                RestoreOntoOriginal(snapshotRoot, fresh, freshOrigins);
                root = fresh;
                IsolateDocument();
                origins = freshOrigins;
            }
            catch
            {
                if (fresh) EditorSceneManager.ClosePreviewScene(fresh.scene);
                throw;
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            dirty = true;
            observedState = Fingerprint();
            return true;
        }

        static List<Origin> FindOrigins(GameObject document, string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            var assetNodes = asset.GetComponentsInChildren<Transform>(true).ToDictionary(t => Key(t, asset.transform));
            var result = new List<Origin>();
            foreach (var node in document.GetComponentsInChildren<Transform>(true))
            {
                if (!assetNodes.TryGetValue(Key(node, document.transform), out var source)) continue;
                string key = Key(node, document.transform);
                result.Add(new Origin { target = node.gameObject, key = key + ":go" });
                var from = source.GetComponents<Component>();
                var to = node.GetComponents<Component>();
                for (int i = 0; i < Math.Min(from.Length, to.Length); i++)
                    if (from[i] && to[i] && from[i].GetType() == to[i].GetType())
                        result.Add(new Origin { target = to[i], key = key + ":" + i });
            }
            return result;
        }

        void RestoreOntoOriginal(GameObject snapshot, GameObject fresh, List<Origin> freshOrigins)
        {
            // Keys address the unchanged source hierarchy, not the edited/snapshot hierarchy.
            // Unlike source-file IDs alone, these also distinguish repeated nested instances.
            var originals = freshOrigins.ToDictionary(o => o.key, o => o.target);
            var nodes = snapshot.GetComponentsInChildren<Transform>(true).ToDictionary(t => Key(t, snapshot.transform));
            var remap = new Dictionary<Object, Object>();
            foreach (var origin in recoveryOrigins)
            {
                if (!nodes.TryGetValue(origin.key, out var node) || !originals.TryGetValue(origin.originalKey, out var target)) continue;
                Object from = origin.component < 0 ? (Object)node.gameObject : node.GetComponents<Component>()[origin.component];
                if (from) remap[from] = target;
            }
            if (!remap.TryGetValue(snapshot, out var restoredRoot) || restoredRoot != fresh)
                throw new IOException("恢复快照缺少原 Prefab 的根节点标识。源资产未被修改。" );

            // Clone newly added subtrees only. Existing native objects must keep their local IDs.
            foreach (var node in nodes.Values)
            {
                if (remap.ContainsKey(node.gameObject)) continue;
                var parent = (Transform)remap[node.parent];
                var copy = Object.Instantiate(node.gameObject, parent, false);
                var sourceNodes = node.GetComponentsInChildren<Transform>(true);
                var copyNodes = copy.GetComponentsInChildren<Transform>(true);
                for (int j = 0; j < sourceNodes.Length; j++)
                {
                    remap[sourceNodes[j].gameObject] = copyNodes[j].gameObject;
                    var from = sourceNodes[j].GetComponents<Component>();
                    var to = copyNodes[j].GetComponents<Component>();
                    for (int k = 0; k < from.Length; k++) if (from[k] && to[k]) remap[from[k]] = to[k];
                }
            }
            foreach (var node in nodes.Values)
            {
                var target = (Transform)remap[node];
                if (node != snapshot.transform)
                {
                    target.SetParent((Transform)remap[node.parent], false);
                    target.SetSiblingIndex(node.GetSiblingIndex());
                }
            }
            var retained = new HashSet<Object>(remap.Values);
            foreach (var t in fresh.GetComponentsInChildren<Transform>(true).Reverse())
                if (t && !retained.Contains(t.gameObject)) Object.DestroyImmediate(t.gameObject);
            foreach (var pair in remap)
            {
                // Nested prefab content was read-only in the document. Keep its native instance data.
                var go = pair.Value as GameObject;
                if (pair.Value is Component component) go = component.gameObject;
                if (go && PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                CopyFields(pair.Key, pair.Value, remap);
            }
        }

        static readonly HashSet<string> IdentityFields = new HashSet<string>
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_GameObject", "m_Component", "m_Father", "m_Children", "m_RootOrder", "m_Script"
        };

        static void CopyFields(Object from, Object to, Dictionary<Object, Object> remap)
        {
            using (var source = new SerializedObject(from))
            using (var target = new SerializedObject(to))
            {
                var field = source.GetIterator();
                if (field.Next(true)) do
                {
                    if (!IdentityFields.Contains(field.name)) target.CopyFromSerializedProperty(field);
                } while (field.Next(false));
                field = target.GetIterator();
                while (field.Next(true))
                    if (field.propertyType == SerializedPropertyType.ObjectReference && field.objectReferenceValue &&
                        !IdentityFields.Contains(field.name) && remap.TryGetValue(field.objectReferenceValue, out var mapped)) field.objectReferenceValue = mapped;
                target.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        public void Close()
        {
            if (root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    Undo.ClearUndo(t.gameObject);
                    foreach (var c in t.GetComponents<Component>()) if (c) Undo.ClearUndo(c);
                }
                Scene scene = root.scene;
                if (scene.IsValid() && EditorSceneManager.IsPreviewScene(scene)) EditorSceneManager.ClosePreviewScene(scene);
                else Object.DestroyImmediate(root);
            }
            root = null;
            origins.Clear();
            path = null;
            dirty = false;
            DeleteRecovery();
        }

        void DeleteRecovery()
        {
            if (!string.IsNullOrEmpty(recoveryPath) && File.Exists(recoveryPath)) File.Delete(recoveryPath);
            recoveryPath = null;
            recoveryOrigins.Clear();
        }

        static string Hash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath)) return "missing";
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(assetPath)));
        }
    }
}
