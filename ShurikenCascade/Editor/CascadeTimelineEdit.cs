using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    // A drag is a proposed delta, not a stream of document changes. Commit exactly once on release.
    internal sealed class CascadeTimelineEdit
    {
        internal readonly ParticleSystem Emitter;
        internal readonly int BurstIndex; // -1 moves Start Delay; repeated markers share their source index.
        readonly CascadeSession session;
        readonly string originalJson;
        readonly float speed, minimum, maximum;
        readonly bool random;
        readonly bool relative;
        bool finished;
        internal float Delta { get; private set; } // Authored seconds, after snapping preview-time displacement.
        internal float TimelineDelta => Delta / speed;
        internal string Description => BurstIndex >= 0 ? $"Burst {BurstIndex + 1} · Time {minimum + Delta:0.###}s" :
            random ? $"Start Delay {minimum + Delta:0.###}–{maximum + Delta:0.###}s" : $"Start Delay {minimum + Delta:0.###}s";

        internal static bool CanEdit(CascadeTimelineTrack track, int burstIndex)
        {
            if (!track.Emitter || track.Nested || track.EventDriven || track.UnknownDelay || track.Speed <= 0) return false;
            return burstIndex == -1 ? !(track.Prewarm && track.Loop) :
                burstIndex >= 0 && track.Emitting && burstIndex < track.Bursts.Length;
        }

        internal static CascadeTimelineEdit Begin(CascadeSession session, CascadeTimelineTrack track, int burstIndex)
        {
            if (!session.Root || !CanEdit(track, burstIndex) || session.IsReadOnly(track.Emitter) ||
                !track.Emitter.transform.IsChildOf(session.Root.transform)) return null;
            return new CascadeTimelineEdit(session, track, burstIndex);
        }

        internal static CascadeTimelineEdit BeginRelativeBurst(CascadeSession session, CascadeTimelineTrack track, int index)
        {
            if (!CascadeTimelineAuthoring.Editable(session, track.Emitter) || !track.Emitting || index < 0 || index >= track.Bursts.Length) return null;
            return new CascadeTimelineEdit(session, track, index, true);
        }

        CascadeTimelineEdit(CascadeSession session, CascadeTimelineTrack track, int burstIndex, bool relative = false)
        {
            this.session = session; Emitter = track.Emitter; BurstIndex = burstIndex;
            originalJson = EditorJsonUtility.ToJson(Emitter);
            this.relative = relative;
            speed = relative ? 1 : Emitter.main.simulationSpeed;
            var delay = Emitter.main.startDelay;
            random = burstIndex < 0 && delay.mode == ParticleSystemCurveMode.TwoConstants;
            minimum = burstIndex >= 0 ? Emitter.emission.GetBurst(burstIndex).time : random ? delay.constantMin : delay.constant;
            maximum = random ? delay.constantMax : minimum;
        }

        internal void Move(double timelineSeconds)
        {
            if (finished || double.IsNaN(timelineSeconds) || double.IsInfinity(timelineSeconds)) return;
            // Shift both endpoints equally, including existing sub-frame offsets and reversed ranges.
            double delta = Math.Round(timelineSeconds * 60, MidpointRounding.AwayFromZero) / 60 * speed;
            Delta = (float)Math.Max(-Math.Min(minimum, maximum), Math.Min(float.MaxValue - Math.Max(minimum, maximum), delta));
        }

        internal void Cancel() { finished = true; Delta = 0; }

        internal bool Commit()
        {
            if (finished) return false;
            finished = true;
            if (Delta == 0 || !Emitter || !session.Root || !Emitter.transform.IsChildOf(session.Root.transform) ||
                session.IsReadOnly(Emitter) || EditorJsonUtility.ToJson(Emitter) != originalJson) return false;
            // Parent sub-emitter relationships can change without changing this component's JSON.
            var current = Array.Find(CascadeTimelineTrack.Build(session), t => t.Emitter == Emitter);
            if (current == null || (relative ? !current.Emitting || BurstIndex >= current.Bursts.Length : !CanEdit(current, BurstIndex))) return false;
            using (var so = new SerializedObject(Emitter))
            {
                var value = BurstIndex >= 0
                    ? so.FindProperty("EmissionModule.m_Bursts").GetArrayElementAtIndex(BurstIndex).FindPropertyRelative("time")
                    : so.FindProperty("startDelay.scalar");
                if (value == null) throw new InvalidOperationException("找不到时间轴参数的序列化属性。");
                value.floatValue = (random ? maximum : minimum) + Delta;
                if (random) so.FindProperty("startDelay.minScalar").floatValue = minimum + Delta;
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(BurstIndex >= 0 ? "Move Particle Burst" : "Move Particle Start Delay");
                try
                {
                    if (!so.ApplyModifiedProperties()) return false;
                    Undo.FlushUndoRecordObjects();
                    Undo.CollapseUndoOperations(group);
                }
                finally { Undo.IncrementCurrentGroup(); }
            }
            session.MarkDirty();
            return true;
        }
    }
}
