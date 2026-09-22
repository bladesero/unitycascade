using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    internal sealed class CascadeResourceUse
    {
        internal readonly string Node, Component, Property;
        internal readonly ParticleSystem Emitter;
        internal readonly bool Environment, Inactive, Nested;
        internal CascadeResourceUse(string node, string component, string property, ParticleSystem emitter, bool environment, bool inactive, bool nested)
        { Node = node; Component = component; Property = property; Emitter = emitter; Environment = environment; Inactive = inactive; Nested = nested; }
        internal CascadeResourceUse Via(string suffix) => new CascadeResourceUse(Node, Component, Property + " → " + suffix, Emitter, Environment, Inactive, Nested);
    }

    internal sealed class CascadeResourceNode
    {
        internal readonly Object Asset;
        internal readonly string Id, Name, Path, Fingerprint;
        internal readonly CascadeMemoryEstimate Memory;
        internal IReadOnlyList<CascadeResourceUse> Uses { get; private set; }
        internal IReadOnlyList<string> Textures { get; private set; }
        internal bool Effect => Uses.Any(u => !u.Environment);
        internal bool Environment => Uses.Any(u => u.Environment);
        internal CascadeResourceNode(Object asset, string id, List<CascadeResourceUse> uses, List<string> textures)
        {
            Asset = asset; Id = id; Name = asset.name; Path = AssetDatabase.GetAssetPath(asset); Memory = CascadeGpuMemory.Estimate(asset);
            Uses = uses.AsReadOnly(); Textures = textures.AsReadOnly();
            Fingerprint = asset is Material ? EditorJsonUtility.ToJson(asset) : "";
        }
    }

    internal readonly struct CascadeResourceTotals
    {
        internal readonly long Textures, Meshes, RenderTextures;
        internal readonly int Unknown;
        internal long Total => Textures + Meshes + RenderTextures;
        internal CascadeResourceTotals(IEnumerable<CascadeResourceNode> nodes)
        {
            long textures = 0, meshes = 0, targets = 0; int unknown = 0;
            foreach (var node in nodes)
            {
                if (!(node.Asset is Texture) && !(node.Asset is Mesh)) continue;
                if (!node.Memory.Known) { unknown++; continue; }
                if (node.Asset is RenderTexture) targets += node.Memory.Bytes;
                else if (node.Asset is Texture) textures += node.Memory.Bytes;
                else meshes += node.Memory.Bytes;
            }
            Textures = textures; Meshes = meshes; RenderTextures = targets; Unknown = unknown;
        }
    }

    internal sealed class CascadeResourceIndex
    {
        sealed class Builder
        {
            internal Object Asset;
            internal readonly List<CascadeResourceUse> Uses = new List<CascadeResourceUse>();
            internal readonly List<string> Textures = new List<string>();
        }
        internal IReadOnlyList<CascadeResourceNode> Nodes { get; private set; } = Array.AsReadOnly(Array.Empty<CascadeResourceNode>());
        internal CascadeResourceTotals Effect { get; private set; }
        internal CascadeResourceTotals Environment { get; private set; }
        internal int Revision { get; private set; }
        internal int ScanCount { get; private set; }
        readonly Dictionary<string, Builder> building = new Dictionary<string, Builder>();
        readonly Dictionary<string, CascadeResourceNode> lookup = new Dictionary<string, CascadeResourceNode>();
        internal static string Identity(Object asset)
        {
            if (!asset) return "";
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long local) && !string.IsNullOrEmpty(guid)
                ? guid + ":" + local : "instance:" + asset.GetInstanceID();
        }
        internal CascadeResourceNode Find(string id) => id != null && lookup.TryGetValue(id, out var node) ? node : null;
        internal void Rebuild(GameObject root, VolumeProfile environment)
        {
            building.Clear(); lookup.Clear(); ScanCount++;
            if (root)
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (!component) continue;
                    var transform = component.transform;
                    bool inactive = false;
                    for (var t = transform; t; t = t == root.transform ? null : t.parent) inactive |= !t.gameObject.activeSelf;
                    var names = new List<string>();
                    for (var t = transform; t; t = t == root.transform ? null : t.parent) names.Add(t.name);
                    names.Reverse();
                    string node = string.Join("/", names) + " [" + CascadeSession.Key(transform, root.transform) + "]";
                    var emitter = component.GetComponent<ParticleSystem>() ?? component.GetComponentInParent<ParticleSystem>(true);
                    int componentIndex = Array.IndexOf(component.GetComponents<Component>(), component);
                    var use = new CascadeResourceUse(node, component.GetType().Name + " #" + componentIndex, "", emitter, false, inactive,
                        PrefabUtility.IsPartOfPrefabInstance(component));
                    Scan(component, use, component is Renderer);
                    if (component is Renderer renderer)
                    {
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++) Add(materials[i], use.Via("Material[" + i + "]"));
                        if (renderer is ParticleSystemRenderer particleRenderer) Add(particleRenderer.trailMaterial, use.Via("Trail Material"));
                    }
                }
            if (environment)
                foreach (var component in environment.components)
                    if (component) Scan(component, new CascadeResourceUse("预览环境", component.GetType().Name, "", null, true, !component.active, false), false);
            var nodes = new List<CascadeResourceNode>();
            foreach (var pair in building)
            {
                var b = pair.Value; var node = new CascadeResourceNode(b.Asset, pair.Key, b.Uses, b.Textures);
                nodes.Add(node); lookup.Add(node.Id, node);
            }
            nodes.Sort((a, b) => string.CompareOrdinal(a.Name + a.Id, b.Name + b.Id));
            Nodes = nodes.AsReadOnly(); Effect = new CascadeResourceTotals(nodes.Where(n => n.Effect));
            Environment = new CascadeResourceTotals(nodes.Where(n => n.Environment)); Revision++; building.Clear();
        }
        void Scan(Object source, CascadeResourceUse use, bool skipMaterials)
        {
            using (var serialized = new SerializedObject(source))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var asset = property.objectReferenceValue;
                    if (skipMaterials && asset is Material) continue;
                    if (asset is Material || asset is Texture || asset is Mesh || asset is Sprite)
                        Add(asset, use.Via(property.propertyPath));
                }
            }
        }
        void Add(Object asset, CascadeResourceUse use)
        {
            if (!asset) return;
            string id = Identity(asset);
            if (!building.TryGetValue(id, out var builder)) building[id] = builder = new Builder { Asset = asset };
            if (builder.Uses.Any(u => u.Node == use.Node && u.Component == use.Component && u.Property == use.Property && u.Environment == use.Environment)) return;
            builder.Uses.Add(use);
            if (asset is Material material)
            {
                if (material.shader) Add(material.shader, use.Via(material.name + ".Shader"));
                foreach (string name in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(name); if (!texture) continue;
                    string textureId = Identity(texture);
                    if (!builder.Textures.Contains(textureId)) builder.Textures.Add(textureId);
                    Add(texture, use.Via(material.name + "." + name));
                }
            }
            else if (asset is Sprite sprite) Add(sprite.texture, use.Via(sprite.name + ".Texture"));
        }
        internal CascadeMemoryEstimate MaterialTextures(CascadeResourceNode material)
        {
            long bytes = 0;
            foreach (string id in material.Textures)
            {
                var texture = Find(id); if (texture == null || !texture.Memory.Known) return new CascadeMemoryEstimate("部分关联纹理容量未知");
                bytes += texture.Memory.Bytes;
            }
            return new CascadeMemoryEstimate(bytes, "关联纹理容量（跨材质共享资源在总计中去重）");
        }
    }
}
