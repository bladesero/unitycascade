using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace ShurikenCascade
{
    // Owns only preview state; never registers a scene Volume or edits a source Profile.
    internal sealed class CascadePreviewEnvironment : IDisposable
    {
        // HideAndDontSave also includes NotEditable, which disables native SerializedProperty fields.
        const HideFlags EditablePreviewFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector | HideFlags.DontSave;
        internal bool Enabled;
        internal float Weight = 1;
        internal VolumeProfile Source { get; private set; }
        internal VolumeProfile Profile { get; private set; }
        internal int Revision { get; private set; }
        internal event Action ReplacingProfile;
        VolumeStack stack, defaults;
        int observedHash, stackRevision = -1;
        readonly UniversalRenderPipeline.SingleCameraRequest request = new UniversalRenderPipeline.SingleCameraRequest();
        internal bool Supported => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

        internal void SetSource(VolumeProfile source)
        {
            ReplacingProfile?.Invoke();
            DestroyProfile(); Source = source;
            Profile = ScriptableObject.CreateInstance<VolumeProfile>();
            Profile.name = "Cascade Preview Volume"; Profile.hideFlags = EditablePreviewFlags;
            if (source)
            {
                foreach (var component in source.components)
                {
                    if (!component) continue;
                    var clone = Object.Instantiate(component);
                    clone.hideFlags = EditablePreviewFlags;
                    Profile.components.Add(clone);
                }
            }
            else
            {
                var bloom = Profile.Add<Bloom>(); bloom.intensity.Override(0.5f); bloom.threshold.Override(1);
                Profile.Add<ColorAdjustments>(); Profile.Add<Tonemapping>(); Profile.Add<Vignette>();
                foreach (var component in Profile.components) component.hideFlags = EditablePreviewFlags;
            }
            NotifyChanged();
        }

        internal void NotifyChanged() { Revision++; observedHash = Hash(); }
        internal bool PollChanges()
        {
            int hash = Hash(); if (hash == observedHash) return false;
            observedHash = hash; Revision++; return true;
        }
        int Hash()
        {
            unchecked
            {
                int hash = (Enabled ? 1 : 0) * 397 ^ Weight.GetHashCode();
                if (Profile)
                    foreach (var c in Profile.components)
                        hash = hash * 397 ^ (c ? c.GetHashCode() ^ (c.active ? c.GetType().GetHashCode() : ~c.GetType().GetHashCode()) : 0);
                return hash;
            }
        }

        internal VolumeStack PrepareStack()
        {
            if (!Profile) SetSource(Source);
            var manager = VolumeManager.instance;
            if (stack == null) { stack = manager.CreateStack(); defaults = manager.CreateStack(); stackRevision = -1; }
            if (stackRevision == Revision) return stack;
            // Reset from type defaults, not scene globals (even if a scene uses every Volume layer).
            foreach (var type in manager.baseComponentTypeArray)
            {
                var dst = stack.GetComponent(type); var src = defaults.GetComponent(type);
                if (!dst || !src) continue;
                for (int i = 0; i < dst.parameters.Count; i++)
                { dst.parameters[i].SetValue(src.parameters[i]); dst.parameters[i].overrideState = src.parameters[i].overrideState; }
            }
            if (Enabled)
                foreach (var component in Profile.components)
                {
                    if (!component || !component.active) continue;
                    var state = stack.GetComponent(component.GetType());
                    if (state) component.Override(state, Mathf.Clamp01(Weight));
                }
            stackRevision = Revision; return stack;
        }

        internal void Render(PreviewRenderUtility utility)
        {
            if (!Enabled || !Supported) { utility.Render(true); return; }
            PollChanges();
            var camera = utility.camera;
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (!data) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.hideFlags = HideFlags.HideAndDontSave;
            data.renderPostProcessing = true; data.renderShadows = false;
            data.volumeLayerMask = 0; data.volumeTrigger = camera.transform;
            data.antialiasing = AntialiasingMode.None;
            var manager = VolumeManager.instance;
            var previousStack = manager.stack; var previousType = camera.cameraType;
            var target = camera.targetTexture; var active = RenderTexture.active;
            float previousFov = camera.fieldOfView;
            try
            {
                manager.stack = PrepareStack();
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = utility.ambientColor;
                float multiplier = target && target.width > 0 ? Mathf.Max(1f, (float)target.height / target.width) : 1f;
                camera.fieldOfView = Mathf.Atan(multiplier * Mathf.Tan(previousFov * 0.5f * Mathf.Deg2Rad)) * Mathf.Rad2Deg * 2f;
                // The single-camera request bypasses scene Volume blending but retains camera data.
                // PreviewRenderUtility still owns the scene, lighting override and destination texture.
                camera.cameraType = CameraType.Game;
                request.destination = target;
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            finally
            {
                request.destination = null; camera.cameraType = previousType;
                camera.fieldOfView = previousFov;
                camera.targetTexture = target; RenderTexture.active = active; manager.stack = previousStack;
            }
        }

        void DestroyProfile()
        {
            if (!Profile) return;
            foreach (var c in Profile.components) if (c) { Undo.ClearUndo(c); Object.DestroyImmediate(c); }
            Undo.ClearUndo(Profile); Object.DestroyImmediate(Profile); Profile = null;
        }
        public void Dispose()
        {
            ReplacingProfile?.Invoke(); DestroyProfile();
            stack?.Dispose(); defaults?.Dispose(); stack = defaults = null; stackRevision = -1;
        }
    }
}
