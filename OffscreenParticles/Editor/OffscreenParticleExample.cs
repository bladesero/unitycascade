using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace SGame.Rendering.OffscreenParticles.Editor
{
    public static class OffscreenParticleExample
    {
        const string Root = "Assets/Bladesero/OffScreenParticleRenderingURP";
        const string ScenePath = Root + "/OffscreenParticlesURP.unity";
        const string ShaderRoot = "Assets/Graphics/RendererFeatures/OffscreenParticles/Resources/";
        const string Output = "Artifacts/OffscreenParticles";
        static Scene workingScene;

        [MenuItem("Tools/Offscreen Particles/Install on ZeroGameRenderer")]
        public static void InstallOnZeroGameRenderer()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<ZeroGameRendererData>("Assets/GraphicsSettings/ZeroGameRenderer.asset");
            if (renderer == null) throw new InvalidOperationException("ZeroGameRenderer asset not found.");
            var feature = renderer.rendererFeatures.OfType<OffscreenParticleRendererFeature>().FirstOrDefault();
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<OffscreenParticleRendererFeature>();
                feature.name = "Offscreen Particles";
                feature.layerMask = 1 << Layer("OffscreenParticle");
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
                EditorUtility.SetDirty(feature);
            }
            renderer.intermediateTextureMode = IntermediateTextureMode.Always;
            renderer.SetDirty(); EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssetIfDirty(renderer);
            Debug.Log("OSP installed on ZeroGameRenderer; existing features preserved.");
        }

        [MenuItem("Tools/Offscreen Particles/Validate ZeroGameRenderer")]
        public static void ValidateZeroGameRenderer()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<ZeroGameRendererData>("Assets/GraphicsSettings/ZeroGameRenderer.asset");
            var feature = renderer.rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var oldResolution = feature.resolution; var oldDebug = feature.debugView;
            bool oldActive = feature.isActive;
            var openPreview = typeof(EditorSceneManager).GetMethod("OpenPreviewScene", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            var scene = (Scene)openPreview.Invoke(null, new object[] { ScenePath });
            try
            {
                Directory.CreateDirectory(Output);
                if (Resources.Load<Shader>("OffscreenParticleProcessing") == null)
                    throw new InvalidOperationException("Processing shader failed to load from Resources.");
                if (new SerializedObject(feature).FindProperty("processingShader") != null)
                    throw new InvalidOperationException("Processing shader must not be serialized in the Inspector.");
                var camera = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Camera>()).First();
                camera.scene = scene;
                camera.GetUniversalAdditionalCameraData().SetRenderer(AppendRenderer(pipeline, renderer));
                foreach (var ps in scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<ParticleSystem>())) { ps.Simulate(5, true, true); ps.Pause(); }
                feature.SetActive(true); feature.debugView = ParticleDebugView.None;
                foreach (ParticleResolution scale in Enum.GetValues(typeof(ParticleResolution)))
                {
                    feature.resolution = scale;
                    Capture(camera, "ZeroGame_" + scale, 1280, 720);
                }
                feature.resolution = ParticleResolution.Half; feature.debugView = ParticleDebugView.Color;
                Capture(camera, "ZeroGame_ParticleColor", 1280, 720);
                feature.debugView = ParticleDebugView.None; feature.SetActive(false);
                Capture(camera, "ZeroGame_Disabled", 1280, 720);
                File.WriteAllText(Output + "/zerogame-validation.txt", "Resources.Load succeeded; processingShader is not serialized.\nActual ZeroGameRenderer with existing features: Full/Half/Quarter, particle color, feature disabled captures completed.\n");
                Debug.Log("OSP ZeroGameRenderer validation complete.");
            }
            finally
            {
                feature.resolution = oldResolution; feature.debugView = oldDebug; feature.SetActive(oldActive);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        static GameObject Place(GameObject go)
        {
            SceneManager.MoveGameObjectToScene(go, workingScene);
            return go;
        }

        static GameObject NewObject(string name, params Type[] types) => Place(new GameObject(name, types));

        static int Layer(string name)
        {
            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0) return existing;
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tags.FindProperty("layers");
            for (int i = 8; i < 32; i++)
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                {
                    layers.GetArrayElementAtIndex(i).stringValue = name;
                    tags.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssetIfDirty(tags.targetObject);
                    return i;
                }
            throw new InvalidOperationException("No unused layer for " + name);
        }

        static Material Material(string name, Shader shader, Color color, bool transparent = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(Root + "/" + name + ".mat");
            if (existing != null) return existing;
            if (shader == null) throw new InvalidOperationException("Missing shader for " + name);
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);
            if (mat.HasProperty("_color")) mat.SetColor("_color", color);
            if (transparent)
            {
                mat.SetFloat("_Surface", 1);
                mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = 3000;
            }
            AssetDatabase.CreateAsset(mat, Root + "/" + name + ".mat");
            return mat;
        }

        static UniversalRendererData Renderer(string name, RenderingMode mode, int layer)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/" + name + ".asset");
            if (existing != null)
            {
                existing.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                existing.SetDirty(); EditorUtility.SetDirty(existing); AssetDatabase.SaveAssetIfDirty(existing);
                return existing;
            }
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.name = name;
            renderer.renderingMode = mode;
            renderer.intermediateTextureMode = IntermediateTextureMode.Always;
            renderer.copyDepthMode = CopyDepthMode.AfterOpaques;
            renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            ResourceReloader.ReloadAllNullIn(renderer, "Packages/com.unity.render-pipelines.universal");
            AssetDatabase.CreateAsset(renderer, Root + "/" + name + ".asset");
            var feature = ScriptableObject.CreateInstance<OffscreenParticleRendererFeature>();
            feature.name = "Offscreen Particles";
            feature.layerMask = 1 << layer;
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssetIfDirty(renderer);
            return renderer;
        }

        static int AppendRenderer(UniversalRenderPipelineAsset pipeline, ScriptableRendererData renderer)
        {
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == renderer) return i;
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = renderer;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(pipeline);
            return index;
        }

        static GameObject Shape(string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material material, int layer)
        {
            var go = Place(GameObject.CreatePrimitive(type));
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            go.layer = layer;
            go.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        [MenuItem("Tools/Offscreen Particles/Create Independent Example")]
        public static void CreateExample()
        {
            if (File.Exists(ScenePath)) { Debug.Log("OSP example already exists; no assets overwritten."); return; }
            Directory.CreateDirectory(Root);
            AssetDatabase.Refresh();
            int particleLayer = Layer("OffscreenParticle");
            int geometryLayer = Layer("OffscreenParticleExample");
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) throw new InvalidOperationException("URP must be active.");
            var deferred = Renderer("OffscreenParticlesDeferred", RenderingMode.Deferred, particleLayer);
            var forward = Renderer("OffscreenParticlesForward", RenderingMode.Forward, particleLayer);
            int deferredIndex = AppendRenderer(pipeline, deferred);
            int forwardIndex = AppendRenderer(pipeline, forward);
            var smoke = Material("Smoke", Shader.Find("SGame/OffscreenParticles/AlphaBlend"), new Color(0.35f, 0.65f, 1, 0.38f));
            var glow = Material("Glow", Shader.Find("SGame/OffscreenParticles/AlphaBlend"), new Color(8, 2, 0.3f, 0.65f));
            var cloud = Material("Cloud", Shader.Find("SGame/OffscreenParticles/CloudVolume"), new Color(0.7f, 0.85f, 0.95f, 0.65f));
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Bladesero/OffScreenParticleRendering/Example/CloudDiff1.psd");
            foreach (var mat in new[] { smoke, glow, cloud }) { mat.SetTexture("_MainTex", texture); EditorUtility.SetDirty(mat); AssetDatabase.SaveAssetIfDirty(mat); }
            cloud.SetFloat("_near_plane", 0);
            cloud.SetFloat("_fade_in_distance", 2);
            cloud.SetFloat("_angle_bias", 0.05f);
            EditorUtility.SetDirty(cloud); AssetDatabase.SaveAssetIfDirty(cloud);
            var wall = Material("Occluder", Shader.Find("Universal Render Pipeline/Lit"), new Color(0.22f, 0.25f, 0.28f));
            var ground = Material("Ground", Shader.Find("Universal Render Pipeline/Lit"), new Color(0.15f, 0.18f, 0.2f));
            var glass = Material("TransparentReference", Shader.Find("Universal Render Pipeline/Unlit"), new Color(0.2f, 0.8f, 0.35f, 0.3f), true);
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(Root + "/BloomProfile.asset");
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, Root + "/BloomProfile.asset");
                var bloom = profile.Add<Bloom>(true); bloom.intensity.Override(0.6f); bloom.threshold.Override(1);
                AssetDatabase.AddObjectToAsset(bloom, profile);
            }
            EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile);

            Scene scene = EditorSceneManager.NewPreviewScene();
            workingScene = scene;
            try
            {
                var camera = NewObject("OSP Example Camera", typeof(Camera)).GetComponent<Camera>();
                camera.transform.position = new Vector3(0, 3, -12);
                camera.transform.LookAt(new Vector3(0, 2, 2));
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.06f, 0.09f, 0.14f);
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 80;
                camera.allowHDR = true;
                camera.cullingMask = (1 << particleLayer) | (1 << geometryLayer);
                camera.GetUniversalAdditionalCameraData().SetRenderer(deferredIndex);
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
                camera.GetUniversalAdditionalCameraData().volumeLayerMask = 1 << geometryLayer;
                var volume = NewObject("Example Bloom", typeof(Volume)).GetComponent<Volume>();
                volume.gameObject.layer = geometryLayer; volume.isGlobal = true; volume.sharedProfile = profile;
                var light = NewObject("Example Sun", typeof(Light)).GetComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.5f; light.shadows = LightShadows.None;
                light.cullingMask = camera.cullingMask; light.transform.rotation = Quaternion.Euler(40, -35, 0);
                Shape("Ground", PrimitiveType.Cube, new Vector3(0, -0.2f, 3), new Vector3(18, 0.3f, 18), ground, geometryLayer);
                Shape("Opaque Wall", PrimitiveType.Cube, new Vector3(-1.5f, 1.7f, 1.5f), new Vector3(1.2f, 3.4f, 0.6f), wall, geometryLayer);
                Shape("Thin Occluder", PrimitiveType.Cube, new Vector3(1, 2, 1), new Vector3(0.06f, 4, 0.15f), wall, geometryLayer);
                Shape("Transparent Ordering Reference", PrimitiveType.Quad, new Vector3(3.8f, 2, 0), new Vector3(2, 3, 1), glass, geometryLayer);
                var workload = NewObject("Cloud Workload");
                for (int i = 0; i < 8; i++)
                {
                    var quad = Shape("Cloud " + i, PrimitiveType.Quad, new Vector3(0, 2.2f, 3 + i * 0.18f), new Vector3(9, 5, 1), cloud, particleLayer);
                    quad.transform.SetParent(workload.transform, true);
                }
                CreateParticles("Smoke Particles", new Vector3(-1, 1, 2.5f), smoke, particleLayer, 417, 30, 2.2f);
                CreateParticles("HDR Particles", new Vector3(2.5f, 1, 1), glow, particleLayer, 418, 8, 0.65f);
                var benchmark = camera.gameObject.AddComponent<OffscreenParticleBenchmark>();
                benchmark.feature = deferred.rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
                benchmark.forwardFeature = forward.rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
                benchmark.cloudWorkload = workload;
                benchmark.forwardRendererIndex = forwardIndex;
                // Unity 2022 cannot save a preview scene. Save its hierarchy as a prefab,
                // then create a scene containing that prefab without touching open scenes.
                var root = NewObject("Offscreen Particles Example");
                foreach (var go in scene.GetRootGameObjects())
                    if (go != root) go.transform.SetParent(root.transform, true);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, Root + "/Example.prefab");
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(prefab.transform, out string guid, out long transformId);
                string yaml = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
                    "--- !u!1001 &1000\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n    m_RemovedGameObjects: []\n    m_AddedGameObjects: []\n    m_AddedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: " + guid + ", type: 3}\n" +
                    "--- !u!4 &1001 stripped\nTransform:\n  m_CorrespondingSourceObject: {fileID: " + transformId + ", guid: " + guid + ", type: 3}\n  m_PrefabInstance: {fileID: 1000}\n  m_PrefabAsset: {fileID: 0}\n";
                File.WriteAllText(ScenePath, yaml);
                AssetDatabase.ImportAsset(ScenePath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                workingScene = default;
            }
            Debug.Log("OSP independent example created. Default renderer and existing scenes preserved.");
        }

        static void CreateParticles(string name, Vector3 position, Material material, int layer, uint seed, float rate, float size)
        {
            var ps = NewObject(name, typeof(ParticleSystem)).GetComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.gameObject.layer = layer; ps.transform.position = position;
            ps.useAutoRandomSeed = false; ps.randomSeed = seed;
            var main = ps.main; main.duration = 5; main.loop = true; main.prewarm = true;
            main.startLifetime = 5; main.startSpeed = 0.3f; main.startSize = size;
            main.maxParticles = 600; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission; emission.rateOverTime = rate;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 1.2f;
            var renderer = ps.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            ps.Play();
        }

        [MenuItem("Tools/Offscreen Particles/Validate and Capture Example")]
        public static void Validate()
        {
            Directory.CreateDirectory(Output);
            UpdateExampleResources();
            // Public in newer editors, internal in 2022.3; avoids disturbing unsaved user scenes.
            var openPreview = typeof(EditorSceneManager).GetMethod("OpenPreviewScene", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (openPreview == null) throw new NotSupportedException("Editor lacks OpenPreviewScene.");
            var scene = (Scene)openPreview.Invoke(null, new object[] { ScenePath });
            var feature = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/OffscreenParticlesDeferred.asset").rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
            var oldResolution = feature.resolution; var oldDebug = feature.debugView;
            bool oldActive = feature.isActive;
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            float oldScale = pipeline.renderScale; int oldMSAA = pipeline.msaaSampleCount;
            try
            {
                feature.SetActive(true);
                var camera = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Camera>()).First();
                camera.scene = scene;
                foreach (var ps in scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<ParticleSystem>())) { ps.Simulate(5, true, true); ps.Pause(); }
                foreach (ParticleResolution scale in Enum.GetValues(typeof(ParticleResolution)))
                {
                    feature.resolution = scale; feature.debugView = ParticleDebugView.None;
                    Capture(camera, scale.ToString(), 1280, 720);
                }
                feature.resolution = ParticleResolution.Half;
                foreach (ParticleDebugView debug in Enum.GetValues(typeof(ParticleDebugView)))
                { feature.debugView = debug; Capture(camera, "Debug_" + debug, 1280, 720); }
                feature.debugView = ParticleDebugView.None;
                camera.orthographic = true; camera.orthographicSize = 4.5f;
                Capture(camera, "Orthographic", 1280, 720); camera.orthographic = false;
                Vector3 originalPosition = camera.transform.position;
                camera.transform.position = new Vector3(0, 2.2f, 3.4f);
                Capture(camera, "InsideCloud", 1280, 720); camera.transform.position = originalPosition;
                Capture(camera, "Portrait", 720, 1280);
                pipeline.renderScale = 0.75f; Capture(camera, "RenderScale075", 1280, 720); pipeline.renderScale = oldScale;
                camera.rect = new Rect(0.15f, 0.1f, 0.7f, 0.8f); Capture(camera, "Viewport", 1280, 720); camera.rect = new Rect(0, 0, 1, 1);
                var other = UnityEngine.Object.Instantiate(camera.gameObject).GetComponent<Camera>();
                SceneManager.MoveGameObjectToScene(other.gameObject, scene);
                other.scene = scene;
                other.transform.position += Vector3.right;
                Capture(other, "SecondCamera", 960, 540); Capture(camera, "AfterSecondCamera", 1280, 720);
                UnityEngine.Object.DestroyImmediate(other.gameObject);
                for (int i = 0; i < 12; i++) { feature.resolution = (ParticleResolution)new[] { 1, 2, 4 }[i % 3]; Capture(camera, "Lifecycle", 640, 360); }
                feature.SetActive(false); Capture(camera, "FeatureDisabled", 640, 360); feature.SetActive(true);
                var forward = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/OffscreenParticlesForward.asset");
                camera.GetUniversalAdditionalCameraData().SetRenderer(AppendRenderer(pipeline, forward));
                pipeline.msaaSampleCount = 1; Capture(camera, "ForwardMSAA1", 1280, 720);
                pipeline.msaaSampleCount = 4; Capture(camera, "ForwardMSAA4", 1280, 720);
                ValidateAnalytic(feature, pipeline);
                var shaderReport = new System.Text.StringBuilder();
                foreach (string shaderName in new[] { "OffscreenParticleProcessing.shader", "OffscreenParticleAlphaBlend.shader", "OffscreenParticleCloudVolume.shader" })
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + shaderName);
                    var messages = ShaderUtil.GetShaderMessages(shader);
                    shaderReport.AppendLine(shaderName + ": supported=" + shader.isSupported + ", messages=" + messages.Length);
                    foreach (var message in messages) shaderReport.AppendLine(message.severity + ": " + message.message);
                    if (messages.Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                        throw new InvalidOperationException("OSP shader errors: " + shaderName);
                }
                File.WriteAllText(Output + "/shader-validation.txt", shaderReport.ToString());
                File.WriteAllText(Output + "/editor-validation.txt", "Completed render-request matrix: Full/Half/Quarter, debug views, perspective/orthographic, portrait, render scale 0.75, viewport, two cameras, 12 reallocations, feature off/on, Forward MSAA1/4.\nInspect PNGs and console; completion alone is not a visual assertion.\n");
                Debug.Log("OSP validation captures complete: " + Path.GetFullPath(Output));
            }
            finally
            {
                feature.resolution = oldResolution; feature.debugView = oldDebug; feature.SetActive(oldActive);
                pipeline.renderScale = oldScale; pipeline.msaaSampleCount = oldMSAA;
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.SaveAssets();
            }
        }

        static void Capture(Camera camera, string name, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = ((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).msaaSampleCount;
            var previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                rt.Create();
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                RenderTexture.active = rt;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
                File.WriteAllBytes(Output + "/" + name + ".png", image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(image); rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
        }

        static void ValidateAnalytic(OffscreenParticleRendererFeature feature, UniversalRenderPipelineAsset pipeline)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var owned = new System.Collections.Generic.List<UnityEngine.Object>();
            var report = new System.Text.StringBuilder("resolution,case,expectedRed,actualRed,passed\n");
            try
            {
                var camera = new GameObject("Analytic Camera", typeof(Camera)).GetComponent<Camera>();
                SceneManager.MoveGameObjectToScene(camera.gameObject, scene); camera.scene = scene;
                camera.transform.position = new Vector3(0, 0, -10);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = UnityEngine.Color.black;
                camera.allowHDR = true; camera.farClipPlane = 50;
                int layer = LayerMask.NameToLayer("OffscreenParticle");
                int geometry = LayerMask.NameToLayer("OffscreenParticleExample");
                camera.cullingMask = (1 << layer) | (1 << geometry);
                camera.GetUniversalAdditionalCameraData().SetRenderer(AppendRenderer(pipeline, AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/OffscreenParticlesDeferred.asset")));
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                var red = new Material(Shader.Find("SGame/OffscreenParticles/AlphaBlend"));
                red.SetColor("_TintColor", new Color(1, 0, 0, 0.5f)); red.SetFloat("_InvFade", 1); owned.Add(red);
                var black = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                black.SetColor("_BaseColor", UnityEngine.Color.black); owned.Add(black);
                Func<Material, int, GameObject> quad = (mat, goLayer) =>
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    SceneManager.MoveGameObjectToScene(go, scene); go.layer = goLayer;
                    go.transform.localScale = new Vector3(6, 6, 1);
                    go.GetComponent<Renderer>().sharedMaterial = mat;
                    var filter = go.GetComponent<MeshFilter>();
                    var mesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
                    mesh.colors = Enumerable.Repeat(UnityEngine.Color.white, mesh.vertexCount).ToArray();
                    filter.sharedMesh = mesh; owned.Add(mesh);
                    return go;
                };
                var first = quad(red, layer); var second = quad(red, layer); var wall = quad(black, geometry);
                second.transform.position = Vector3.forward;
                foreach (ParticleResolution scale in Enum.GetValues(typeof(ParticleResolution)))
                {
                    feature.resolution = scale; feature.debugView = ParticleDebugView.None;
                    foreach (string test in new[] { "premultiplied", "occluded", "soft-intersection" })
                    {
                        second.SetActive(test == "premultiplied"); wall.SetActive(test != "premultiplied");
                        wall.transform.position = Vector3.forward * (test == "occluded" ? -1 : 0.25f);
                        float expected = test == "premultiplied" ? 0.75f : test == "occluded" ? 0 : 0.125f;
                        var rt = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        var previous = RenderTexture.active;
                        Texture2D tex = null;
                        float actual;
                        try
                        {
                            rt.Create();
                            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                            RenderTexture.active = rt; tex = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
                            tex.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); tex.Apply(); actual = tex.GetPixel(64, 64).r;
                        }
                        finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                        bool passed = Mathf.Abs(actual - expected) < 0.02f;
                        report.AppendLine(FormattableString.Invariant($"{scale},{test},{expected},{actual},{passed}"));
                        File.WriteAllText(Output + "/analytic-validation.csv", report.ToString());
                        if (!passed) throw new InvalidOperationException($"OSP analytic test failed: {scale}/{test}: {actual} != {expected}");
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [MenuItem("Tools/Offscreen Particles/Build Windows Validation Player")]
        public static void Build()
        {
            UpdateExampleResources();
            Directory.CreateDirectory(Output + "/Player");
            bool previous = PlayerSettings.enableFrameTimingStats;
            var originalGraphics = GraphicsSettings.defaultRenderPipeline;
            var originalQuality = QualitySettings.renderPipeline;
            var originalBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            string originalIl2CppPath = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
            // BuildPlayer persists temporary settings before compiling. Preserve disk state
            // as well as the live objects, including when unrelated scripts fail to build.
            string[] settingsPaths = { "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset", "ProjectSettings/ProjectSettings.asset" };
            var settingsBytes = settingsPaths.Select(File.ReadAllBytes).ToArray();
            // This example has no hot-update assemblies. Disable HybridCLR only in memory
            // for this validation build; no plugin settings asset is saved.
            var hybridType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HybridCLR.Editor.Settings.HybridCLRSettings")).FirstOrDefault(t => t != null);
            object hybrid = hybridType?.GetProperty("Instance")?.GetValue(null);
            var hybridEnable = hybridType?.GetField("enable");
            bool wasHybridEnabled = hybrid != null && (bool)hybridEnable.GetValue(hybrid);
            try
            {
                var validationPipeline = ValidationPipeline();
                GraphicsSettings.defaultRenderPipeline = validationPipeline;
                QualitySettings.renderPipeline = validationPipeline;
                if (hybrid != null) hybridEnable.SetValue(hybrid, false);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                PlayerSettings.enableFrameTimingStats = true;
                // UnityRHI's no-variant build hook still registers this directory.
                if (Directory.Exists("Packages/top.kuanmi.unityrhi")) Directory.CreateDirectory("Library/UnityRhi/PlayerShaders");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, locationPathName = Output + "/Player/OffscreenParticles.exe",
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
                });
                File.WriteAllText(Output + "/build.txt", report.summary.result + "\nErrors: " + report.summary.totalErrors + "\nWarnings: " + report.summary.totalWarnings + "\nDuration: " + report.summary.totalTime);
                if (report.summary.result != BuildResult.Succeeded) throw new Exception("OSP validation build failed; see build.txt and Console.");
                Debug.Log("OSP Windows development build succeeded.");
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = previous;
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, originalBackend);
                GraphicsSettings.defaultRenderPipeline = originalGraphics;
                QualitySettings.renderPipeline = originalQuality;
                if (hybrid != null) hybridEnable.SetValue(hybrid, wasHybridEnabled);
                Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", originalIl2CppPath);
                AssetDatabase.SaveAssets();
                for (int i = 0; i < settingsPaths.Length; i++) File.WriteAllBytes(settingsPaths[i], settingsBytes[i]);
            }
        }

        static UniversalRenderPipelineAsset ValidationPipeline()
        {
            string path = Root + "/ValidationPipeline.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (pipeline != null) return pipeline;
            pipeline = UnityEngine.Object.Instantiate((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline);
            pipeline.name = "Offscreen Particles Validation Pipeline";
            var deferred = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/OffscreenParticlesDeferred.asset");
            var forward = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root + "/OffscreenParticlesForward.asset");
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            // Preserve the example camera's renderer indices, removing unrelated feature dependencies.
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue != forward) list.GetArrayElementAtIndex(i).objectReferenceValue = deferred;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(pipeline, path);
            return pipeline;
        }

        // Entry point for the disposable Library/OffscreenParticleValidationProject copy.
        public static void BuildIsolated()
        {
            if (!Application.dataPath.Replace('\\', '/').Contains("/Library/OffscreenParticleValidationProject/"))
                throw new InvalidOperationException("BuildIsolated is only for the disposable validation project.");
            GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root + "/ValidationPipeline.asset");
            QualitySettings.renderPipeline = null;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
            PlayerSettings.productName = "OffscreenParticlesValidation";
            PlayerSettings.companyName = "SGame";
            Build();
        }

        static void UpdateExampleResources()
        {
            var deferred = Renderer("OffscreenParticlesDeferred", RenderingMode.Deferred, LayerMask.NameToLayer("OffscreenParticle"));
            var forward = Renderer("OffscreenParticlesForward", RenderingMode.Forward, LayerMask.NameToLayer("OffscreenParticle"));
            string prefabPath = Root + "/Example.prefab";
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var benchmark = contents.GetComponentInChildren<OffscreenParticleBenchmark>();
                benchmark.feature = deferred.rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
                benchmark.forwardFeature = forward.rendererFeatures.OfType<OffscreenParticleRendererFeature>().First();
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
    }
}
