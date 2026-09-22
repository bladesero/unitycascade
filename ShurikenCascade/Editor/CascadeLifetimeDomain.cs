using UnityEngine;

namespace ShurikenCascade
{
    // A representative particle age, never an absolute birth/death schedule for all particles.
    internal sealed class CascadeLifetimeDomain
    {
        internal float Minimum, Maximum, Reference, Seconds;
        internal bool Variable, Sampled, Normalized, Frozen;
        internal static CascadeLifetimeDomain Build(ParticleSystem p, float reference = -1)
        {
            var life = p.main.startLifetime;
            var result = new CascadeLifetimeDomain();
            if (life.mode == ParticleSystemCurveMode.Constant) result.Minimum = result.Maximum = life.constant;
            else if (life.mode == ParticleSystemCurveMode.TwoConstants)
            { result.Minimum = Mathf.Min(life.constantMin, life.constantMax); result.Maximum = Mathf.Max(life.constantMin, life.constantMax); result.Variable = true; }
            else
            {
                result.Minimum = float.PositiveInfinity; result.Maximum = 0; result.Sampled = result.Variable = true;
                for (int i = 0; i <= 128; i++)
                {
                    float a = life.curveMax.Evaluate(i / 128f) * life.curveMultiplier;
                    float b = life.mode == ParticleSystemCurveMode.TwoCurves ? life.curveMin.Evaluate(i / 128f) * life.curveMultiplier : a;
                    result.Minimum = Mathf.Min(result.Minimum, a, b); result.Maximum = Mathf.Max(result.Maximum, a, b);
                }
            }
            result.Minimum = Mathf.Max(0, result.Minimum); result.Maximum = Mathf.Max(result.Minimum, result.Maximum);
            result.Reference = reference >= 0 && result.Variable ? Mathf.Clamp(reference, result.Minimum, result.Maximum) : result.Maximum;
            result.Frozen = p.main.simulationSpeed <= 0;
            result.Seconds = result.Reference / (result.Frozen ? 1 : p.main.simulationSpeed);
            result.Normalized = result.Seconds <= 0 || float.IsNaN(result.Seconds) || float.IsInfinity(result.Seconds);
            if (result.Normalized) result.Seconds = 1;
            return result;
        }
        internal string Label => Normalized ? "无有限寿命 · 归一化 %" :
            $"粒子年龄 0–{Seconds:0.###}s（出生=0）" + (Frozen ? " · 冻结" : "");
    }
}
