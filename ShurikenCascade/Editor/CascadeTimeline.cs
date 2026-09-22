using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    [Serializable]
    internal sealed class CascadeTimelineState
    {
        public bool Expanded = true, Loop;
        public float Height = 170;
        public int EndFrame = 600, StartFrame, StopFrame = 600;
        public Vector2 Scroll;
        public List<string> OpenEmitters = new List<string>(), CollapsedModules = new List<string>();
        [NonSerialized] internal int TreeRevision;
        internal void ResetRange() { EndFrame = StopFrame = 600; StartFrame = 0; Loop = false; Scroll = Vector2.zero; OpenEmitters.Clear(); CollapsedModules.Clear(); TreeRevision++; }
        internal void Apply(CascadePreview preview)
        {
            EndFrame = Mathf.Clamp(EndFrame, 60, 36000);
            StartFrame = Mathf.Clamp(StartFrame, 0, EndFrame - 1);
            StopFrame = Mathf.Clamp(StopFrame, StartFrame + 1, EndFrame);
            preview.SetRange(EndFrame, Loop, StartFrame, StopFrame);
        }
    }

    internal sealed class CascadeTimelineTrack
    {
        internal ParticleSystem Emitter;
        internal string Key, Label;
        internal bool EventDriven, Nested, Active, Emitting, Loop, UncertainDelay, UnknownDelay, Prewarm;
        internal float DelayMin, DelayMax, Duration, Speed;
        internal ParticleSystem.Burst[] Bursts;

        internal static CascadeTimelineTrack[] Build(CascadeSession session)
        {
            var systems = session.Emitters;
            var children = new HashSet<ParticleSystem>();
            foreach (var p in systems)
            {
                var sub = p.subEmitters;
                for (int i = 0; sub.enabled && i < sub.subEmittersCount; i++)
                    if (sub.GetSubEmitterSystem(i)) children.Add(sub.GetSubEmitterSystem(i));
            }
            var result = new CascadeTimelineTrack[systems.Length];
            for (int i = 0; i < systems.Length; i++)
            {
                var p = systems[i]; var main = p.main; var emission = p.emission;
                var delay = main.startDelay;
                bool random = delay.mode == ParticleSystemCurveMode.TwoConstants;
                string key = CascadeSession.Key(p.transform, session.Root.transform);
                var bursts = new ParticleSystem.Burst[emission.burstCount]; emission.GetBursts(bursts);
                float speed = main.simulationSpeed;
                result[i] = new CascadeTimelineTrack {
                    Emitter = p, Key = key, Label = p.name + " [" + key + "]", EventDriven = children.Contains(p),
                    Nested = session.IsReadOnly(p), Active = session.IsActiveInDocument(p.transform), Emitting = emission.enabled,
                    Loop = main.loop, Prewarm = main.prewarm, Speed = speed, UncertainDelay = random,
                    UnknownDelay = !(main.prewarm && main.loop) && delay.mode != ParticleSystemCurveMode.Constant && !random,
                    DelayMin = speed > 0 && !(main.prewarm && main.loop) ? Mathf.Max(0, random ? Mathf.Min(delay.constantMin, delay.constantMax) : delay.constant) / speed : 0,
                    DelayMax = speed > 0 && !(main.prewarm && main.loop) ? Mathf.Max(0, random ? Mathf.Max(delay.constantMin, delay.constantMax) : delay.constant) / speed : 0,
                    Duration = speed > 0 ? main.duration / speed : 0, Bursts = bursts
                };
            }
            return result;
        }
    }

    internal sealed class CascadeTimeline
    {
        CascadeTimelineTrack[] tracks = Array.Empty<CascadeTimelineTrack>();
        CascadeTimelineEdit edit;
        int editControl;
        float dragScreenX, secondsPerPixel;
        bool moved;
        readonly CascadeTimelineParameters parameters = new CascadeTimelineParameters();
        internal CascadeTimelineParameters Parameters => parameters;
        internal bool IsEditing => edit != null || parameters.IsEditing;
        internal int EditingBurstIndex => edit?.BurstIndex ?? -2;
        // Screen-space lane bounds also allow UI regression tests to exercise the actual hit targets.
        internal Rect FirstLaneScreenRect { get; private set; }
        internal void Rebuild(CascadeSession session)
        {
            CancelEdit();
            tracks = session.Root ? CascadeTimelineTrack.Build(session) : Array.Empty<CascadeTimelineTrack>();
            parameters.Rebuild();
        }
        internal IReadOnlyList<CascadeTimelineTrack> Tracks => tracks;
        const float NameWidth = CascadeTimelineScale.NameWidth;

        internal void CancelEdit()
        {
            parameters.CancelEdit();
            edit?.Cancel(); edit = null;
            if (editControl != 0 && GUIUtility.hotControl == editControl) GUIUtility.hotControl = 0;
            editControl = 0;
        }

        // Called before native Inspectors, in window coordinates, so release outside the lane still commits.
        internal void HandleDrag(Action changed)
        {
            if (parameters.IsEditing) { parameters.HandleDrag(changed); return; }
            if (edit == null) return;
            Event e = Event.current;
            if (!edit.Emitter || GUIUtility.hotControl != editControl) { CancelEdit(); return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            { CancelEdit(); e.Use(); return; }
            if (e.type == EventType.ScrollWheel) { e.Use(); return; }
            if (e.type != EventType.MouseDrag && !(e.type == EventType.MouseUp && e.button == 0)) return;
            float pixels = GUIUtility.GUIToScreenPoint(e.mousePosition).x - dragScreenX;
            moved |= Mathf.Abs(pixels) >= 3;
            if (moved) edit.Move(pixels * secondsPerPixel);
            if (e.type == EventType.MouseUp)
            {
                var pending = edit; edit = null;
                GUIUtility.hotControl = 0; editControl = 0;
                e.Use();
                if (moved && pending.Commit()) changed?.Invoke(); else pending.Cancel();
            }
            else e.Use();
            GUI.changed = true;
        }

        internal void Draw(CascadeTimelineState state, CascadeSession session, CascadePreview preview, ref ParticleSystem selected, float trackHeight, Action<ParticleSystem> selectEmitter = null, Func<ParticleSystem, bool> isSelected = null, Action changed = null, CascadeModuleClipboard clipboard = null)
        {
            parameters.BeginDraw();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                state.Expanded = GUILayout.Toggle(state.Expanded, "时间轴", EditorStyles.toolbarButton, GUILayout.Width(60));
                if (GUILayout.Button("|◀", EditorStyles.toolbarButton, GUILayout.Width(30))) preview.RequestSeekFrame(0);
                if (GUILayout.Button("◀ 帧", EditorStyles.toolbarButton, GUILayout.Width(42))) preview.StepFrames(-1);
                if (GUILayout.Button("帧 ▶", EditorStyles.toolbarButton, GUILayout.Width(42))) preview.StepFrames(1);
                GUILayout.Label("时间", GUILayout.Width(28));
                EditorGUI.BeginChangeCheck();
                double seconds = EditorGUILayout.DelayedDoubleField(GUIContent.none, (preview.IsSeeking ? preview.SeekTargetFrame : preview.CurrentFrame) / 60.0, GUILayout.Width(65));
                if (EditorGUI.EndChangeCheck()) preview.RequestSeek(seconds);
                GUILayout.Label($"s · {preview.CurrentFrame}f / 60 FPS", EditorStyles.miniLabel, GUILayout.Width(125));
                GUILayout.FlexibleSpace();
                GUILayout.Label("范围 0–", GUILayout.Width(55));
                EditorGUI.BeginChangeCheck();
                double end = EditorGUILayout.DelayedDoubleField(GUIContent.none, state.EndFrame / 60.0, GUILayout.Width(55));
                if (EditorGUI.EndChangeCheck())
                {
                    bool full = state.StopFrame == state.EndFrame;
                    state.EndFrame = Mathf.Clamp(CascadePreview.ToFrame(end), 60, 36000);
                    if (full) state.StopFrame = state.EndFrame;
                }
                GUILayout.Label("s", GUILayout.Width(10));
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                state.Loop = GUILayout.Toggle(state.Loop, "区间循环", EditorStyles.toolbarButton, GUILayout.Width(70));
                GUILayout.Label("A", GUILayout.Width(12));
                state.StartFrame = CascadePreview.ToFrame(EditorGUILayout.DelayedDoubleField(GUIContent.none, state.StartFrame / 60.0, GUILayout.Width(55)));
                GUILayout.Label("B", GUILayout.Width(12));
                state.StopFrame = CascadePreview.ToFrame(EditorGUILayout.DelayedDoubleField(GUIContent.none, state.StopFrame / 60.0, GUILayout.Width(55)));
                GUILayout.Label("s", GUILayout.Width(10));
                GUILayout.FlexibleSpace();
                if (edit != null) GUILayout.Label(edit.Description + " · 松手提交 / Esc 取消", EditorStyles.miniLabel);
                else if (preview.IsSeeking)
                {
                    Rect progress = GUILayoutUtility.GetRect(200, 18);
                    EditorGUI.ProgressBar(progress, preview.SeekProgress, $"定位 {preview.Time:0.00} → {preview.SeekTargetFrame / 60f:0.00}s");
                    if (GUILayout.Button("取消", EditorStyles.toolbarButton, GUILayout.Width(42))) preview.CancelSeek();
                }
                else GUILayout.Label("拖区间改 Delay · 拖标记改 Burst · 标尺/空白处定位", EditorStyles.miniLabel);
            }
            state.Apply(preview);
            if (!state.Expanded) return;
            float height = Mathf.Max(1, trackHeight);
            Rect area = GUILayoutUtility.GetRect(100, height, GUILayout.ExpandWidth(true));
            float endSeconds = state.EndFrame / 60f;
            Rect ruler = new Rect(area.x + NameWidth, area.y, Mathf.Max(1, area.width - NameWidth - 16), 22);
            EditorGUI.DrawRect(area, new Color(0.13f, 0.13f, 0.13f));
            GUI.Label(new Rect(area.x + 5, area.y, NameWidth - 5, 22), "发射器 / 状态", EditorStyles.miniBoldLabel);
            DrawRuler(ruler, endSeconds, state, preview);
            Scrub(ruler, preview, endSeconds);
            Rect viewport = new Rect(area.x, area.y + 22, area.width, area.height - 22);
            parameters.Ensure(session, tracks, state);
            var rows = parameters.Rows;
            Rect content = new Rect(0, 0, Mathf.Max(1, viewport.width - 16), parameters.Height);
            bool pointerInViewport = viewport.Contains(Event.current.mousePosition);
            state.Scroll = GUI.BeginScrollView(viewport, state.Scroll, content);
            parameters.DrawnRows = 0;
            try
            {
            for (int i = 0; i < rows.Count; i++)
            {
                var parameter = rows[i];
                var track = parameter.Track;
                if (!track.Emitter) continue; // Asset reload/Undo may replace the document before the next update.
                Rect row = new Rect(0, parameter.Y, content.width, parameter.Height - 1);
                if (row.yMax < state.Scroll.y || row.y > state.Scroll.y + viewport.height) continue;
                parameter.ScreenRect = new Rect(GUIUtility.GUIToScreenPoint(row.position), row.size);
                if (parameter.Kind == CascadeParameterRowKind.Module)
                {
                    EditorGUI.DrawRect(row, new Color(0.18f, 0.2f, 0.22f));
                    parameters.DrawModule(parameter, row, session, state, changed, clipboard, selectEmitter, pointerInViewport);
                    continue;
                }
                if (parameter.Kind != CascadeParameterRowKind.Emitter)
                {
                    parameter.ViewSeconds = endSeconds;
                    parameters.DrawParameter(parameter, row, session, changed, pointerInViewport);
                    parameters.RegisterGraph(parameter, new Rect(GUIUtility.GUIToScreenPoint(new Vector2(state.Scroll.x, state.Scroll.y)), new Vector2(content.width, viewport.height)));
                    if (CascadeTimelineParameters.UsesGlobalTime(parameter))
                    {
                        Rect graph = CascadeTimelineParameters.GraphRect(parameter, row, endSeconds);
                        Rect parameterLane = new Rect(NameWidth, graph.y, Mathf.Max(1, content.width - NameWidth), graph.height);
                        DrawCursor(parameterLane, preview.Time, endSeconds, Color.white);
                        if (preview.IsSeeking) DrawCursor(parameterLane, preview.SeekTargetFrame / 60f, endSeconds, Color.yellow);
                    }
                    continue;
                }
                if (isSelected != null ? isSelected(track.Emitter) : track.Emitter == selected) EditorGUI.DrawRect(row, new Color(0.2f, 0.35f, 0.48f));
                bool hidden = preview.Hidden.Contains(track.Key) || (preview.Solo != null && preview.Solo != track.Key);
                string status = (track.Nested ? "嵌套 " : "") + (!track.Active || !track.Emitting ? "禁用 " : "") + (hidden ? "隐藏 " : preview.Solo == track.Key ? "独显 " : "");
                bool open = CascadeTimelineParameters.Expanded(state, track.Key);
                if (EditorGUI.Foldout(new Rect(3, row.y + 3, 17, 20), open, GUIContent.none) != open)
                    CascadeTimelineParameters.ToggleEmitter(state, track.Key);
                if (GUI.Button(new Rect(21, row.y, NameWidth - 24, 25), new GUIContent(track.Label + " " + status, session.DisplayPath(track.Emitter.transform)), EditorStyles.miniLabel))
                { if (selectEmitter != null) selectEmitter(track.Emitter); else selected = track.Emitter; }
                if (pointerInViewport && Event.current.type == EventType.ContextClick && row.Contains(Event.current.mousePosition))
                    parameters.EmitterMenu(session, parameter, state, changed);
                Rect lane = new Rect(NameWidth, row.y + 2, Mathf.Max(1, content.width - NameWidth), 21);
                if (i == 0) FirstLaneScreenRect = new Rect(GUIUtility.GUIToScreenPoint(lane.position), lane.size);
                int control = GUIUtility.GetControlID(FocusType.Passive);
                int hit = DrawTrack(track, lane, endSeconds, hidden);
                if (!pointerInViewport) hit = -2;
                if (edit == null && hit != -2 && CascadeTimelineEdit.CanEdit(track, hit))
                {
                    EditorGUIUtility.AddCursorRect(lane, MouseCursor.SlideArrow);
                    Event input = Event.current;
                    if (input.type == EventType.MouseDown && input.button == 0 && GUIUtility.hotControl == 0)
                    {
                        edit = CascadeTimelineEdit.Begin(session, track, hit);
                        if (edit != null)
                        {
                            dragScreenX = GUIUtility.GUIToScreenPoint(input.mousePosition).x;
                            secondsPerPixel = endSeconds / lane.width; moved = false;
                            editControl = control; GUIUtility.hotControl = control;
                            GUI.FocusControl(null);
                            if (selectEmitter != null) selectEmitter(track.Emitter); else selected = track.Emitter;
                            input.Use();
                        }
                    }
                }
                DrawCursor(lane, preview.Time, endSeconds, Color.white);
                if (preview.IsSeeking) DrawCursor(lane, preview.SeekTargetFrame / 60f, endSeconds, Color.yellow);
                Scrub(lane, preview, endSeconds, pointerInViewport);
            }
            }
            finally { GUI.EndScrollView(); }

        }

        static void DrawRuler(Rect ruler, float end, CascadeTimelineState state, CascadePreview preview)
        {
            float step = CascadeTimelineScale.TickStep(ruler.width, end);
            for (float t = 0; t <= end; t += step)
            {
                float x = CascadeTimelineScale.X(ruler, t, end);
                GUI.Label(new Rect(x + 2, ruler.y, 65, 18), t.ToString("0.##") + "s", EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(x, ruler.yMax - 4, 1, 4), Color.gray);
            }
            float a = ruler.x + ruler.width * state.StartFrame / state.EndFrame;
            float b = ruler.x + ruler.width * state.StopFrame / state.EndFrame;
            EditorGUI.DrawRect(new Rect(a, ruler.yMax - 3, b - a, 3), state.Loop ? Color.cyan : Color.gray);
            DrawCursor(ruler, preview.Time, end, Color.white);
            if (preview.IsSeeking) DrawCursor(ruler, preview.SeekTargetFrame / 60f, end, Color.yellow);
        }

        static void DrawCursor(Rect rect, float seconds, float end, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x + Mathf.Clamp01(seconds / end) * rect.width, rect.y, 1, rect.height), color);
        }

        static void Scrub(Rect rect, CascadePreview preview, float end, bool canStart = true)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            if (canStart && e.type == EventType.MouseDown && e.button == 0 && GUIUtility.hotControl == 0 && rect.Contains(e.mousePosition)) { GUIUtility.hotControl = control; preview.RequestSeek(Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width) * end); e.Use(); }
            else if (GUIUtility.hotControl == control && e.type == EventType.MouseDrag) { preview.RequestSeek(Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width) * end); e.Use(); }
            else if (GUIUtility.hotControl == control && e.type == EventType.MouseUp) { GUIUtility.hotControl = 0; e.Use(); }
        }

        int DrawTrack(CascadeTimelineTrack track, Rect lane, float end, bool hidden)
        {
            if (track.EventDriven || track.Speed <= 0 || track.UnknownDelay)
            {
                GUI.Label(lane, track.EventDriven ? "由父事件触发 · Burst 时间相对父事件" : track.Speed <= 0 ? "Simulation Speed = 0 · 冻结" : "曲线延迟 · 无固定绝对起点", EditorStyles.miniLabel);
                return -2;
            }
            bool editing = edit != null && edit.Emitter == track.Emitter;
            float shift = editing && edit.BurstIndex < 0 ? edit.TimelineDelta : 0;
            int hit = -2;
            float closestMarker = float.MaxValue;
            Vector2 mouse = Event.current.mousePosition;
            Color color = hidden || !track.Active || !track.Emitting ? new Color(0.35f, 0.35f, 0.35f) : new Color(0.2f, 0.55f, 0.7f);
            if (editing && edit.BurstIndex < 0) color = new Color(0.25f, 0.8f, 0.8f);
            int cycles = track.Loop && track.Duration > 0 ? Mathf.CeilToInt(end / track.Duration) + 1 : 1;
            // Dense loops and bursts are deliberately bounded by screen resolution, not effect duration.
            if (cycles > 300)
            {
                Rect dense = Band(lane, track.DelayMin + shift, end, end, color, "拖动调整 Start Delay · 密集循环（周期 " + track.Duration.ToString("0.####") + "s），Burst 标记已合并");
                return lane.Contains(mouse) && dense.Contains(mouse) ? -1 : -2;
            }
            int markers = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                float offset = cycle * track.Duration;
                float begin = track.DelayMin + shift + offset, finish = track.DelayMax + shift + offset + track.Duration;
                if (begin > end) break;
                Rect band = Band(lane, begin, finish, end, color, $"拖动调整 Start Delay · {track.DelayMin + shift:0.###}–{track.DelayMax + shift:0.###}s · Duration {track.Duration:0.###}s（非粒子存活时长）" + (track.Prewarm && track.Loop ? " · Prewarm：Start Delay 不生效，不可拖动" : track.Nested ? " · 嵌套只读" : ""));
                if (hit == -2 && lane.Contains(mouse) && band.Contains(mouse)) hit = -1;
                if (track.UncertainDelay) Band(lane, begin, track.DelayMax + shift + offset, end, new Color(0.65f, 0.45f, 0.2f), "随机 Start Delay 范围，拖动整体平移（非确定时刻）");
                if (!track.Emitting) continue;
                for (int burstIndex = 0; burstIndex < track.Bursts.Length; burstIndex++)
                {
                    var burst = track.Bursts[burstIndex];
                    bool editingBurst = editing && edit.BurstIndex == burstIndex;
                    float first = burst.time / track.Speed + (editingBurst ? edit.TimelineDelta : 0);
                    float interval = Mathf.Max(0.0001f, burst.repeatInterval / track.Speed);
                    int repeats = burst.cycleCount == 0 ? Mathf.CeilToInt(Mathf.Max(0, track.Duration - first) / interval) : burst.cycleCount;
                    for (int repeat = 0; repeat < repeats && markers < 600; repeat++, markers++)
                    {
                        float local = first + repeat * interval;
                        if (local > track.Duration || begin + local > end) break;
                        float t = begin + local;
                        Color markerColor = editingBurst ? Color.cyan : burst.probability < 1 ? new Color(1, 0.65f, 0.15f) : Color.white;
                        float markerX = lane.x + t / end * lane.width;
                        Rect marker = new Rect(markerX, lane.y + 3, Mathf.Min(lane.xMax - markerX, Mathf.Max(burst.probability < 1 ? 12 : 2, (track.DelayMax - track.DelayMin) / end * lane.width)), lane.height - 6);
                        Rect target = new Rect(marker.x - 4, lane.y, Mathf.Max(10, marker.width + 8), lane.height);
                        float distance = Mathf.Abs(mouse.x - markerX);
                        if (lane.Contains(mouse) && target.Contains(mouse) && distance < closestMarker)
                        { hit = burstIndex; closestMarker = distance; }
                        string tooltip = $"Burst {burstIndex + 1} · {t:0.###}s · Time {first * track.Speed:0.###}s · Probability {burst.probability:P0}\n拖动修改此 Burst 的原始 Time，所有循环和重复标记同步" + (track.Nested ? " · 嵌套只读" : "");
                        if (burst.probability < 1)
                        {
                            Color previous = GUI.contentColor; GUI.contentColor = markerColor;
                            GUI.Label(marker, new GUIContent("◇", tooltip), EditorStyles.miniLabel);
                            GUI.contentColor = previous;
                        }
                        else { EditorGUI.DrawRect(marker, markerColor); GUI.Label(marker, new GUIContent("", tooltip)); }
                    }
                }
            }
            return hit;
        }

        static Rect Band(Rect lane, float start, float finish, float end, Color color, string tooltip)
        {
            float left = lane.x + Mathf.Clamp01(start / end) * lane.width;
            float right = lane.x + Mathf.Clamp01(finish / end) * lane.width;
            Rect band = new Rect(left, lane.y + 4, Mathf.Max(0, right - left - 1), lane.height - 8);
            EditorGUI.DrawRect(band, color);
            GUI.Label(band, new GUIContent("", tooltip));
            return band;
        }
    }
}
