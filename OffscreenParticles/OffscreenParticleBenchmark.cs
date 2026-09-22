using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SGame.Rendering.OffscreenParticles
{
    /// <summary>Deterministic example and opt-in development-player benchmark.</summary>
    public sealed class OffscreenParticleBenchmark : MonoBehaviour
    {
        public OffscreenParticleRendererFeature feature;
        public OffscreenParticleRendererFeature forwardFeature;
        public GameObject cloudWorkload;
        public int forwardRendererIndex;
        readonly List<GameObject> extraClouds = new List<GameObject>();
        readonly FrameTiming[] timings = new FrameTiming[1];
        bool running;
        string label = "Full / Half / Quarter: select the renderer feature in the Inspector.";

        IEnumerator Start()
        {
            if (!Environment.GetCommandLineArgs().Contains("--osp-benchmark")) yield break;
            running = true;
            Application.runInBackground = true;
            var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var camera = GetComponent<Camera>();
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--osp-forward"))
            {
                camera.GetUniversalAdditionalCameraData().SetRenderer(forwardRendererIndex);
                // Resolve the feature on the selected renderer data through an explicit serialized reference.
                if (forwardFeature != null) feature = forwardFeature;
            }
            camera.orthographic = args.Contains("--osp-orthographic"); camera.orthographicSize = 4.5f;
            if (args.Contains("--osp-scale075")) pipeline.renderScale = 0.75f;
            if (args.Contains("--osp-msaa4")) pipeline.msaaSampleCount = 4;
            string output = Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith("--osp-output="));
            output = output == null ? Path.Combine(Application.persistentDataPath, "OffscreenParticles") : output.Substring("--osp-output=".Length);
            Directory.CreateDirectory(output);
            int oldVSync = QualitySettings.vSyncCount, oldTarget = Application.targetFrameRate;
            var oldResolution = feature.resolution; var oldDebug = feature.debugView;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            feature.EnableTiming(true); feature.debugView = ParticleDebugView.None;
            var csv = new System.Text.StringBuilder("load,resolution,width,height,frames,gpuFrameSamples,rejectedGpuSamples,gpuFrameMs,depthMs,drawMs,compositeMs,cpuFrameMs\n");
            var raw = new System.Text.StringBuilder("load,resolution,frame,timestamp,gpuMs,accepted\n");
            File.WriteAllText(Path.Combine(output, "environment.txt"), SystemInfo.graphicsDeviceName + "\n" + SystemInfo.graphicsDeviceType +
                "\nUnity " + Application.unityVersion + "\n" + SystemInfo.operatingSystem + "\nFrame timing enabled: " + FrameTimingManager.IsFeatureEnabled() +
                "\nRender scale: " + pipeline.renderScale + "\nMSAA: " + pipeline.msaaSampleCount + "\nOrthographic: " + camera.orthographic);
            try
            {
                foreach (bool high in new[] { false, true })
                {
                    SetHighLoad(high);
                    foreach (ParticleResolution scale in new[] { ParticleResolution.Full, ParticleResolution.Half, ParticleResolution.Quarter })
                    {
                        feature.resolution = scale;
                        label = (high ? "High" : "Low") + " " + scale;
                        // Identical simulation state for every comparison; cloud geometry remains fixed.
                        foreach (var ps in FindObjectsOfType<ParticleSystem>()) { ps.Simulate(5, true, true); ps.Pause(); }
                        for (int i = 0; i < 180; i++) { FrameTimingManager.CaptureFrameTimings(); yield return null; }
                        if (args.Contains("--osp-renderdoc"))
                        {
                            OffscreenParticleCapture.Queue(Path.Combine(output, label.Replace(' ', '_')));
                            // Exclude capture overhead from the timing interval.
                            for (int i = 0; i < 30; i++) yield return null;
                        }
                        double gpu = 0, cpu = 0; Vector3 passes = Vector3.zero; int samples = 0, rejected = 0;
                        ulong previousTimestamp = 0;
                        for (int i = 0; i < 240; i++)
                        {
                            FrameTimingManager.CaptureFrameTimings();
                            yield return null;
                            uint count = FrameTimingManager.GetLatestTimings(1, timings);
                            if (count > 0 && timings[0].frameStartTimestamp != previousTimestamp)
                            {
                                // D3D11 disjoint/startup timestamps can yield huge invalid durations.
                                // Retain every unique raw sample and report rejected samples explicitly.
                                double milliseconds = timings[0].gpuFrameTime;
                                bool valid = milliseconds > 0 && milliseconds < 1000 && !double.IsNaN(milliseconds);
                                raw.AppendLine(string.Join(",", high ? "high" : "low", scale.ToString(), i.ToString(),
                                    timings[0].frameStartTimestamp.ToString(), milliseconds.ToString("R", CultureInfo.InvariantCulture), valid.ToString()));
                                previousTimestamp = timings[0].frameStartTimestamp;
                                if (valid) { gpu += milliseconds; samples++; } else rejected++;
                            }
                            passes += feature.GpuPassMilliseconds;
                            cpu += Time.unscaledDeltaTime * 1000.0;
                        }
                        Func<double, string> number = x => x.ToString("F6", CultureInfo.InvariantCulture);
                        csv.AppendLine(string.Join(",", high ? "high" : "low", scale.ToString(), Screen.width.ToString(), Screen.height.ToString(), "240",
                            samples.ToString(), rejected.ToString(), samples > 0 ? number(gpu / samples) : "NA", number(passes.x / 240), number(passes.y / 240), number(passes.z / 240), number(cpu / 240)));
                        File.WriteAllText(Path.Combine(output, "gpu-timings.csv"), csv.ToString());
                        File.WriteAllText(Path.Combine(output, "gpu-samples.csv"), raw.ToString());
                        ScreenCapture.CaptureScreenshot(Path.Combine(output, label.Replace(' ', '_') + ".png"));
                        yield return new WaitForEndOfFrame();
                    }
                }
                File.WriteAllText(Path.Combine(output, "complete.txt"), "Benchmark completed. NA or zero GPU timings indicate unavailable counters, not zero rendering cost.");
            }
            finally
            {
                SetHighLoad(false); feature.EnableTiming(false);
                feature.resolution = oldResolution; feature.debugView = oldDebug;
                QualitySettings.vSyncCount = oldVSync; Application.targetFrameRate = oldTarget;
            }
            if (!Application.isEditor) Application.Quit();
            running = false;
        }

        void SetHighLoad(bool high)
        {
            foreach (var go in extraClouds) Destroy(go);
            extraClouds.Clear();
            if (!high || cloudWorkload == null) return;
            for (int i = 0; i < 15; i++)
            {
                var go = Instantiate(cloudWorkload, cloudWorkload.transform.parent);
                go.transform.position += Vector3.forward * (i + 1) * 0.025f;
                extraClouds.Add(go);
            }
        }

        void OnGUI()
        {
            if (running) return;
            GUILayout.BeginArea(new Rect(12, 12, 420, 120), GUI.skin.box);
            GUILayout.Label("URP Offscreen Particles — " + feature.resolution);
            GUILayout.BeginHorizontal();
            foreach (ParticleResolution scale in new[] { ParticleResolution.Full, ParticleResolution.Half, ParticleResolution.Quarter })
                if (GUILayout.Button(scale.ToString())) feature.resolution = scale;
            GUILayout.EndHorizontal();
            GUILayout.Label("Opaque wall / thin edge / transparent ordering / HDR bloom");
            GUILayout.EndArea();
        }
    }
}
