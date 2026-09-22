using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        Material AnalysisMaterial()
        {
            var shader = Shader.Find("Hidden/ShurikenCascade/AnalysisFixture"); Assert.NotNull(shader);
            var material = new Material(shader); AssetDatabase.CreateAsset(material, folder + "/Analysis.mat");
            session.Emitters[0].GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            return material;
        }

        [Test]
        public void ResourceIndexDeduplicatesAndKeepsInactiveNestedNonParticleAndTrailUses()
        {
            var material = AnalysisMaterial(); var texture = new Texture2D(8, 8, TextureFormat.RGBA32, true);
            AssetDatabase.CreateAsset(texture, folder + "/Texture.asset"); material.SetTexture("_MainTex", texture);
            var first = session.Emitters[0].GetComponent<ParticleSystemRenderer>(); first.trailMaterial = material;
            session.Emitters[1].GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            session.Emitters[1].gameObject.SetActive(false);
            var meshNode = new GameObject("Other Renderer", typeof(MeshRenderer)); meshNode.transform.SetParent(session.Root.transform, false);
            meshNode.GetComponent<MeshRenderer>().sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(meshNode, folder + "/Nested.prefab");
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Nested.prefab"), session.Root.transform);
            var index = new CascadeResourceIndex(); index.Rebuild(session.Root, null);
            var m = index.Find(CascadeResourceIndex.Identity(material)); var t = index.Find(CascadeResourceIndex.Identity(texture));
            Assert.NotNull(m); Assert.NotNull(t); Assert.GreaterOrEqual(m.Uses.Count, 5);
            Assert.IsTrue(m.Uses.Any(u => u.Property.Contains("Trail Material")));
            Assert.IsTrue(m.Uses.Any(u => u.Nested)); Assert.IsTrue(m.Uses.Any(u => u.Inactive));
            Assert.IsTrue(m.Uses.Any(u => u.Emitter == null && u.Node.Contains("Other Renderer")));
            Assert.AreEqual(1, index.Nodes.Count(n => n.Asset == texture)); Assert.AreEqual(m.Uses.Count, t.Uses.Count);
            Assert.AreEqual(t.Memory.Bytes, index.MaterialTextures(m).Bytes);
            Assert.AreEqual(t.Memory.Bytes, index.Effect.Textures);
            Assert.IsTrue(t.Uses.All(u => u.Property.Contains("_MainTex")));
            Assert.IsFalse(session.Dirty); Assert.NotNull(nested);
        }

        [Test]
        public void ResourceIndexFollowsUnsavedReplacementUndoAndSeparatesEnvironment()
        {
            var first = AnalysisMaterial(); var second = new Material(first); AssetDatabase.CreateAsset(second, folder + "/Second.mat");
            var texture = new Texture2D(4, 4); AssetDatabase.CreateAsset(texture, folder + "/Shared.asset");
            first.SetTexture("_MainTex", texture); second.SetTexture("_MainTex", texture);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>(); var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(); bloom.dirtTexture.value = texture;
            try
            {
                var index = new CascadeResourceIndex(); var renderer = session.Emitters[0].GetComponent<ParticleSystemRenderer>();
                index.Rebuild(session.Root, profile);
                var node = index.Find(CascadeResourceIndex.Identity(texture));
                Assert.IsTrue(node.Effect && node.Environment);
                Assert.AreEqual(node.Memory.Bytes, index.Environment.Textures);
                var before = File.ReadAllBytes(path);
                Undo.RecordObject(renderer, "Replace analysis material"); renderer.sharedMaterial = second; Undo.FlushUndoRecordObjects();
                index.Rebuild(session.Root, profile); Assert.NotNull(index.Find(CascadeResourceIndex.Identity(second)));
                Assert.IsNull(index.Find(CascadeResourceIndex.Identity(first)));
                Undo.PerformUndo(); index.Rebuild(session.Root, profile); Assert.NotNull(index.Find(CascadeResourceIndex.Identity(first)));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            }
            finally { Object.DestroyImmediate(bloom); Object.DestroyImmediate(profile); }
        }

        [Test]
        public void ResourceIndexIncludesSpriteTextureAndSubassetIdentity()
        {
            var texture = new Texture2D(8, 8); AssetDatabase.CreateAsset(texture, folder + "/Sprites.asset");
            var a = Sprite.Create(texture, new Rect(0, 0, 4, 4), Vector2.zero);
            var b = Sprite.Create(texture, new Rect(4, 0, 4, 4), Vector2.zero);
            AssetDatabase.AddObjectToAsset(a, texture); AssetDatabase.AddObjectToAsset(b, texture);
            Assert.AreNotEqual(CascadeResourceIndex.Identity(a), CascadeResourceIndex.Identity(b));
            var go = new GameObject("Sprite", typeof(SpriteRenderer)); go.transform.SetParent(session.Root.transform, false); go.GetComponent<SpriteRenderer>().sprite = a;
            var index = new CascadeResourceIndex(); index.Rebuild(session.Root, null);
            Assert.NotNull(index.Find(CascadeResourceIndex.Identity(a))); Assert.NotNull(index.Find(CascadeResourceIndex.Identity(texture)));
            Assert.AreNotEqual(CascadeResourceIndex.Identity(session.Emitters[0]), CascadeResourceIndex.Identity(session.Emitters[1]));
        }

        [TestCase(8, 8, 1, 1, 4, GraphicsFormat.R8G8B8A8_UNorm, 340)]
        [TestCase(5, 7, 1, 1, 1, GraphicsFormat.RGBA_DXT1_UNorm, 32)]
        [TestCase(4, 4, 1, 6, 1, GraphicsFormat.R8G8B8A8_UNorm, 384)]
        [TestCase(4, 4, 4, 1, 3, GraphicsFormat.R8G8B8A8_UNorm, 292)]
        [TestCase(4, 4, 1, 3, 1, GraphicsFormat.R8G8B8A8_UNorm, 192)]
        public void GpuMemoryUsesFormatsBlocksMipsDepthAndLayers(int width, int height, int depth, int layers, int mips, GraphicsFormat format, long bytes)
        { Assert.AreEqual(bytes, CascadeGpuMemory.Mips(width, height, depth, layers, mips, format)); }

        [TestCase(IndexFormat.UInt16, 42)]
        [TestCase(IndexFormat.UInt32, 48)]
        public void GpuMemoryEstimatesNonReadableMeshWithoutAllocatingBuffers(IndexFormat format, long expected)
        {
            var mesh = new Mesh { indexFormat = format }; mesh.vertices = new[] { Vector3.zero, Vector3.one, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            try
            {
                mesh.UploadMeshData(true); var memory = CascadeGpuMemory.Estimate(mesh);
                Assert.IsTrue(memory.Known, memory.Detail); Assert.AreEqual(expected, memory.Bytes);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void GpuMemorySeparatesRenderTargetColorDepthResolveAndMsaa()
        {
            var descriptor = new RenderTextureDescriptor(8, 8) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm, depthStencilFormat = GraphicsFormat.D32_SFloat, msaaSamples = 4, bindMS = false, dimension = TextureDimension.Tex2D, volumeDepth = 1 };
            var estimate = CascadeGpuMemory.RenderTarget(descriptor); Assert.IsTrue(estimate.Known); Assert.AreEqual(2304, estimate.Bytes);
            descriptor.bindMS = true; Assert.AreEqual(2048, CascadeGpuMemory.RenderTarget(descriptor).Bytes);
            descriptor.msaaSamples = 1; Assert.AreEqual(512, CascadeGpuMemory.RenderTarget(descriptor).Bytes);
        }

        [Test]
        public void ShaderAnalysisCompilesActualDxbcVariantsAndReflectsInstructions()
        {
            var material = AnalysisMaterial(); material.SetShaderPassEnabled("Second", false);
            var index = new CascadeResourceIndex(); index.Rebuild(session.Root, null);
            using (var service = new CascadeShaderAnalysis())
            {
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) });
                Assert.AreEqual(0, service.CompileCalls); Assert.AreEqual(2, service.Rows.Count);
                while (service.Running) service.Tick();
                foreach (var row in service.Rows)
                {
                    Assert.AreEqual(CascadeShaderStatus.Ready, row.Status, row.Result?.Diagnostic);
                    TestContext.WriteLine($"{row.Stage}: ALU {row.Result.Instructions.Alu} total {row.Result.Instructions.Total} texture {row.Result.Instructions.Samples} loads {row.Result.Instructions.Loads} registers {row.Result.Instructions.Registers}");
                }
                Assert.IsTrue(service.Rows.Any(r => r.Stage == ShaderType.Fragment && r.Result.Instructions.Samples > 0));
                Assert.Greater(service.Maximum(ShaderType.Fragment).Result.Instructions.Alu, 0);
                int compiled = service.CompileCalls;
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) }); Assert.IsFalse(service.Running); Assert.AreEqual(compiled, service.CompileCalls);
                material.EnableKeyword("CASCADE_ANALYSIS_COMPLEX"); index.Rebuild(session.Root, null); service.Refresh(index);
                Assert.IsTrue(service.Rows.All(r => r.Stale));
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) });
                while (service.Running) service.Tick();
                Assert.IsTrue(service.Rows.Any(r => r.KeywordsText.Contains("CASCADE_ANALYSIS_COMPLEX")));
                Assert.IsTrue(service.Rows.All(r => !r.Stale && r.Status == CascadeShaderStatus.Ready));
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) }, 0, 1);
                while (service.Running) service.Tick();
                Assert.IsTrue(service.Rows.Any(r => r.PassName == "Second"));
            }
        }

        [Test]
        public void ShaderQueueCancellationErrorsAndResourceChangesCannotReuseStaleRows()
        {
            var material = AnalysisMaterial(); var index = new CascadeResourceIndex(); index.Rebuild(session.Root, null);
            using (var service = new CascadeShaderAnalysis(v => new CascadeShaderResult("Synthetic compiler failure")))
            {
                var node = index.Find(CascadeResourceIndex.Identity(material)); service.Request(new[] { node });
                service.Tick(); Assert.AreEqual(CascadeShaderStatus.Unavailable, service.Rows[0].Status);
                service.Cancel(); Assert.IsFalse(service.Running); Assert.IsTrue(service.Rows.Any(r => r.Status == CascadeShaderStatus.Cancelled));
                service.Request(new[] { node }); material.SetColor("_Tint", Color.red); index.Rebuild(session.Root, null); service.Refresh(index);
                int calls = service.CompileCalls; while (service.Running) service.Tick(); Assert.AreEqual(calls, service.CompileCalls);
                service.Clear(); Assert.AreEqual(0, service.Rows.Count); Assert.IsFalse(service.Running);
            }
        }

        [Test]
        public void ShaderDependencyChangeInvalidatesResultsAndCompiledCacheKeys()
        {
            var material = AnalysisMaterial();
            string shaderPath = folder + "/Dependency.shader";
            string fixture = File.ReadAllText(AssetDatabase.GetAssetPath(material.shader));
            File.WriteAllText(shaderPath, fixture.Replace("Hidden/ShurikenCascade/AnalysisFixture", "Hidden/ShurikenCascade/Dependency/" + Path.GetFileName(folder)));
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            material.shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            var index = new CascadeResourceIndex(); index.Rebuild(session.Root, null);
            using (var service = new CascadeShaderAnalysis(v => new CascadeShaderResult(new CascadeShaderInstructions(1, 0, 0, 2, 0, 0, 0, 0, 1))))
            {
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) }); while (service.Running) service.Tick();
                Assert.IsNotEmpty(service.Rows); string key = service.Rows[0].CacheKey; int calls = service.CompileCalls;
                File.AppendAllText(shaderPath, "\n// changed shader dependency\n"); AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
                index.Rebuild(session.Root, null); service.Refresh(index); Assert.IsTrue(service.Rows.All(r => r.Stale));
                service.Request(new[] { index.Find(CascadeResourceIndex.Identity(material)) }); while (service.Running) service.Tick();
                Assert.AreNotEqual(key, service.Rows[0].CacheKey); Assert.Greater(service.CompileCalls, calls);
            }
        }

        [Test]
        public void DxbcValidationRejectsTruncatedAndDxilWithoutNativeCalls()
        {
            Assert.IsFalse(CascadeD3D11Reflection.TryRead(new byte[5], out _, out string reason)); Assert.IsNotEmpty(reason);
            var data = new byte[44]; Array.Copy(BitConverter.GetBytes(0x43425844u), data, 4); Array.Copy(BitConverter.GetBytes(44u), 0, data, 24, 4);
            Array.Copy(BitConverter.GetBytes(1u), 0, data, 28, 4); Array.Copy(BitConverter.GetBytes(36u), 0, data, 32, 4); Array.Copy(BitConverter.GetBytes(0x4C495844u), 0, data, 36, 4);
            Assert.IsFalse(CascadeD3D11Reflection.TryRead(data, out _, out reason)); StringAssert.Contains("DXIL", reason);
        }

        [Test]
        public void DxbcFallbackCountsInstructionsWithoutSourceOrExecutionEstimates()
        {
            string code = "ps_5_0\ndcl_temps 3\nmul r0, r1, r2\niadd r0, r1, r2\numul r0, r1, r2\nsample_indexable(texture2d)(float,float,float,float) r0, v0, t0, s0\nld r0, r1, t0\nif_nz r0.x\nmov o0, r0\nendif\nret\n";
            Assert.IsTrue(CascadeDxbcInstructions.TryCount(code, out var result, out var reason), reason);
            Assert.AreEqual(3, result.Alu); Assert.AreEqual(9, result.Total); Assert.AreEqual(1, result.Samples); Assert.AreEqual(1, result.Loads);
            Assert.AreEqual(2, result.DynamicFlow); Assert.AreEqual(1, result.StaticFlow); Assert.AreEqual(3, result.Registers);
            Assert.IsFalse(CascadeDxbcInstructions.TryCount("ps_5_0\nunknown_instruction r0\n", out _, out _));
        }

        [Test]
        public void GpuMemoryReadsActualTextureKindsAndCubeArraySliceDescriptors()
        {
            Texture[] textures = { new Texture2DArray(4, 4, 3, TextureFormat.RGBA32, false), new Cubemap(4, TextureFormat.RGBA32, false), new CubemapArray(4, 2, TextureFormat.RGBA32, false), new Texture3D(4, 4, 4, TextureFormat.RGBA32, false) };
            try
            {
                long[] expected = { 192, 384, 768, 256 };
                for (int i = 0; i < textures.Length; i++) { var result = CascadeGpuMemory.Estimate(textures[i]); Assert.IsTrue(result.Known, result.Detail); Assert.AreEqual(expected[i], result.Bytes); }
                var d = new RenderTextureDescriptor(4, 4) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm, depthStencilFormat = GraphicsFormat.None, dimension = TextureDimension.CubeArray, volumeDepth = 12, msaaSamples = 1 };
                Assert.AreEqual(768, CascadeGpuMemory.RenderTarget(d).Bytes);
            }
            finally { foreach (var texture in textures) Object.DestroyImmediate(texture); }
        }

        [UnityTest]
        public IEnumerator AnalysisExistingProjectPrefabUsesUrpMaterialsAndDrawsAllTabs()
        {
            const string example = "Assets/Content/Particle System.prefab";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(example);
            if (!asset) Assert.Ignore("项目特效示例未包含在当前工程中。");
            var disk = File.ReadAllBytes(example); var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            int savedTab = EditorPrefs.GetInt("ShurikenCascade.AnalysisTab", 0);
            var oldPipeline = GraphicsSettings.defaultRenderPipeline; var oldQuality = QualitySettings.renderPipeline;
            var renderer = ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRendererData>();
            ResourceReloader.ReloadAllNullIn(renderer, UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.packagePath);
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.Create(renderer);
            try
            {
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                yield return null;
                window.Show(); window.position = new Rect(20, 20, 1400, 900); window.Open(asset); window.Preview.Playing = false;
                yield return null; window.Analysis.Update(window.Analysis.Root, window.Preview.Environment);
                Assert.GreaterOrEqual(window.Analysis.Root.GetComponentsInChildren<ParticleSystem>(true).Length, 2);
                var nodes = window.Analysis.Resources.Nodes.Where(n => n.Asset is Material).ToArray(); Assert.IsNotEmpty(nodes);
                window.Analysis.Shaders.Request(nodes, -1, 0);
                while (window.Analysis.Shaders.Running) window.Analysis.Shaders.Tick();
                foreach (var row in window.Analysis.Shaders.Rows)
                { Assert.AreEqual(CascadeShaderStatus.Ready, row.Status, row.Result?.Diagnostic); TestContext.WriteLine($"{row.MaterialName}/{row.PassName}/{row.Stage}: ALU={row.Result.Instructions.Alu}, total={row.Result.Instructions.Total}, sample={row.Result.Instructions.Samples}"); }
                using (var serialized = new SerializedObject(window)) { serialized.FindProperty("performanceExpanded").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
                for (int tab = 0; tab < 3; tab++) { window.Analysis.Tab = tab; DrawTestFrame(window); }
                Assert.IsFalse(window.hasUnsavedChanges); CollectionAssert.AreEqual(disk, File.ReadAllBytes(example));
            }
            finally
            {
                window.Close(); EditorPrefs.SetInt("ShurikenCascade.AnalysisTab", savedTab);
                GraphicsSettings.defaultRenderPipeline = oldPipeline; QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(pipeline); Object.DestroyImmediate(renderer);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnalysisTabsDrawAtMinimumSizeWithoutAutomaticCompilationOrAssetChanges()
        {
            AnalysisMaterial(); session.Save(); var disk = File.ReadAllBytes(path);
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>(); int savedTab = EditorPrefs.GetInt("ShurikenCascade.AnalysisTab", 0);
            try
            {
                window.Show(); window.position = new Rect(20, 20, 850, 550); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path)); window.Preview.Playing = false;
                yield return null;
                window.Analysis.Update(window.Analysis.Root, window.Preview.Environment);
                using (var serialized = new SerializedObject(window)) { serialized.FindProperty("performanceExpanded").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
                for (int tab = 0; tab < 3; tab++) { window.Analysis.Tab = tab; DrawTestFrame(window); }
                int scans = window.Analysis.Resources.ScanCount;
                for (int i = 0; i < 5; i++) { DrawTestFrame(window); window.Analysis.Update(window.Analysis.Root, window.Preview.Environment); }
                Assert.AreEqual(scans, window.Analysis.Resources.ScanCount); Assert.AreEqual(0, window.Analysis.Shaders.CompileCalls);
                Assert.IsFalse(window.hasUnsavedChanges); CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
                window.Analysis.Shaders.Request(window.Analysis.Resources.Nodes.Where(n => n.Asset is Material));
                var analysis = window.Analysis; window.Close(); Assert.IsFalse(analysis.Shaders.Running); Assert.AreEqual(0, analysis.Shaders.Rows.Count);
            }
            finally { if (window) window.Close(); EditorPrefs.SetInt("ShurikenCascade.AnalysisTab", savedTab); }
        }
    }
}
