using UnityEngine;

namespace ShurikenCascade
{
    // Geometry is resolved once for the entire window. Requested lower-panel heights survive
    // temporary window constraints; dragging starts from the resolved sizes to avoid jumps.
    internal readonly struct CascadePanelLayout
    {
        internal const float Split = 6, TimelineHeader = 44, PerformanceHeader = 22;
        internal readonly Rect Preview, Columns, Timeline, Performance;
        internal readonly Rect HorizontalGrip, TimelineGrip, PerformanceGrip;
        internal readonly float TrackHeight, StatsHeight, MinTop, MinTrack, MinStats, MinPreview, MinColumns;
        internal float TopHeight => Preview.height;

        internal CascadePanelLayout(Rect area, float previewRatio, float trackHeight, float statsHeight, bool timelineExpanded, bool performanceExpanded)
        {
            float fixedHeight = TimelineHeader + PerformanceHeader + (timelineExpanded ? Split : 0) + (performanceExpanded ? Split : 0);
            float available = Mathf.Max(0, area.height - fixedHeight);
            float minimum = 180 + (timelineExpanded ? 85 : 0) + (performanceExpanded ? 170 : 0);
            float compression = Mathf.Min(1, available / minimum);
            MinTop = 180 * compression; MinTrack = timelineExpanded ? 85 * compression : 0; MinStats = performanceExpanded ? 170 * compression : 0;
            float track = timelineExpanded ? Mathf.Max(MinTrack, trackHeight) : 0;
            float stats = performanceExpanded ? Mathf.Max(MinStats, statsHeight) : 0;
            float excess = Mathf.Max(0, track + stats + MinTop - available);
            float slack = track - MinTrack + stats - MinStats;
            if (excess > 0 && slack > 0)
            {
                float fraction = Mathf.Min(1, excess / slack);
                track -= (track - MinTrack) * fraction;
                stats -= (stats - MinStats) * fraction;
            }
            TrackHeight = track; StatsHeight = stats;
            float top = Mathf.Max(0, available - track - stats);
            float width = Mathf.Max(0, area.width - Split);
            float widthCompression = Mathf.Min(1, width / 640);
            MinPreview = 240 * widthCompression; MinColumns = 400 * widthCompression;
            float left = Mathf.Clamp(width * Mathf.Clamp01(previewRatio), MinPreview, width - MinColumns);
            Preview = new Rect(area.x, area.y, left, top);
            HorizontalGrip = new Rect(Preview.xMax, area.y, Split, top);
            Columns = new Rect(HorizontalGrip.xMax, area.y, width - left, top);
            float y = area.y + top;
            TimelineGrip = new Rect(area.x, y, area.width, timelineExpanded ? Split : 0); y += TimelineGrip.height;
            Timeline = new Rect(area.x, y, area.width, TimelineHeader + track); y = Timeline.yMax;
            PerformanceGrip = new Rect(area.x, y, area.width, performanceExpanded ? Split : 0); y += PerformanceGrip.height;
            Performance = new Rect(area.x, y, area.width, PerformanceHeader + stats);
        }

        // 0: preview/columns, 1: top/timeline, 2: timeline/performance (or top/performance when timeline is collapsed).
        internal void Resize(int divider, float delta, ref float ratio, ref float track, ref float stats)
        {
            if (divider == 0)
            {
                float width = Preview.width + Columns.width;
                ratio = Mathf.Clamp(Preview.width + delta, MinPreview, width - MinColumns) / Mathf.Max(1, width);
                return;
            }
            if (TimelineGrip.height > 0) track = TrackHeight;
            if (PerformanceGrip.height > 0) stats = StatsHeight;
            if (divider == 1)
                track = TrackHeight - Mathf.Clamp(delta, -(TopHeight - MinTop), TrackHeight - MinTrack);
            else
            {
                float above = TimelineGrip.height > 0 ? TrackHeight - MinTrack : TopHeight - MinTop;
                float movement = Mathf.Clamp(delta, -above, StatsHeight - MinStats);
                stats = StatsHeight - movement;
                if (TimelineGrip.height > 0) track = TrackHeight + movement;
            }
        }
    }
}
