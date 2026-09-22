using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShurikenCascade
{
    public enum ParticleLODMethod { Automatic, ActivateAutomatic, DirectSet }

    [Serializable]
    public sealed class ParticleLODLevel
    {
        [Min(0), Tooltip("本档起始距离（Unity 世界单位）；LOD 0 始终从 0 开始。")]
        public float distance;
        [Range(0, 1)] public float emissionMultiplier = 1;
        [Range(0, 1)] public float maxParticlesMultiplier = 1;
        public bool disableEmission;
        public bool disableNoise;
        public bool disableCollision;
        public bool disableTrails;
        public bool disableLights;
    }

    /// <summary>Distance-based quality for a Shuriken hierarchy. Authored curves are never modified.</summary>
    [DisallowMultipleComponent, AddComponentMenu("Effects/Particle Distance LOD")]
    public sealed partial class ParticleDistanceLOD : MonoBehaviour
    {
        public ParticleLODMethod method;
        [Tooltip("留空使用 Camera.main；没有可用相机时保留当前档位。")]
        public Camera distanceCamera;
        [Min(0)] public float distanceCheckTime = 0.25f;
        [Min(0), Tooltip("回到更高质量档位时，需额外靠近的世界距离。")]
        public float hysteresis = 1;
        [Min(0)] public int directLOD;
        public ParticleLODLevel[] levels = {
            new ParticleLODLevel(),
            new ParticleLODLevel { distance = 20, emissionMultiplier = .5f, maxParticlesMultiplier = .5f, disableCollision = true },
            new ParticleLODLevel { distance = 50, emissionMultiplier = .25f, maxParticlesMultiplier = .25f, disableCollision = true, disableNoise = true, disableLights = true }
        };

        public int CurrentLOD { get; private set; } = -1;
        readonly List<EmitterState> states = new List<EmitterState>();
        float nextCheck;
        bool activationPending;
        bool captured;
        static readonly ParticleLODLevel OriginalLevel = new ParticleLODLevel();

        void OnEnable() { if (Application.isPlaying) ActivateLOD(); }
        void OnDisable() { RestoreOriginalSettings(); }

        /// <summary>Call before ParticleSystem.Play for pooled effects which do not toggle active state.</summary>
        public void ActivateLOD()
        {
            RestoreOriginalSettings();
            Capture();
            activationPending = true;
            RefreshQuality();
            CheckDistance();
        }

        void Update()
        {
            if (Time.unscaledTime >= nextQualityCheck) PollQuality();
            if (method == ParticleLODMethod.DirectSet)
            {
                if (CurrentLOD != Mathf.Clamp(directLOD, 0, Math.Max(0, (levels?.Length ?? 0) - 1))) SetLOD(directLOD);
                return;
            }
            if ((method == ParticleLODMethod.Automatic || activationPending) && Time.unscaledTime >= nextCheck)
                CheckDistance();
        }

        void CheckDistance()
        {
            nextCheck = Time.unscaledTime + Mathf.Max(0, distanceCheckTime);
            if (method == ParticleLODMethod.DirectSet) { SetLOD(directLOD); activationPending = false; return; }
            var camera = distanceCamera ? distanceCamera : Camera.main;
            if (!camera) return;
            float distance = Vector3.Distance(transform.position, camera.transform.position);
            int lod = SelectLOD(distance, CurrentLOD);
            if (lod != CurrentLOD) SetLOD(lod);
            activationPending = false;
        }

        public int SelectLOD(float distance, int previous = -1)
        {
            if (levels == null || levels.Length == 0) return -1;
            int selected = 0;
            // Normalize thresholds during evaluation without rewriting serialized authoring data.
            float threshold = 0;
            for (int i = 1; i < levels.Length; i++)
            {
                threshold = Mathf.Max(threshold, levels[i]?.distance ?? threshold);
                float margin = Mathf.Min(Mathf.Max(0, hysteresis), threshold * .5f);
                float boundary = threshold - (previous >= i ? margin : 0);
                if (distance >= Mathf.Max(0, boundary)) selected = i;
                else break;
            }
            return selected;
        }

        /// <summary>Applies one level without restarting simulation; Automatic may change it at the next check.</summary>
        public void SetLOD(int index)
        {
            Capture();
            CurrentLOD = levels == null || levels.Length == 0 ? -1 : Mathf.Clamp(index, 0, levels.Length - 1);
            if (CurrentLOD >= 0) directLOD = CurrentLOD;
            ApplyCurrentSettings();
        }

        void ApplyCurrentSettings()
        {
            var level = levels != null && CurrentLOD >= 0 && CurrentLOD < levels.Length
                ? levels[CurrentLOD] ?? OriginalLevel : OriginalLevel;
            foreach (var state in states) state.Apply(level);
            ApplyQuality();
        }

        void Capture()
        {
            if (captured) return;
            captured = true;
            foreach (var system in GetComponentsInChildren<ParticleSystem>(true))
                // Nested controllers own their own emitters, even while disabled.
                if (system.GetComponentInParent<ParticleDistanceLOD>(true) == this)
                    states.Add(new EmitterState(system));
            CaptureQuality();
        }

        public void RestoreOriginalSettings()
        {
            foreach (var state in states) state.Restore();
            RestoreQuality();
            states.Clear();
            captured = false;
            CurrentLOD = -1;
        }

        sealed class EmitterState
        {
            readonly ParticleSystem system;
            readonly float rateTime, rateDistance;
            readonly int maxParticles;
            readonly bool emission, noise, collision, trails, lights;
            readonly ParticleSystem.Burst[] bursts;
            readonly ParticleSystem.Burst[] workingBursts;

            public EmitterState(ParticleSystem system)
            {
                this.system = system;
                var e = system.emission;
                rateTime = e.rateOverTimeMultiplier; rateDistance = e.rateOverDistanceMultiplier;
                maxParticles = system.main.maxParticles;
                emission = e.enabled; noise = system.noise.enabled; collision = system.collision.enabled;
                trails = system.trails.enabled; lights = system.lights.enabled;
                bursts = new ParticleSystem.Burst[e.burstCount]; e.GetBursts(bursts);
                workingBursts = new ParticleSystem.Burst[bursts.Length];
            }

            public void Apply(ParticleLODLevel level)
            {
                if (!system) return;
                float scale = Mathf.Clamp01(level.emissionMultiplier);
                var e = system.emission;
                e.enabled = emission && !level.disableEmission && scale > 0;
                e.rateOverTimeMultiplier = rateTime * scale;
                e.rateOverDistanceMultiplier = rateDistance * scale;
                for (int i = 0; i < bursts.Length; i++)
                {
                    workingBursts[i] = bursts[i];
                    var count = bursts[i].count;
                    // Constants and curves use different backing values in MinMaxCurve.
                    if (count.mode == ParticleSystemCurveMode.Constant) count.constant *= scale;
                    else if (count.mode == ParticleSystemCurveMode.TwoConstants)
                    { count.constantMin *= scale; count.constantMax *= scale; }
                    else count.curveMultiplier *= scale;
                    workingBursts[i].count = count;
                }
                e.SetBursts(workingBursts);
                var main = system.main;
                main.maxParticles = Mathf.Max(0, Mathf.RoundToInt(maxParticles * Mathf.Clamp01(level.maxParticlesMultiplier)));
                var n = system.noise; n.enabled = noise && !level.disableNoise;
                var c = system.collision; c.enabled = collision && !level.disableCollision;
                var t = system.trails; t.enabled = trails && !level.disableTrails;
                var l = system.lights; l.enabled = lights && !level.disableLights;
            }

            public void Restore() { Apply(OriginalLevel); }
        }
    }
}
