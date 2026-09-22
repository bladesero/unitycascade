using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShurikenCascade
{
    [Serializable]
    public sealed class ParticleEmitterQuality
    {
        public ParticleSystem emitter;
        // A deny list preserves legacy prefabs and enables newly added quality names by default.
        public List<string> disabledQualities = new List<string>();
    }

    public sealed partial class ParticleDistanceLOD
    {
        public List<ParticleEmitterQuality> emitterQuality = new List<ParticleEmitterQuality>();
        public string CurrentQualityName { get; private set; }
        float nextQualityCheck;
        bool previewQuality;
        static int cachedQualityFrame = -1, cachedQualityIndex = -1;
        static string cachedQualityName;
        readonly List<QualityState> qualityStates = new List<QualityState>();
        readonly List<QualityLink> qualityLinks = new List<QualityLink>();

        public bool IsEmitterAllowed(ParticleSystem emitter, string qualityName)
        {
            if (emitterQuality != null && qualityName != null)
                foreach (var rule in emitterQuality)
                    if (rule != null && rule.emitter == emitter && rule.disabledQualities != null &&
                        rule.disabledQualities.Contains(qualityName)) return false;
            return true;
        }

        public bool IsEmitterBlocked(ParticleSystem emitter)
        {
            foreach (var state in qualityStates)
                if (state.System == emitter) return state.Blocked;
            return false;
        }

        /// <summary>Immediately reads project quality, independently of the distance selection method.</summary>
        public void RefreshQuality()
        {
            if (previewQuality) return;
            nextQualityCheck = Time.unscaledTime + .25f;
            CurrentQualityName = ProjectQualityName();
            Capture();
            ApplyCurrentSettings();
        }

        static string ProjectQualityName()
        {
            int index = QualitySettings.GetQualityLevel();
            // Unity returns a new names array. Share the lookup across effects checked this frame.
            if (cachedQualityFrame != Time.frameCount || cachedQualityIndex != index)
            {
                var names = QualitySettings.names;
                cachedQualityName = index >= 0 && index < names.Length ? names[index] : null;
                cachedQualityFrame = Time.frameCount; cachedQualityIndex = index;
            }
            return cachedQualityName;
        }

        void PollQuality()
        {
            nextQualityCheck = Time.unscaledTime + .25f;
            if (!previewQuality && (!captured || CurrentQualityName != ProjectQualityName())) RefreshQuality();
        }

        /// <summary>Only call on a disabled preview copy; never changes global QualitySettings.</summary>
        public void ApplyPreviewLOD(int index, string qualityName)
        {
            previewQuality = true;
            CurrentQualityName = qualityName;
            SetLOD(index);
        }

        void CaptureQuality()
        {
            var owned = new Dictionary<ParticleSystem, QualityState>();
            foreach (var system in GetComponentsInChildren<ParticleSystem>(true))
                if (system.GetComponentInParent<ParticleDistanceLOD>(true) == this)
                {
                    var state = new QualityState(system);
                    qualityStates.Add(state);
                    owned.Add(system, state);
                }
            // Incoming links are managed by the target's owner. This also covers links across
            // nested controllers without two controllers overwriting the same link probability.
            foreach (var source in transform.root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var sub = source.subEmitters;
                for (int i = 0; i < sub.subEmittersCount; i++)
                {
                    var target = sub.GetSubEmitterSystem(i);
                    if (!target || !owned.TryGetValue(target, out var state)) continue;
                    if (sub.enabled) state.IsSubEmitter = true;
                    qualityLinks.Add(new QualityLink(source, target, i, sub.GetSubEmitterEmitProbability(i)));
                }
            }
        }

        void ApplyQuality()
        {
            // Mask all incoming events before stopping anything (including death emitters).
            foreach (var link in qualityLinks)
                link.Apply(!IsEmitterAllowed(link.Target, CurrentQualityName));
            foreach (var state in qualityStates)
                state.Apply(!IsEmitterAllowed(state.System, CurrentQualityName), !previewQuality);
        }

        void LateUpdate()
        {
            // Play(true), PlayOnAwake and pooled activations may start a blocked child again.
            // Keep it stopped without recursively affecting its allowed children.
            foreach (var state in qualityStates) state.EnforceBlocked();
        }

        void RestoreQuality()
        {
            foreach (var state in qualityStates) state.Restore();
            foreach (var link in qualityLinks) link.Apply(false);
            qualityStates.Clear(); qualityLinks.Clear();
            CurrentQualityName = null;
            previewQuality = false;
        }

        sealed class QualityLink
        {
            readonly ParticleSystem source;
            public readonly ParticleSystem Target;
            readonly int index;
            readonly float probability;
            public QualityLink(ParticleSystem source, ParticleSystem target, int index, float probability)
            { this.source = source; Target = target; this.index = index; this.probability = probability; }
            public void Apply(bool blocked)
            {
                if (!source || !Target) return;
                var sub = source.subEmitters;
                // Do not overwrite a link that gameplay has structurally replaced.
                if (index < sub.subEmittersCount && sub.GetSubEmitterSystem(index) == Target)
                    sub.SetSubEmitterEmitProbability(index, blocked ? 0 : probability);
            }
        }

        sealed class QualityState
        {
            public readonly ParticleSystem System;
            readonly ParticleSystemRenderer renderer;
            readonly bool forceRenderingOff;
            readonly ParticleSystemStopAction stopAction;
            bool resumeLoop;
            public bool Blocked { get; private set; }
            public bool IsSubEmitter;

            public QualityState(ParticleSystem system)
            {
                System = system;
                renderer = system.GetComponent<ParticleSystemRenderer>();
                forceRenderingOff = renderer && renderer.forceRenderingOff;
                stopAction = system.main.stopAction;
            }

            public void Apply(bool blocked, bool allowResume)
            {
                if (!System) return;
                bool wasBlocked = Blocked;
                Blocked = blocked;
                if (blocked)
                {
                    if (!wasBlocked) resumeLoop = System.isPlaying;
                    EnforceBlocked();
                }
                else if (wasBlocked)
                {
                    Restore();
                    if (allowResume && resumeLoop && System.main.loop && !IsSubEmitter && System.gameObject.activeInHierarchy)
                        System.Play(false);
                    resumeLoop = false;
                }
            }

            public void EnforceBlocked()
            {
                if (!Blocked || !System) return;
                var main = System.main; main.stopAction = ParticleSystemStopAction.None;
                var emission = System.emission; emission.enabled = false;
                if (renderer) renderer.forceRenderingOff = true;
                if (System.isPlaying || System.isPaused || System.particleCount > 0)
                {
                    resumeLoop |= System.isPlaying;
                    System.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }

            public void Restore()
            {
                if (!System) return;
                var main = System.main; main.stopAction = stopAction;
                if (renderer) renderer.forceRenderingOff = forceRenderingOff;
                Blocked = false;
            }
        }
    }
}
