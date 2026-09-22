using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    internal enum CascadeShaderStatus { Queued, Ready, Unavailable, Cancelled }
    internal sealed class CascadeShaderResult
    {
        internal readonly bool Known;
        internal readonly CascadeShaderInstructions Instructions;
        internal readonly string Diagnostic;
        internal CascadeShaderResult(CascadeShaderInstructions instructions, string diagnostic = "") { Known = true; Instructions = instructions; Diagnostic = diagnostic; }
        internal CascadeShaderResult(string reason) { Diagnostic = reason; }
    }
    internal sealed class CascadeShaderVariant
    {
        internal readonly Shader Shader;
        internal readonly string MaterialId, MaterialName, MaterialFingerprint, ShaderHash, CacheKey, KeywordsText, PassName;
        internal readonly int Subshader, Pass;
        internal readonly ShaderType Stage;
        internal readonly GraphicsTier Tier;
        internal readonly string[] Keywords;
        internal CascadeShaderStatus Status { get; private set; }
        internal CascadeShaderResult Result { get; private set; }
        internal bool Stale { get; private set; }
        internal CascadeShaderVariant(CascadeResourceNode material, int subshader, int pass, ShaderType stage, string[] keywords, string shaderHash)
        {
            var m = (Material)material.Asset; Shader = m.shader; MaterialId = material.Id; MaterialName = material.Name;
            MaterialFingerprint = material.Fingerprint; Subshader = subshader; Pass = pass; Stage = stage; Keywords = keywords;
            KeywordsText = string.Join(" ", keywords); ShaderHash = shaderHash; Tier = Graphics.activeTier;
            PassName = ShaderUtil.GetShaderData(Shader).GetSubshader(subshader).GetPass(pass).Name;
            CacheKey = CascadeResourceIndex.Identity(Shader) + "|" + shaderHash + "|D3D11|Windows64|" + subshader + "|" + pass + "|" + stage + "|" + Tier + "|" + KeywordsText;
            Status = CascadeShaderStatus.Queued;
        }
        internal void Complete(CascadeShaderResult result) { Result = result; Status = result.Known ? CascadeShaderStatus.Ready : CascadeShaderStatus.Unavailable; }
        internal void Cancel() { if (Status == CascadeShaderStatus.Queued) Status = CascadeShaderStatus.Cancelled; }
        internal void Invalidate() { Stale = true; Cancel(); }
    }

    internal sealed class CascadeShaderAnalysis : IDisposable
    {
        readonly List<CascadeShaderVariant> rows = new List<CascadeShaderVariant>();
        readonly Queue<CascadeShaderVariant> queue = new Queue<CascadeShaderVariant>();
        readonly Dictionary<string, CascadeShaderResult> cache = new Dictionary<string, CascadeShaderResult>();
        readonly Func<CascadeShaderVariant, CascadeShaderResult> compiler;
        internal IReadOnlyList<CascadeShaderVariant> Rows { get; }
        internal int Revision { get; private set; }
        internal int CompileCalls { get; private set; }
        internal bool Running => queue.Count > 0;
        internal int Pending => queue.Count;
        internal string Message { get; private set; }
        internal int KnownCount => rows.Count(r => !r.Stale && r.Status == CascadeShaderStatus.Ready);
        internal CascadeShaderVariant Maximum(ShaderType stage) => rows.Where(r => r.Stage == stage && !r.Stale && r.Status == CascadeShaderStatus.Ready).OrderByDescending(r => r.Result.Instructions.Alu).FirstOrDefault();
        internal CascadeShaderAnalysis(Func<CascadeShaderVariant, CascadeShaderResult> compiler = null)
        { this.compiler = compiler ?? Compile; Rows = rows.AsReadOnly(); }
        internal static string Dependency(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            return string.IsNullOrEmpty(path) ? "builtin:" + Application.unityVersion : AssetDatabase.GetAssetDependencyHash(path).ToString();
        }
        internal static string[] CaptureKeywords(Material material, int subshader, int pass, ShaderType stage)
        {
            // Runtime fallback passes need not have a serialized PassIdentifier. The shader's
            // public keyword space is safe for both; unused keywords are ignored by compilation.
            var allowed = material.shader.keywordSpace.keywords;
            var globals = new HashSet<string>(Shader.enabledGlobalKeywords.Select(k => k.name));
            return allowed.Where(k => material.IsKeywordEnabled(k) || (k.isOverridable && globals.Contains(k.name)))
                .Select(k => k.name).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
        internal void Request(IEnumerable<CascadeResourceNode> materials, int subshader = -1, int pass = -1)
        {
            Cancel(); Message = null;
            foreach (var node in materials)
            {
                if (!(node.Asset is Material material) || !material.shader) continue;
                try
                {
                    var data = ShaderUtil.GetShaderData(material.shader);
                    int sub = subshader < 0 ? data.ActiveSubshaderIndex : subshader;
                    if (sub < 0 || sub >= data.SubshaderCount) { Message = material.name + "：没有可用 SubShader"; continue; }
                    var shaderSub = data.GetSubshader(sub); string hash = Dependency(material.shader);
                    rows.RemoveAll(r => r.MaterialId == node.Id && (pass < 0 || r.Pass == pass) && r.Subshader == sub);
                    for (int p = 0; p < shaderSub.PassCount; p++)
                    {
                        if (pass >= 0 && pass != p) continue;
                        var shaderPass = shaderSub.GetPass(p);
                        if (pass < 0 && !string.IsNullOrEmpty(shaderPass.Name) && !material.GetShaderPassEnabled(shaderPass.Name)) continue;
                        foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                        {
                            var variant = new CascadeShaderVariant(node, sub, p, stage, CaptureKeywords(material, sub, p, stage), hash);
                            rows.Add(variant);
                            if (!shaderPass.HasShaderStage(stage)) variant.Complete(new CascadeShaderResult("Pass 没有此 Shader 阶段"));
                            else if (cache.TryGetValue(variant.CacheKey, out var cached)) variant.Complete(cached);
                            else queue.Enqueue(variant);
                        }
                    }
                }
                catch (Exception ex) { Message = node.Name + "：" + ex.Message; }
            }
            Revision++;
        }
        // Called only by the owning window update. A native compile is synchronous; cancellation is between variants.
        internal bool Tick()
        {
            if (!Running || EditorApplication.isCompiling) return false;
            var variant = queue.Dequeue();
            if (variant.Stale || variant.Status != CascadeShaderStatus.Queued) return true;
            if (!cache.TryGetValue(variant.CacheKey, out var result))
            {
                try { CompileCalls++; result = compiler(variant); }
                catch (Exception ex) { result = new CascadeShaderResult(ex.Message); }
                if (cache.Count >= 512) cache.Clear();
                cache[variant.CacheKey] = result;
            }
            variant.Complete(result); Revision++; return true;
        }
        internal void Refresh(CascadeResourceIndex index)
        {
            var hashes = new Dictionary<Shader, string>();
            foreach (var row in rows)
            {
                if (row.Stale) continue;
                var material = index.Find(row.MaterialId);
                if (material == null || !row.Shader || material.Fingerprint != row.MaterialFingerprint) { row.Invalidate(); continue; }
                if (!hashes.TryGetValue(row.Shader, out string hash)) hashes[row.Shader] = hash = Dependency(row.Shader);
                if (hash != row.ShaderHash) row.Invalidate();
            }
            Revision++;
        }
        internal void Cancel() { foreach (var variant in queue) variant.Cancel(); queue.Clear(); Revision++; }
        internal void Clear() { Cancel(); rows.Clear(); cache.Clear(); Message = null; Revision++; }
        public void Dispose() => Clear();
        static CascadeShaderResult Compile(CascadeShaderVariant variant)
        {
            var pass = ShaderUtil.GetShaderData(variant.Shader).GetSubshader(variant.Subshader).GetPass(variant.Pass);
            var compiled = pass.CompileVariant(variant.Stage, variant.Keywords, ShaderCompilerPlatform.D3D,
                BuildTarget.StandaloneWindows64, variant.Tier, true);
            string messages = compiled.Messages == null ? "" : string.Join("\n", compiled.Messages.Select(m => m.severity + ": " + m.message + " " + m.file + ":" + m.line));
            if (!compiled.Success) return new CascadeShaderResult("编译失败\n" + messages);
            if (!CascadeD3D11Reflection.TryRead(compiled.ShaderData, out var instructions, out var reason)) return new CascadeShaderResult(reason + "\n" + messages);
            return new CascadeShaderResult(instructions, reason + "\n" + messages);
        }
    }
}
