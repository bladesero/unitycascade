using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SGame.Rendering.OffscreenParticles
{
    public enum ParticleResolution { Full = 1, Half = 2, Quarter = 4 }
    public enum ParticleDebugView { None, Color, Alpha, Depth, Edges }

    [DisallowMultipleRendererFeature("Offscreen Particles")]
    public sealed class OffscreenParticleRendererFeature : ScriptableRendererFeature
    {
        public LayerMask layerMask;
        public ParticleResolution resolution = ParticleResolution.Half;
        [Min(0)] public float absoluteDepthThreshold = 0.1f;
        [Min(0)] public float relativeDepthThreshold = 0.01f;
        public bool showInSceneView = true;
        [Tooltip("Low resolution modes only. Full always renders directly without intermediate buffers.")]
        public ParticleDebugView debugView;
        const string ProcessingShaderResource = "OffscreenParticleProcessing";
        Shader processingShader;

        Material material;
        RTHandle lowDepth, particleColor, cameraColor;
        DepthPass depthPass;
        DrawPass drawPass;
        CompositePass compositePass;
        bool lowResolution, warnedFormat, warnedShader, timingEnabled;
        int width, height;
        static readonly int Size = Shader.PropertyToID("_OSPSize");
        static readonly int Depth = Shader.PropertyToID("_OSPDepth");
        static readonly int Color = Shader.PropertyToID("_OSPColor");
        static readonly int Threshold = Shader.PropertyToID("_OSPThreshold");
        static readonly int DebugMode = Shader.PropertyToID("_OSPDebug");
        static readonly int RasterSize = Shader.PropertyToID("_OSPRasterSize");

        public Vector3 GpuPassMilliseconds => new Vector3(lowResolution ? depthPass?.GpuMilliseconds ?? 0 : 0,
            drawPass?.GpuMilliseconds ?? 0, lowResolution ? compositePass?.GpuMilliseconds ?? 0 : 0);
        public void EnableTiming(bool enabled)
        {
            timingEnabled = enabled;
            depthPass?.EnableTiming(enabled); drawPass?.EnableTiming(enabled); compositePass?.EnableTiming(enabled);
        }

        public override void Create()
        {
            Release();
            depthPass = new DepthPass(this);
            drawPass = new DrawPass(this);
            compositePass = new CompositePass(this);
            EnableTiming(timingEnabled);
            EnsureMaterial();
        }

        bool EnsureMaterial()
        {
            if (material != null) return true;
            if (processingShader == null) processingShader = Resources.Load<Shader>(ProcessingShaderResource);
            if (processingShader == null || !processingShader.isSupported) return false;
            material = CoreUtils.CreateEngineMaterial(processingShader);
            return true;
        }

        bool SupportsCamera(in CameraData data)
        {
            return !data.xrRendering && data.renderType == CameraRenderType.Base &&
                (data.cameraType == CameraType.Game || (showInSceneView && data.isSceneViewCamera));
        }

        static bool SupportsFormat(GraphicsFormat format, bool blend)
        {
            return SystemInfo.IsFormatSupported(format, FormatUsage.Render) &&
                SystemInfo.IsFormatSupported(format, FormatUsage.Sample) &&
                (!blend || SystemInfo.IsFormatSupported(format, FormatUsage.Blend));
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!SupportsCamera(renderingData.cameraData)) return;
            lowResolution = resolution != ParticleResolution.Full;
            if (lowResolution && (!SupportsFormat(GraphicsFormat.R32_SFloat, false) ||
                !SupportsFormat(GraphicsFormat.R16G16B16A16_SFloat, true)))
            {
                lowResolution = false;
                if (!warnedFormat) Debug.LogWarning("Offscreen Particles: required RT formats unavailable; using Full.");
                warnedFormat = true;
            }
            if (lowResolution && !EnsureMaterial())
            {
                lowResolution = false;
                if (!warnedShader) Debug.LogWarning("Offscreen Particles: Resources/OffscreenParticleProcessing shader missing/unsupported; using Full.");
                warnedShader = true;
            }
            if (lowResolution) renderer.EnqueuePass(depthPass);
            else ReleaseBuffers();
            renderer.EnqueuePass(drawPass);
            if (lowResolution) renderer.EnqueuePass(compositePass);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (SupportsCamera(renderingData.cameraData)) cameraColor = renderer.cameraColorTargetHandle;
        }

        void Allocate(in CameraData data)
        {
            var desc = data.cameraTargetDescriptor;
            int divisor = resolution == ParticleResolution.Quarter ? 4 : 2;
            float sx = desc.useDynamicScale ? ScalableBufferManager.widthScaleFactor : 1;
            float sy = desc.useDynamicScale ? ScalableBufferManager.heightScaleFactor : 1;
            width = Mathf.Max(1, Mathf.RoundToInt(desc.width * sx / divisor));
            height = Mathf.Max(1, Mathf.RoundToInt(desc.height * sy / divisor));
            desc.width = width;
            desc.height = height;
            desc.depthBufferBits = 0;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.msaaSamples = 1;
            desc.bindMS = false;
            desc.useDynamicScale = false;
            desc.useMipMap = desc.autoGenerateMips = desc.enableRandomWrite = false;
            desc.memoryless = RenderTextureMemoryless.None;
            desc.graphicsFormat = GraphicsFormat.R16_SFloat;
            RenderingUtils.ReAllocateIfNeeded(ref lowDepth, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_OSPDepth");
            // desc.memoryless = RenderTextureMemoryless.Color;
            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            RenderingUtils.ReAllocateIfNeeded(ref particleColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_OSPColor");
        }

        Rect CameraViewport(in CameraData data)
        {
            if (cameraColor.rt == null) return data.camera.pixelRect;
            Vector2Int size = cameraColor.useScaling
                ? cameraColor.GetScaledSize(cameraColor.rtHandleProperties.currentViewportSize)
                : new Vector2Int(data.cameraTargetDescriptor.width, data.cameraTargetDescriptor.height);
            float sx = data.cameraTargetDescriptor.useDynamicScale ? ScalableBufferManager.widthScaleFactor : 1;
            float sy = data.cameraTargetDescriptor.useDynamicScale ? ScalableBufferManager.heightScaleFactor : 1;
            return new Rect(0, 0, Mathf.Max(1, Mathf.RoundToInt(size.x * sx)), Mathf.Max(1, Mathf.RoundToInt(size.y * sy)));
        }

        void SetParameters(CommandBuffer cmd)
        {
            cmd.SetGlobalVector(Size, new Vector4(width, height, 1f / width, 1f / height));
            cmd.SetGlobalVector(Threshold, new Vector4(Mathf.Max(0, absoluteDepthThreshold), Mathf.Max(0, relativeDepthThreshold), 0, 0));
            cmd.SetGlobalFloat(DebugMode, (int)debugView);
        }

        void ReleaseBuffers()
        {
            lowDepth?.Release(); lowDepth = null;
            particleColor?.Release(); particleColor = null;
        }

        void Release()
        {
            ReleaseBuffers();
            CoreUtils.Destroy(material); material = null;
            cameraColor = null;
        }
        protected override void Dispose(bool disposing) => Release();

        abstract class ParticlePass : ScriptableRenderPass
        {
            protected readonly OffscreenParticleRendererFeature owner;
            protected readonly ProfilingSampler sampler;
            public float GpuMilliseconds => sampler.gpuElapsedTime;
            public void EnableTiming(bool enabled) => sampler.enableRecording = enabled;
            protected ParticlePass(OffscreenParticleRendererFeature owner, string label, int offset)
            {
                this.owner = owner;
                sampler = new ProfilingSampler(label);
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents + offset;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }
        }

        sealed class DepthPass : ParticlePass
        {
            public DepthPass(OffscreenParticleRendererFeature owner) : base(owner, "OSP Depth Downsample", 1) { }
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                owner.Allocate(data.cameraData);
                ConfigureTarget(owner.lowDepth);
                ConfigureClear(ClearFlag.None, UnityEngine.Color.clear);
            }
            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, sampler))
                {
                    owner.SetParameters(cmd);
                    cmd.SetViewport(new Rect(0, 0, owner.width, owner.height));
                    cmd.DrawProcedural(Matrix4x4.identity, owner.material, 0, MeshTopology.Triangles, 3);
                    cmd.SetGlobalTexture(Depth, owner.lowDepth);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        sealed class DrawPass : ParticlePass
        {
            static readonly ShaderTagId Tag = new ShaderTagId("OffscreenParticle");
            public DrawPass(OffscreenParticleRendererFeature owner) : base(owner, "OSP Particle Draw", 2) { }
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                ConfigureTarget(owner.lowResolution ? owner.particleColor : owner.cameraColor);
                ConfigureClear(owner.lowResolution ? ClearFlag.Color : ClearFlag.None, UnityEngine.Color.clear);
            }
            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, sampler))
                {
                    Rect viewport = owner.lowResolution ? new Rect(0, 0, owner.width, owner.height) : owner.CameraViewport(data.cameraData);
                    cmd.SetViewport(viewport);
                    // Raster pixel coordinates avoid ComputeScreenPos/projection-flip ambiguity.
                    cmd.SetGlobalVector(RasterSize, new Vector4(viewport.x, viewport.y, 1f / viewport.width, 1f / viewport.height));
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();
                    var drawing = CreateDrawingSettings(Tag, ref data, SortingCriteria.CommonTransparent);
                    var filtering = new FilteringSettings(RenderQueueRange.transparent, owner.layerMask);
                    context.DrawRenderers(data.cullResults, ref drawing, ref filtering);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        sealed class CompositePass : ParticlePass
        {
            public CompositePass(OffscreenParticleRendererFeature owner) : base(owner, "OSP Upsample Composite", 3) { }
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                ConfigureTarget(owner.cameraColor);
                ConfigureClear(ClearFlag.None, UnityEngine.Color.clear);
            }
            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, sampler))
                {
                    owner.SetParameters(cmd);
                    cmd.SetGlobalTexture(Color, owner.particleColor);
                    cmd.SetGlobalTexture(Depth, owner.lowDepth);
                    cmd.SetViewport(owner.CameraViewport(data.cameraData));
                    cmd.DrawProcedural(Matrix4x4.identity, owner.material, owner.debugView == ParticleDebugView.None ? 1 : 2, MeshTopology.Triangles, 3);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                cmd.SetGlobalTexture(Color, Texture2D.blackTexture);
                cmd.SetGlobalTexture(Depth, Texture2D.blackTexture);
            }
        }
    }
}
