using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    internal sealed class CascadeAnalysisPanel : IDisposable
    {
        internal readonly CascadeResourceIndex Resources = new CascadeResourceIndex();
        internal readonly CascadeShaderAnalysis Shaders = new CascadeShaderAnalysis();
        internal int Tab;
        internal bool Dirty { get; private set; } = true;
        internal GameObject Root { get; private set; }
        readonly CascadePerformancePanel performance = new CascadePerformancePanel();
        string search = "", selectedResource, selectedMaterial;
        int kind, group, resourceSort = 2, resourceRevision = -1, shaderRevision = -1, shaderSort;
        bool resourceDescending = true, shaderDescending = true;
        int subshader = -1, pass = -1, environmentRevision = -1;
        readonly List<CascadeResourceNode> filtered = new List<CascadeResourceNode>();
        readonly List<CascadeShaderVariant> results = new List<CascadeShaderVariant>();
        CascadeResourceNode[] materials = Array.Empty<CascadeResourceNode>();
        string[] materialLabels = Array.Empty<string>(), subLabels = Array.Empty<string>(), passLabels = Array.Empty<string>();
        Vector2 resourceScroll, referenceScroll, shaderScroll, diagnosticScroll;
        CascadeShaderVariant selectedVariant;
        double nextScan;
        double nextMaterialCheck;
        internal void Invalidate() { Dirty = true; }
        internal void Bind(GameObject root)
        {
            Root = root; Shaders.Clear(); selectedResource = selectedMaterial = null; selectedVariant = null;
            resourceScroll = shaderScroll = referenceScroll = diagnosticScroll = Vector2.zero;
            environmentRevision = -1; Dirty = true; nextScan = 0;
        }
        internal bool Update(GameObject root, CascadePreviewEnvironment environment)
        {
            if (Root != root) Bind(root);
            if (!Dirty && EditorApplication.timeSinceStartup >= nextMaterialCheck)
            {
                nextMaterialCheck = EditorApplication.timeSinceStartup + 0.5;
                foreach (var material in materials)
                    if (!material.Asset || EditorJsonUtility.ToJson(material.Asset) != material.Fingerprint) { Dirty = true; break; }
            }
            if (environmentRevision != environment.Revision) { environmentRevision = environment.Revision; Dirty = true; }
            bool changed = false;
            if (Dirty && EditorApplication.timeSinceStartup >= nextScan)
            {
                Resources.Rebuild(root, environment.Profile); Shaders.Refresh(Resources);
                materials = Resources.Nodes.Where(n => n.Effect && n.Asset is Material).ToArray();
                materialLabels = materials.Select(n => n.Name + " — " + (n.Asset as Material).shader?.name).ToArray();
                if (!materials.Any(n => n.Id == selectedMaterial)) selectedMaterial = materials.FirstOrDefault()?.Id;
                BuildPasses(); resourceRevision = -1; Dirty = false; nextScan = EditorApplication.timeSinceStartup + 0.1; changed = true;
            }
            return Shaders.Tick() || changed;
        }
        internal void DrawHeader()
        {
            Tab = Mathf.Clamp(Tab, 0, 2);
            Tab = GUILayout.Toolbar(Tab, new[] { "性能分析", "资源引用", "Shader 分析" }, EditorStyles.toolbarButton, GUILayout.Width(285));
        }
        internal void Draw(CascadeSession session, CascadePreview preview, CascadePreviewGizmos gizmos, ref ParticleSystem selected,
            Action<ParticleSystem> selectEmitter, Func<ParticleSystem, bool> isSelected)
        {
            if (Tab == 0) performance.Draw(session, preview, ref selected, selectEmitter, isSelected, () => DrawSummary(preview, gizmos));
            else if (Tab == 1) DrawResources(selectEmitter);
            else DrawShaders();
        }
        void DrawSummary(CascadePreview preview, CascadePreviewGizmos gizmos)
        {
            var t = Resources.Effect;
            EditorGUILayout.LabelField($"GPU 资源估算：纹理 {F(t.Textures)} · 网格 {F(t.Meshes)} · RT {F(t.RenderTextures)} · 合计 {F(t.Total)}" + (t.Unknown > 0 ? $" + {t.Unknown} 项 N/A" : ""), EditorStyles.miniLabel);
            long tool = 0; int unknown = 0; var ids = new HashSet<int>();
            foreach (var texture in new[] { preview.TargetTexture, gizmos.Overlay })
                if (texture && ids.Add(texture.GetInstanceID()))
                { var memory = CascadeGpuMemory.Estimate(texture); if (memory.Known) tool += memory.Bytes; else unknown++; }
            EditorGUILayout.LabelField($"另计：预览环境 {F(Resources.Environment.Total)}{(Resources.Environment.Unknown > 0 ? " + N/A" : "")} · 工具 RT {F(tool)}{(unknown > 0 ? " + N/A" : "")}", EditorStyles.miniLabel);
            var vs = Shaders.Maximum(ShaderType.Vertex); var ps = Shaders.Maximum(ShaderType.Fragment);
            if (GUILayout.Button($"D3D11 静态 ALU：已分析 {Shaders.KnownCount}/{Shaders.Rows.Count} 项 · VS 最大 {Alu(vs)} · PS 最大 {Alu(ps)} → Shader 分析", EditorStyles.miniButton)) Tab = 2;
            EditorGUILayout.LabelField("完整 Mip 资源容量，非实时驻留显存；未计粒子/Trails 动态缓冲、驱动、Shader 程序及 URP 内部临时 RT。", EditorStyles.wordWrappedMiniLabel);
        }
        static string Alu(CascadeShaderVariant row) => row == null ? "—" : row.Result.Instructions.Alu + " (" + row.MaterialName + "/" + row.PassName + ")";
        static string F(long bytes) => CascadeMemoryEstimate.Format(bytes);

        void FilterResources()
        {
            filtered.Clear();
            foreach (var node in Resources.Nodes)
            {
                if (group == 0 ? !node.Effect : !node.Environment) continue;
                if (kind == 1 && !(node.Asset is Material) || kind == 2 && !(node.Asset is Texture) || kind == 3 && !(node.Asset is Mesh)) continue;
                if (!string.IsNullOrEmpty(search) && (node.Name + " " + node.Path).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                filtered.Add(node);
            }
            filtered.Sort((a, b) => {
                int value = resourceSort == 0 ? string.CompareOrdinal(a.Name, b.Name) : resourceSort == 1 ? a.Uses.Count.CompareTo(b.Uses.Count) : ResourceBytes(a).CompareTo(ResourceBytes(b));
                return value == 0 ? string.CompareOrdinal(a.Id, b.Id) : resourceDescending ? -value : value;
            });
            resourceRevision = Resources.Revision;
        }
        long ResourceBytes(CascadeResourceNode node) => node.Asset is Material ? Resources.MaterialTextures(node).Bytes : node.Memory.Bytes;
        void DrawResources(Action<ParticleSystem> selectEmitter)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                group = EditorGUILayout.Popup(group, new[] { "特效资源", "预览环境" }, GUILayout.Width(95));
                kind = EditorGUILayout.Popup(kind, new[] { "全部", "材质", "纹理 / RT", "网格" }, GUILayout.Width(95));
                search = GUILayout.TextField(search, GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField, GUILayout.MinWidth(90));
                if (EditorGUI.EndChangeCheck()) { resourceRevision = -1; resourceScroll = Vector2.zero; }
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(40))) Invalidate();
            }
            if (resourceRevision != Resources.Revision) FilterResources();
            Rect area = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            var left = new Rect(area.x, area.y, area.width * 0.62f - 3, area.height);
            var right = new Rect(left.xMax + 6, area.y, Mathf.Max(1, area.width - left.width - 6), area.height);
            GUI.Box(left, GUIContent.none); GUI.Box(right, GUIContent.none);
            if (GUI.Button(new Rect(left.x, left.y, left.width * .55f, 20), "资源 / 类型", EditorStyles.toolbarButton)) ResourceSort(0);
            if (GUI.Button(new Rect(left.x + left.width * .55f, left.y, left.width * .15f, 20), "引用", EditorStyles.toolbarButton)) ResourceSort(1);
            if (GUI.Button(new Rect(left.x + left.width * .7f, left.y, left.width * .3f, 20), "容量估算", EditorStyles.toolbarButton)) ResourceSort(2);
            var body = new Rect(left.x, left.y + 21, left.width, Mathf.Max(1, left.height - 21));
            resourceScroll = GUI.BeginScrollView(body, resourceScroll, new Rect(0, 0, Mathf.Max(1, body.width - 18), filtered.Count * 42));
            int start = Mathf.Max(0, (int)(resourceScroll.y / 42)), end = Math.Min(filtered.Count, start + Mathf.CeilToInt(body.height / 42) + 1);
            for (int i = start; i < end; i++)
            {
                var node = filtered[i]; float w = body.width - 18; var row = new Rect(0, i * 42, w, 42);
                if (selectedResource == node.Id) EditorGUI.DrawRect(row, new Color(.2f, .4f, .6f, .3f));
                if (GUI.Button(new Rect(2, row.y, w * .55f, 21), new GUIContent(node.Name + " · " + node.Asset.GetType().Name, node.Path), EditorStyles.label)) { selectedResource = node.Id; referenceScroll = Vector2.zero; }
                GUI.Label(new Rect(w * .56f, row.y, w * .14f, 21), node.Uses.Count.ToString());
                var memory = node.Asset is Material ? Resources.MaterialTextures(node) : node.Memory;
                GUI.Label(new Rect(w * .7f, row.y, w * .3f, 21), new GUIContent(memory.Display, memory.Detail));
                string detail = node.Asset is Material m ? $"{m.shader?.name} · {node.Textures.Count} 纹理" : memory.Detail;
                GUI.Label(new Rect(4, row.y + 21, w - 4, 20), detail, EditorStyles.miniLabel);
            }
            GUI.EndScrollView();
            DrawReferences(right, selectEmitter);
        }
        void ResourceSort(int column) { resourceDescending = resourceSort == column ? !resourceDescending : true; resourceSort = column; resourceRevision = -1; }
        void DrawReferences(Rect area, Action<ParticleSystem> selectEmitter)
        {
            var node = Resources.Find(selectedResource);
            if (node == null) { GUI.Label(area, "选择资源查看引用来源", EditorStyles.centeredGreyMiniLabel); return; }
            GUI.Label(new Rect(area.x + 3, area.y, Mathf.Max(1, area.width - 132), 22), node.Name, EditorStyles.boldLabel);
            if (GUI.Button(new Rect(area.xMax - 130, area.y, 78, 22), "Project 定位", EditorStyles.toolbarButton)) EditorGUIUtility.PingObject(node.Asset);
            if (node.Asset is Material && GUI.Button(new Rect(area.xMax - 52, area.y, 52, 22), "Shader", EditorStyles.toolbarButton))
            { selectedMaterial = node.Id; subshader = pass = -1; BuildPasses(); Tab = 2; }
            var body = new Rect(area.x, area.y + 23, area.width, Mathf.Max(1, area.height - 23));
            float heading = node.Asset is Material ? 64 : 38;
            referenceScroll = GUI.BeginScrollView(body, referenceScroll, new Rect(0, 0, Mathf.Max(1, body.width - 18), heading + node.Uses.Count * 42));
            float width = Mathf.Max(1, body.width - 22);
            string path = string.IsNullOrEmpty(node.Path) ? "内置 / 会话资源" : node.Path;
            GUI.Label(new Rect(3, 0, width, 36), new GUIContent(path, path), EditorStyles.wordWrappedMiniLabel);
            if (node.Asset is Material material && material) GUI.Label(new Rect(3, 36, width, 28), new GUIContent("材质关键字：" + string.Join(" ", material.shaderKeywords)), EditorStyles.wordWrappedMiniLabel);
            int start = Mathf.Max(0, (int)((referenceScroll.y - heading) / 42));
            int end = Math.Min(node.Uses.Count, start + Mathf.CeilToInt(body.height / 42) + 3);
            for (int i = start; i < end; i++)
            {
                var use = node.Uses[i]; float y = heading + i * 42;
                string label = use.Node + (use.Inactive ? " · 禁用" : "") + (use.Nested ? " · 嵌套" : "");
                if (GUI.Button(new Rect(2, y, width, 21), new GUIContent(label, label), EditorStyles.miniButton) && use.Emitter) selectEmitter?.Invoke(use.Emitter);
                string property = use.Component + use.Property;
                GUI.Label(new Rect(3, y + 21, width, 21), new GUIContent(property, property), EditorStyles.miniLabel);
            }
            GUI.EndScrollView();
        }

        void BuildPasses()
        {
            var material = Resources.Find(selectedMaterial)?.Asset as Material;
            subLabels = passLabels = Array.Empty<string>();
            if (!material || !material.shader) return;
            var data = ShaderUtil.GetShaderData(material.shader);
            if (subshader < 0 || subshader >= data.SubshaderCount) subshader = data.ActiveSubshaderIndex;
            if (subshader < 0) return;
            subLabels = Enumerable.Range(0, data.SubshaderCount).Select(i => "SubShader " + i + (i == data.ActiveSubshaderIndex ? " (当前)" : "")).ToArray();
            var sub = data.GetSubshader(subshader);
            passLabels = new[] { "材质启用的 Pass" }.Concat(Enumerable.Range(0, sub.PassCount).Select(i => i + ": " + sub.GetPass(i).Name)).ToArray();
            if (pass >= sub.PassCount) pass = -1;
        }
        void DrawShaders()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                int current = Array.FindIndex(materials, n => n.Id == selectedMaterial);
                int next = EditorGUILayout.Popup(Math.Max(0, current), materialLabels, GUILayout.MinWidth(120));
                if (next != current && next >= 0 && next < materials.Length) { selectedMaterial = materials[next].Id; subshader = pass = -1; BuildPasses(); }
                int nextSub = EditorGUILayout.Popup(Math.Max(0, subshader), subLabels, GUILayout.Width(130));
                if (nextSub != subshader && nextSub < subLabels.Length) { subshader = nextSub; pass = -1; BuildPasses(); }
                pass = EditorGUILayout.Popup(pass + 1, passLabels, GUILayout.Width(160)) - 1;
                using (new EditorGUI.DisabledScope(materials.Length == 0))
                {
                    if (GUILayout.Button("分析选中", EditorStyles.toolbarButton, GUILayout.Width(65)))
                    { var node = Resources.Find(selectedMaterial); if (node != null) Shaders.Request(new[] { node }, subshader, pass); }
                    if (GUILayout.Button("分析全部", EditorStyles.toolbarButton, GUILayout.Width(65))) Shaders.Request(materials);
                }
                using (new EditorGUI.DisabledScope(!Shaders.Running))
                    if (GUILayout.Button("取消", EditorStyles.toolbarButton, GUILayout.Width(40))) Shaders.Cancel();
            }
            EditorGUILayout.LabelField($"D3D11 静态比较基准 · VS/PS 分开 · 剩余 {Shaders.Pending} 项 · 单次同步编译可能短暂阻塞，取消在项间生效。", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(Shaders.Message)) EditorGUILayout.HelpBox(Shaders.Message, MessageType.Warning);
            if (shaderRevision != Shaders.Revision)
            {
                results.Clear(); results.AddRange(Shaders.Rows);
                results.Sort((a, b) => {
                    long av = ShaderSortValue(a), bv = ShaderSortValue(b); int comparison = av.CompareTo(bv);
                    return comparison == 0 ? string.CompareOrdinal(a.MaterialName + a.PassName + a.Stage, b.MaterialName + b.PassName + b.Stage) : shaderDescending ? -comparison : comparison;
                }); shaderRevision = Shaders.Revision;
            }
            Rect area = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var table = new Rect(area.x, area.y, area.width * .68f, area.height);
            var detail = new Rect(table.xMax + 5, area.y, Mathf.Max(1, area.width - table.width - 5), area.height);
            const float width = 940;
            shaderScroll = GUI.BeginScrollView(table, shaderScroll, new Rect(0, 0, width, (results.Count + 1) * 22));
            GUI.Label(new Rect(0, 0, 380, 22), "材质 / Pass / 阶段 / 状态", EditorStyles.boldLabel);
            if (GUI.Button(new Rect(380, 0, 100, 22), "ALU", EditorStyles.toolbarButton)) ShaderSort(0);
            if (GUI.Button(new Rect(480, 0, 100, 22), "总指令", EditorStyles.toolbarButton)) ShaderSort(1);
            if (GUI.Button(new Rect(580, 0, 120, 22), "采样 / 加载", EditorStyles.toolbarButton)) ShaderSort(2);
            GUI.Label(new Rect(700, 0, 140, 22), "分支 静态 / 动态"); GUI.Label(new Rect(840, 0, 100, 22), "临时寄存器");
            int start = Mathf.Max(0, (int)(shaderScroll.y / 22) - 1), end = Math.Min(results.Count, start + Mathf.CeilToInt(table.height / 22) + 2);
            for (int i = start; i < end; i++)
            {
                var row = results[i]; float y = (i + 1) * 22;
                string status = row.Stale ? "过期" : row.Status == CascadeShaderStatus.Queued ? "排队" : row.Status == CascadeShaderStatus.Cancelled ? "取消" : row.Status == CascadeShaderStatus.Unavailable ? "N/A" : "已分析";
                if (GUI.Button(new Rect(0, y, 378, 22), row.MaterialName + "/" + row.PassName + "/" + (row.Stage == ShaderType.Vertex ? "VS" : "PS") + " · " + status, EditorStyles.miniButton)) selectedVariant = row;
                if (row.Result == null || !row.Result.Known) continue;
                var d = row.Result.Instructions;
                GUI.Label(new Rect(380, y, 100, 22), d.Alu.ToString()); GUI.Label(new Rect(480, y, 100, 22), d.Total.ToString());
                GUI.Label(new Rect(580, y, 120, 22), d.Samples + " / " + d.Loads); GUI.Label(new Rect(700, y, 140, 22), d.StaticFlow + " / " + d.DynamicFlow);
                GUI.Label(new Rect(840, y, 100, 22), d.Registers.ToString());
            }
            GUI.EndScrollView();
            GUILayout.BeginArea(detail); diagnosticScroll = EditorGUILayout.BeginScrollView(diagnosticScroll);
            if (selectedVariant != null)
            {
                var row = selectedVariant;
                EditorGUILayout.LabelField(row.MaterialName + " / " + (row.Shader ? row.Shader.name : "Shader 已删除"), EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField($"SubShader {row.Subshader} · Pass {row.Pass} · {row.Stage}\nWindows64 · D3D11 · {row.Tier}", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("冻结关键字：" + (string.IsNullOrEmpty(row.KeywordsText) ? "无" : row.KeywordsText), EditorStyles.wordWrappedMiniLabel);
                if (row.Result?.Known == true) EditorGUILayout.LabelField($"ALU = FP {row.Result.Instructions.Float} + INT {row.Result.Instructions.Int} + UINT {row.Result.Instructions.UInt}", EditorStyles.wordWrappedMiniLabel);
                if (row.Stale) EditorGUILayout.HelpBox("资源已变化，请手动重新分析。", MessageType.Info);
                EditorGUILayout.LabelField(row.Result?.Diagnostic ?? "尚未编译", EditorStyles.wordWrappedMiniLabel);
            }
            else EditorGUILayout.LabelField("选择材质后按需分析。结果是编译后静态指令，不代表实际 Draw Call、GPU 周期、耗时或整个特效的 ALU。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView(); GUILayout.EndArea();
        }
        long ShaderSortValue(CascadeShaderVariant row) => row.Result?.Known != true ? -1 : shaderSort == 0 ? row.Result.Instructions.Alu : shaderSort == 1 ? row.Result.Instructions.Total : row.Result.Instructions.Samples;
        void ShaderSort(int column) { shaderDescending = shaderSort == column ? !shaderDescending : true; shaderSort = column; shaderRevision = -1; }
        public void Dispose() { Shaders.Dispose(); Resources.Rebuild(null, null); Root = null; }
    }
}
