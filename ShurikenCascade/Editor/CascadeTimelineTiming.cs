using UnityEngine;

namespace ShurikenCascade
{
    // Authoring reference for the first emission, not a claim that all particles share one birth.
    internal sealed class CascadeTimelineTiming
    {
        internal float Origin;
        internal bool Absolute;
        internal string Note;

        internal static CascadeTimelineTiming Build(CascadeTimelineTrack track)
        {
            var result = new CascadeTimelineTiming();
            if (track.EventDriven) { result.Note = "父事件触发 · 相对生命周期"; return result; }
            if (track.Speed <= 0) { result.Note = "模拟冻结 · 相对生命周期"; return result; }
            if (track.UnknownDelay) { result.Note = "起点不确定 · 相对生命周期"; return result; }
            result.Absolute = true; result.Origin = track.DelayMin;
            result.Note = "首轮发射参考";
            var emission = track.Emitter.emission;
            if (track.Emitting && !CanEmit(emission.rateOverTime) && !CanEmit(emission.rateOverDistance))
            {
                float first = float.PositiveInfinity;
                foreach (var burst in track.Bursts)
                    if (burst.time >= 0 && burst.time <= track.Duration * track.Speed && burst.probability > 0 && CanEmit(burst.count))
                        first = Mathf.Min(first, burst.time);
                if (!float.IsInfinity(first))
                { result.Origin += first / track.Speed; result.Note = "首个 Burst 参考"; }
            }
            if (track.Prewarm && track.Loop) result.Note = "预热首轮配置参考";
            return result;
        }

        static bool CanEmit(ParticleSystem.MinMaxCurve curve)
        {
            if (curve.mode == ParticleSystemCurveMode.Constant) return curve.constant > 0;
            if (curve.mode == ParticleSystemCurveMode.TwoConstants) return Mathf.Max(curve.constantMin, curve.constantMax) > 0;
            for (int i = 0; i <= 128; i++)
                if ((curve.curveMax != null && curve.curveMax.Evaluate(i / 128f) * curve.curveMultiplier > 0) ||
                    (curve.mode == ParticleSystemCurveMode.TwoCurves && curve.curveMin != null && curve.curveMin.Evaluate(i / 128f) * curve.curveMultiplier > 0)) return true;
            return false;
        }
    }

    internal static class CascadeTimelineScale
    {
        internal const float NameWidth = 210;
        internal static float X(Rect lane, float seconds, float end) => lane.x + lane.width * seconds / Mathf.Max(0.01f, end);
        internal static float TickStep(float width, float end)
        {
            float step = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(end / Mathf.Max(2, width / 65))));
            while (width * step / end < 50) step *= 2;
            return step;
        }
    }
}
