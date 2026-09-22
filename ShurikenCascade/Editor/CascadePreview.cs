using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ShurikenCascade
{
    public sealed class CascadePreview : IDisposable
    {
        PreviewRenderUtility utility;
        GameObject container;
        Texture cachedTexture; // Owned by PreviewRenderUtility, never separately destroyed.
        int renderedFrame = -1;
        Vector2 renderedSize, renderedOrbit;
        Vector3 renderedPivot;
        float renderedDistance, renderedPixelsPerPoint;
        Color renderedBackground;
        bool renderFailed;
        internal int RenderSubmissionCount { get; private set; }
        internal RenderTexture TargetTexture => cachedTexture as RenderTexture;
        internal void InvalidateRender() { renderedFrame = -1; if (renderFailed) { Error = null; renderFailed = false; } }
        ParticleSystem[] systems = Array.Empty<ParticleSystem>();
        ParticleSystem[] drivers = Array.Empty<ParticleSystem>();
        readonly Dictionary<string, ParticleSystemRenderer> renderers = new Dictionary<string, ParticleSystemRenderer>();
        readonly Dictionary<string, bool> originalVisibility = new Dictionary<string, bool>();
        public readonly HashSet<string> Hidden = new HashSet<string>();
        public string Solo;
        public bool Playing = true;
        public float Speed = 1f;
        public int PreviewLOD;
        public string PreviewQualityName;
        internal string AppliedQualityName { get; private set; }
        internal const int FramesPerSecond = 60;
        internal int CurrentFrame { get; private set; }
        public float Time => CurrentFrame / (float)FramesPerSecond;
        public int ParticleCount => Statistics.ParticleCount;
        internal CascadePreviewStatistics Statistics { get; } = new CascadePreviewStatistics();
        internal bool IsSeeking { get; private set; }
        internal int SeekTargetFrame { get; private set; }
        internal float SeekProgress => !IsSeeking ? 1 : Mathf.InverseLerp(seekStartFrame, SeekTargetFrame, CurrentFrame);
        internal double SeekMilliseconds { get; private set; }
        internal int EndFrame { get; private set; } = 600;
        internal int LoopStartFrame { get; private set; }
        internal int LoopEndFrame { get; private set; } = 600;
        internal bool Loop { get; private set; }
        double pendingFrames;
        int seekStartFrame;
        bool resetForSeek;
        internal IReadOnlyList<ParticleSystem> PreviewSystems => systems;
        public Vector2 Orbit = new Vector2(25f, -25f);
        public Vector3 Pivot;
        public float Distance = 8f;
        public Color Background = new Color(0.09f, 0.1f, 0.12f, 1f);
        internal CascadePreviewEnvironment Environment { get; } = new CascadePreviewEnvironment();
        public string Error { get; private set; }
        public bool Ready => container;

        public void Rebuild(GameObject document)
        {
            DisposeScene();
            if (!document) return;
            try
            {
                utility = new PreviewRenderUtility();
                utility.camera.fieldOfView = 35f;
                utility.camera.nearClipPlane = 0.01f;
                utility.camera.farClipPlane = 10000f;
                utility.camera.clearFlags = CameraClearFlags.SolidColor;
                utility.camera.allowHDR = true;
                utility.camera.cullingMask = ~0;
                utility.ambientColor = new Color(0.3f, 0.3f, 0.3f);
                utility.lights[0].intensity = 1.2f;
                utility.lights[0].transform.rotation = Quaternion.Euler(45, 35, 0);
                utility.lights[1].intensity = 0.6f;
                utility.lights[1].transform.rotation = Quaternion.Euler(340, 210, 0);
                container = new GameObject("Shuriken Cascade Preview") { hideFlags = HideFlags.HideAndDontSave };
                container.SetActive(false);
                utility.AddSingleGO(container);
                var clone = Object.Instantiate(document, container.transform, false);
                clone.name = document.name;
                // Strip script behaviours before activating the clone: authoring tools never run gameplay.
                foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (script && !(script is ParticleDistanceLOD)) Object.DestroyImmediate(script);
                var previewLODs = clone.GetComponentsInChildren<ParticleDistanceLOD>(true).Where(lod => lod.enabled).ToArray();
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
                    if (!(behaviour is ParticleSystemForceField) && !(behaviour is Collider2D)) behaviour.enabled = false;
                // Keep authored layers: collision masks and force-field masks depend on them.
                systems = clone.GetComponentsInChildren<ParticleSystem>(true);
                var subs = new HashSet<ParticleSystem>();
                foreach (var p in systems)
                {
                    var main = p.main;
                    main.stopAction = ParticleSystemStopAction.None;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                    uint seed = p.useAutoRandomSeed ? StableSeed(CascadeSession.Key(p.transform, clone.transform)) : p.randomSeed;
                    p.useAutoRandomSeed = false;
                    p.randomSeed = seed == 0 ? 1u : seed;
                    var sub = p.subEmitters;
                    for (int i = 0; sub.enabled && i < sub.subEmittersCount; i++)
                        if (sub.GetSubEmitterSystem(i)) subs.Add(sub.GetSubEmitterSystem(i));
                    var renderer = p.GetComponent<ParticleSystemRenderer>();
                    if (renderer)
                    {
                        string key = CascadeSession.Key(p.transform, clone.transform);
                        renderers[key] = renderer;
                        originalVisibility[key] = renderer.enabled;
                    }
                }
                // Ordinary child systems are independent drivers. Referenced sub-emitters are driven
                // only by their parent particle events, never started independently.
                // Preserve authored sub-emitter classification before applying quality gates.
                string quality = PreviewQualityName;
                if (quality == null)
                {
                    var names = QualitySettings.names;
                    int index = QualitySettings.GetQualityLevel();
                    quality = index >= 0 && index < names.Length ? names[index] : null;
                }
                foreach (var lod in previewLODs) lod.ApplyPreviewLOD(PreviewLOD, quality);
                AppliedQualityName = quality;
                var authoredDrivers = systems.Where(p => !subs.Contains(p)).ToArray();
                drivers = authoredDrivers.Where(p => !previewLODs.Any(lod => lod.IsEmitterBlocked(p))).ToArray();
                Statistics.Rebuild(systems, authoredDrivers, clone.transform);
                container.SetActive(true);
                Restart();
                ApplyVisibility();
                Error = null;
            }
            catch (Exception ex)
            {
                DisposeScene();
                Error = "预览创建失败：" + ex.Message;
            }
        }

        public void Restart()
        {
            CancelSeek(false);
            ResetSimulation();
        }

        static uint StableSeed(string key)
        {
            uint seed = 2166136261;
            foreach (char c in key) seed = unchecked((seed ^ c) * 16777619);
            return seed == 0 ? 1 : seed;
        }

        void ResetSimulation()
        {
            InvalidateRender();
            CurrentFrame = 0;
            pendingFrames = 0;
            foreach (var p in systems) if (p) p.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var p in drivers)
                if (p && p.gameObject.activeInHierarchy) p.Simulate(0f, false, true, true);
            Statistics.ObserveParticles();
        }

        internal void SetRange(int endFrame, bool loop, int startFrame, int loopEndFrame)
        {
            EndFrame = Mathf.Clamp(endFrame, 60, 36000);
            LoopStartFrame = Mathf.Clamp(startFrame, 0, EndFrame - 1);
            LoopEndFrame = Mathf.Clamp(loopEndFrame, LoopStartFrame + 1, EndFrame);
            Loop = loop;
            if (CurrentFrame > EndFrame || (IsSeeking && SeekTargetFrame > EndFrame)) RequestSeekFrame(EndFrame);
        }

        internal static int ToFrame(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
            return (int)Math.Round(Math.Max(0, Math.Min(600, seconds)) * FramesPerSecond, MidpointRounding.AwayFromZero);
        }

        internal void RequestSeek(double seconds) => RequestSeekFrame(ToFrame(seconds));

        internal void RequestSeekFrame(int frame)
        {
            Playing = false;
            BeginSeek(frame);
        }

        void BeginSeek(int frame)
        {
            SeekTargetFrame = Mathf.Clamp(frame, 0, EndFrame);
            resetForSeek = SeekTargetFrame < CurrentFrame;
            seekStartFrame = resetForSeek ? 0 : CurrentFrame;
            pendingFrames = 0;
            SeekMilliseconds = 0;
            IsSeeking = resetForSeek || SeekTargetFrame != CurrentFrame;
        }

        internal void StepFrames(int delta) => RequestSeekFrame((IsSeeking ? SeekTargetFrame : CurrentFrame) + delta);

        internal void CancelSeek(bool pause = true)
        {
            IsSeeking = resetForSeek = false;
            pendingFrames = 0;
            if (pause) Playing = false;
        }

        internal void TogglePlaying()
        {
            if (IsSeeking) CancelSeek(false);
            Playing = !Playing;
            if (Playing && CurrentFrame >= (Loop ? LoopEndFrame : EndFrame))
            {
                if (Loop) BeginSeek(LoopStartFrame);
                else Restart();
            }
        }

        void AdvanceOneFrame(bool seeking)
        {
            double totalMs = 0;
            foreach (var p in drivers)
                if (p && p.gameObject.activeInHierarchy)
                {
                    long start = Stopwatch.GetTimestamp();
                    p.Simulate(1f / FramesPerSecond, false, false, false);
                    double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    if (!seeking) Statistics.RecordDriver(p, elapsed);
                    totalMs += elapsed;
                }
            if (!seeking) Statistics.Simulation.Add(totalMs);
            CurrentFrame++;
            Statistics.ObserveParticles();
        }

        public void Tick(float delta)
        {
            if (!container || Error != null) return;
            long start = Stopwatch.GetTimestamp();
            if (IsSeeking)
            {
                if (resetForSeek) { ResetSimulation(); resetForSeek = false; }
                while (CurrentFrame < SeekTargetFrame)
                {
                    AdvanceOneFrame(true);
                    if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= 8) break;
                }
                SeekMilliseconds += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (CurrentFrame >= SeekTargetFrame) IsSeeking = false;
                return;
            }
            if (!Playing) return;
            pendingFrames += Mathf.Clamp(delta, 0, 0.1f) * (double)Speed * FramesPerSecond;
            while (pendingFrames + 1e-6 >= 1)
            {
                int boundary = Loop ? LoopEndFrame : EndFrame;
                if (CurrentFrame >= boundary)
                {
                    if (Loop) BeginSeek(LoopStartFrame);
                    else { Playing = false; pendingFrames = 0; }
                    break;
                }
                AdvanceOneFrame(false);
                pendingFrames = Math.Max(0, pendingFrames - 1);
                if (CurrentFrame == boundary)
                {
                    if (Loop) BeginSeek(LoopStartFrame);
                    else { Playing = false; pendingFrames = 0; }
                    break;
                }
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= 8) break;
            }
        }

        internal void RefreshEnvironment()
        {
            if (Environment.PollChanges()) InvalidateRender();
        }

        public void ApplyVisibility()
        {
            InvalidateRender();
            foreach (var pair in renderers)
                if (pair.Value) pair.Value.enabled = originalVisibility[pair.Key] && !Hidden.Contains(pair.Key) && (Solo == null || Solo == pair.Key);
            Statistics.RefreshSnapshot(true);
        }

        public void Frame()
        {
            var visible = renderers.Values.Where(r => r && r.enabled).ToArray();
            if (visible.Length == 0) return;
            Bounds bounds = new Bounds(visible[0].transform.position, Vector3.one);
            foreach (var r in visible)
            {
                bounds.Encapsulate(r.transform.position);
                if (r.bounds.size.sqrMagnitude > 0 && r.bounds.size.sqrMagnitude < 1e10f) bounds.Encapsulate(r.bounds);
            }
            Pivot = bounds.center;
            Distance = Mathf.Clamp(bounds.extents.magnitude * 3.5f, 1f, 10000f);
        }

        public void Draw(Rect rect) => Draw(rect, true);

        internal Camera HandleCameraObject { get { if (utility == null) return null; PrepareCamera(); return utility.camera; } }

        internal void Draw(Rect rect, bool handleInput)
        {
            EditorGUI.DrawRect(rect, Background);
            if (Error != null) { GUI.Label(rect, Error, EditorStyles.wordWrappedLabel); return; }
            if (utility == null || rect.width < 1 || rect.height < 1) return;
            PrepareCamera();
            if (handleInput) HandleCamera(rect);
            if (Event.current.type != EventType.Repaint) return;
            bool render = !cachedTexture || renderedFrame != CurrentFrame || renderedSize != rect.size ||
                renderedOrbit != Orbit || renderedPivot != Pivot || renderedDistance != Distance ||
                renderedBackground != Background || renderedPixelsPerPoint != EditorGUIUtility.pixelsPerPoint;
            if (render)
            {
                PrepareCamera();
                utility.BeginPreview(rect, GUIStyle.none);
                long renderStart = Stopwatch.GetTimestamp();
                bool seeking = IsSeeking;
                try { Environment.Render(utility); RenderSubmissionCount++; }
                catch (Exception ex) { renderFailed = true; Error = "项目渲染管线未能渲染预览：" + ex.Message; }
                finally
                {
                    cachedTexture = utility.EndPreview();
                    if (!seeking && Error == null) Statistics.Rendering.Add((Stopwatch.GetTimestamp() - renderStart) * 1000.0 / Stopwatch.Frequency);
                }
                renderedFrame = CurrentFrame; renderedSize = rect.size; renderedOrbit = Orbit;
                renderedPivot = Pivot; renderedDistance = Distance; renderedBackground = Background;
                renderedPixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            }
            if (cachedTexture) GUI.DrawTexture(rect, cachedTexture, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, rect.width - 16, 40),
                $"{Time:0.00}s · {ParticleCount} particles · CPU {Statistics.Simulation.Mean:0.00} ms/步\n拖动旋转 · 中键平移 · 滚轮缩放 · F 聚焦", EditorStyles.whiteMiniLabel);
        }

        void PrepareCamera()
        {
            utility.camera.transform.rotation = Quaternion.Euler(Orbit.y, Orbit.x, 0);
            utility.camera.transform.position = Pivot - utility.camera.transform.forward * Distance;
            utility.camera.backgroundColor = Background;
        }

        // A render-based regression check can use the same camera and SRP path as the window.
        internal Texture2D RenderSnapshot(int width, int height)
        {
            PrepareCamera();
            utility.BeginStaticPreview(new Rect(0, 0, width, height));
            Environment.Render(utility);
            return utility.EndStaticPreview();
        }

        internal void HandleCamera(Rect rect)
        {
            Event e = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (e.type == EventType.MouseDown && GUIUtility.hotControl == 0 && rect.Contains(e.mousePosition) && (e.button == 0 || e.button == 2))
            { GUIUtility.hotControl = control; e.Use(); }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == control)
            { GUIUtility.hotControl = 0; e.Use(); }
            else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            {
                if (e.button == 2 || e.shift)
                {
                    Quaternion rotation = Quaternion.Euler(Orbit.y, Orbit.x, 0);
                    Pivot += rotation * new Vector3(-e.delta.x, e.delta.y, 0) * Distance * 0.002f;
                }
                else { Orbit.x += e.delta.x * 0.5f; Orbit.y = Mathf.Clamp(Orbit.y + e.delta.y * 0.5f, -89, 89); }
                e.Use();
            }
            else if (GUIUtility.hotControl == 0 && rect.Contains(e.mousePosition) && e.type == EventType.ScrollWheel)
            { Distance = Mathf.Clamp(Distance * Mathf.Exp(e.delta.y * 0.08f), 0.05f, 10000f); e.Use(); }
            else if (GUIUtility.hotControl == 0 && rect.Contains(e.mousePosition) && e.type == EventType.KeyDown && e.keyCode == KeyCode.F)
            { Frame(); e.Use(); }
        }

        void DisposeScene()
        {
            cachedTexture = null; InvalidateRender();
            CancelSeek(false);
            CurrentFrame = 0;
            Statistics.Clear();
            systems = drivers = Array.Empty<ParticleSystem>();
            renderers.Clear();
            originalVisibility.Clear();
            if (container) Object.DestroyImmediate(container);
            container = null;
            utility?.Cleanup();
            utility = null;
        }

        public void Dispose() { Environment.Dispose(); DisposeScene(); }
    }
}
