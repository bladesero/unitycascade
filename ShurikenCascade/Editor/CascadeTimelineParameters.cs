using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal enum CascadeParameterRowKind { Emitter, Module, Burst, Curve, Gradient }

    internal sealed class CascadeParameterRow
    {
        internal CascadeParameterRowKind Kind;
        internal CascadeTimelineTrack Track;
        internal int Module;
        internal string Label, Path;
        internal float Y, Height = 26, Value, Range;
        internal AnimationCurve Curve;
        internal Gradient Gradient;
        internal Color Color;
        internal bool IsColor;
        internal Rect ScreenRect;
        internal Rect GraphScreenRect;
        internal Rect VisibleGraphScreenRect;
        internal CascadeLifetimeDomain Lifetime;
        internal CascadeTimelineTiming Timing;
        internal bool RandomDomain, SeparateAxes;
        internal float ViewSeconds;
        internal bool HasChildren, EmitterTime, NonTemporal;
        internal string AxisLabel, AxisUnit = "%";
        internal float AxisMin, AxisMax = 100;
    }

    // Cache the flattened tree on document/fold changes; only visible parameter rows run GUI code.
    internal sealed partial class CascadeTimelineParameters
    {
        internal List<CascadeParameterRow> Rows { get; private set; } = new List<CascadeParameterRow>();
        internal float Height { get; private set; }
        internal int DrawnRows { get; set; }
        internal readonly CascadeTimelineNativeField Native = new CascadeTimelineNativeField();
        readonly List<CascadeParameterRow> visibleGraphs = new List<CascadeParameterRow>();
        internal void BeginDraw() { if (Event.current.type == EventType.Repaint) visibleGraphs.Clear(); }
        internal void RegisterGraph(CascadeParameterRow row, Rect viewport)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (row.Kind != CascadeParameterRowKind.Curve && row.Kind != CascadeParameterRowKind.Gradient) return;
            Rect graph = row.GraphScreenRect;
            row.VisibleGraphScreenRect = Rect.MinMaxRect(Mathf.Max(graph.xMin, viewport.xMin), Mathf.Max(graph.yMin, viewport.yMin),
                Mathf.Min(graph.xMax, viewport.xMax), Mathf.Min(graph.yMax, viewport.yMax));
            visibleGraphs.Add(row);
        }
        internal void DrawNativeFields(CascadeSession session, Vector2 screenOrigin, Action changed)
        {
            bool opening = false;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && GUIUtility.hotControl == 0)
            {
                Vector2 mouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
                foreach (var row in visibleGraphs)
                {
                    if (!row.VisibleGraphScreenRect.Contains(mouse)) continue;
                    selectedRow = row; selectedKey = -1; keyFocus = true;
                    opening = Native.Bind(session, row);
                    break;
                }
            }
            Native.Draw(session, screenOrigin, opening, changed);
        }
        bool dirty = true;
        int treeRevision = -1;

        CascadeTimelineEdit burstEdit;
        CascadeParameterRow editingRow;
        int control;
        Vector2 startScreen;
        Rect graphScreen;

        bool moved;
        CascadeParameterRow selectedRow;
        int selectedKey = -1;
        bool keyFocus;
        CascadeLifetimeDomain currentLifetime;
        CascadeTimelineTiming currentTiming;
        internal bool IsEditing => burstEdit != null;
        internal void Rebuild() { dirty = true; CancelEdit(); }
        internal void CancelEdit()
        {
            burstEdit = null; editingRow = null;
            if (control != 0 && GUIUtility.hotControl == control) GUIUtility.hotControl = 0;
            control = 0;
        }

        internal static bool Expanded(CascadeTimelineState state, string key) => state.OpenEmitters.Contains(key);
        internal static void ToggleEmitter(CascadeTimelineState state, string key)
        { if (!state.OpenEmitters.Remove(key)) state.OpenEmitters.Add(key); state.TreeRevision++; }

        internal void Ensure(CascadeSession session, IReadOnlyList<CascadeTimelineTrack> tracks, CascadeTimelineState state)
        {
            if (!dirty && treeRevision == state.TreeRevision) return;
            dirty = false; treeRevision = state.TreeRevision; Rows = new List<CascadeParameterRow>(); Height = 0;
            foreach (var track in tracks)
            {
                if (!track.Emitter) continue;
                currentLifetime = CascadeLifetimeDomain.Build(track.Emitter);
                currentTiming = CascadeTimelineTiming.Build(track);
                Add(new CascadeParameterRow { Kind = CascadeParameterRowKind.Emitter, Track = track });
                if (!Expanded(state, track.Key)) continue;
                using (var so = new SerializedObject(track.Emitter))
                {
                    for (int i = 1; i < CascadeModules.All.Length; i++)
                    {
                        var module = CascadeModules.All[i];
                        var field = so.FindProperty(module.Path + ".enabled");
                        if (module.Path != "InitialModule" && (field == null || !field.boolValue)) continue;
                        var discovered = CascadeTimelineDiscovery.Collect(so, track, i);
                        Add(new CascadeParameterRow { Kind = CascadeParameterRowKind.Module, Track = track, Module = i, Label = module.Name, HasChildren = discovered.Count > 0 || module.Path == "EmissionModule",
                            SeparateAxes = track.Emitter.sizeOverLifetime.separateAxes });
                        if (state.CollapsedModules.Contains(track.Key + "/" + module.Path)) continue;
                        if (module.Path == "EmissionModule")
                        {
                            float range = track.Emitter.main.duration;
                            foreach (var burst in track.Bursts) range = Mathf.Max(range, burst.time + 0.1f);
                            Add(new CascadeParameterRow { Kind = CascadeParameterRowKind.Burst, Track = track, Module = i, Label = "Bursts · 发射器秒", Height = 104, Range = Mathf.Max(0.1f, range) });
                        }
                        foreach (var parameter in discovered) Add(parameter);
                    }
                }
            }
            if (selectedRow != null)
            {
                selectedRow = Rows.Find(r => r.Track.Emitter == selectedRow.Track.Emitter && r.Path == selectedRow.Path && r.Kind == selectedRow.Kind);
                if (selectedRow == null) { selectedKey = -1; keyFocus = false; }
            }
        }

        internal void HandleKeyboard(CascadeSession session, Action changed)
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDown && selectedRow != null && !selectedRow.ScreenRect.Contains(GUIUtility.GUIToScreenPoint(e.mousePosition))) keyFocus = false;
            if (!keyFocus || e.type != EventType.KeyDown || EditorGUIUtility.editingTextField || GUIUtility.keyboardControl != 0 || IsEditing) return;
            if (e.keyCode == KeyCode.Escape) { keyFocus = false; selectedKey = -1; e.Use(); return; }
            if (e.keyCode != KeyCode.Delete && e.keyCode != KeyCode.Backspace) return;
            e.Use(); // A focused parameter lane must never forward Delete to emitter deletion.
            if (selectedRow == null || selectedKey < 0 || !CascadeTimelineAuthoring.Editable(session, selectedRow.Track.Emitter)) return;
            bool applied = false;
            if (selectedRow.Kind == CascadeParameterRowKind.Burst)
                applied = CascadeTimelineAuthoring.DeleteBurst(session, selectedRow.Track.Emitter, selectedKey);
            selectedKey = -1;
            if (applied) changed?.Invoke();
        }

        void Add(CascadeParameterRow row)
        {
            row.Lifetime = currentLifetime;
            row.Timing = currentTiming;
            row.Y = Height; Height += row.Height; Rows.Add(row);
        }
        internal void EmitterMenu(CascadeSession session, CascadeParameterRow row, CascadeTimelineState state, Action changed)
        {
            var menu = new GenericMenu();
            bool editable = CascadeTimelineAuthoring.Editable(session, row.Track.Emitter);
            using (var so = new SerializedObject(row.Track.Emitter))
            {
                foreach (var module in CascadeModules.All)
                {
                    if (module.Path == "InitialModule") continue;
                    var enabled = so.FindProperty(module.Path + ".enabled"); if (enabled == null) continue;
                    var content = new GUIContent("添加模块/" + module.Name);
                    if (!editable || enabled.boolValue) menu.AddDisabledItem(content, enabled.boolValue);
                    else menu.AddItem(content, false, () => {
                        if (!CascadeTimelineAuthoring.SetEnabled(session, row.Track.Emitter, module.Path, true)) return;
                        if (!Expanded(state, row.Track.Key)) state.OpenEmitters.Add(row.Track.Key);
                        state.CollapsedModules.Remove(row.Track.Key + "/" + module.Path); state.TreeRevision++;
                        changed?.Invoke();
                    });
                }
            }
            menu.ShowAsContext(); Event.current.Use();
        }

        internal void DrawModule(CascadeParameterRow row, Rect rect, CascadeSession session, CascadeTimelineState state,
            Action changed, CascadeModuleClipboard clipboard, Action<ParticleSystem> select, bool allowInput)
        {
            var module = CascadeModules.All[row.Module]; string key = row.Track.Key + "/" + module.Path;
            bool hasChildren = row.HasChildren;
            if (hasChildren)
            {
                bool expanded = !state.CollapsedModules.Contains(key);
                bool next = EditorGUI.Foldout(new Rect(rect.x + 20, rect.y, 180, 24), expanded, row.Label, true);
                if (next != expanded) { if (next) state.CollapsedModules.Remove(key); else state.CollapsedModules.Add(key); state.TreeRevision++; }
            }
            else GUI.Label(new Rect(rect.x + 32, rect.y, 270, 24), row.Label, EditorStyles.miniBoldLabel);
            DrawModuleFields(row, rect, session, changed);
            if (!allowInput || Event.current.type != EventType.ContextClick || !rect.Contains(Event.current.mousePosition)) return;
            var menu = new GenericMenu();
            if (clipboard != null)
            {
                menu.AddItem(new GUIContent("复制模块"), false, () => { if (row.Track.Emitter) clipboard.Copy(row.Track.Emitter, module); });
                if (clipboard.CanPaste(session, row.Track.Emitter, module, out _))
                    menu.AddItem(new GUIContent("粘贴模块"), false, () => { if (clipboard.CanPaste(session, row.Track.Emitter, module, out _)) { clipboard.Paste(session, row.Track.Emitter, module); changed?.Invoke(); } });
                else menu.AddDisabledItem(new GUIContent("粘贴模块"));
            }
            if (module.Path != "InitialModule" && CascadeTimelineAuthoring.Editable(session, row.Track.Emitter))
                menu.AddItem(new GUIContent("禁用模块（保留参数）"), false, () => { if (CascadeTimelineAuthoring.SetEnabled(session, row.Track.Emitter, module.Path, false)) changed?.Invoke(); });
            else menu.AddDisabledItem(new GUIContent("禁用模块"));
            if (CascadeTimelineAuthoring.Editable(session, row.Track.Emitter))
                menu.AddItem(new GUIContent("重置模块"), false, () => { if (CascadeTimelineAuthoring.ResetModule(session, row.Track.Emitter, module)) changed?.Invoke(); });
            else menu.AddDisabledItem(new GUIContent("重置模块"));
            menu.ShowAsContext(); Event.current.Use();
        }

        internal void DrawParameter(CascadeParameterRow row, Rect rect, CascadeSession session, Action changed, bool allowInput)
        {
            row.ScreenRect = new Rect(GUIUtility.GUIToScreenPoint(rect.position), rect.size);
            DrawnRows++;
            GUI.Label(new Rect(rect.x + 38, rect.y + 1, 168, 24), new GUIContent(row.Label, row.Label + "\n" + row.Path), EditorStyles.miniLabel);
            Rect graph = GraphRect(row, rect, row.ViewSeconds);
            if (Event.current.type == EventType.Repaint)
                row.GraphScreenRect = new Rect(GUIUtility.GUIToScreenPoint(graph.position), graph.size);
            bool editable = CascadeTimelineAuthoring.Editable(session, row.Track.Emitter);
            EditorGUI.DrawRect(graph, new Color(0.1f, 0.1f, 0.1f));
            if (row.Kind == CascadeParameterRowKind.Burst) { DrawBursts(row, graph, session, changed, editable && allowInput); DrawBurstFields(row, rect, session, changed); return; }
            var curve = row.Curve;
            var gradient = row.Gradient;
            if (UsesGlobalTime(row))
            {
                Rect lane = new Rect(rect.x + CascadeTimelineScale.NameWidth, graph.y, Mathf.Max(1, rect.width - CascadeTimelineScale.NameWidth), graph.height);
                float step = CascadeTimelineScale.TickStep(lane.width, row.ViewSeconds);
                for (float t = 0; t <= row.ViewSeconds; t += step)
                {
                    float x = CascadeTimelineScale.X(lane, t, row.ViewSeconds);
                    GUI.Label(new Rect(x + 2, rect.y, 65, 18), t.ToString("0.##") + "s", EditorStyles.miniLabel);
                    EditorGUI.DrawRect(new Rect(x, graph.y, 1, graph.height), new Color(1, 1, 1, 0.08f));
                }
            }
            else for (int i = 0; i <= 4; i++)
            {
                float x = graph.x + graph.width * i / 4;
                if (x <= rect.xMax - 4)
                    GUI.Label(new Rect(Mathf.Min(x, rect.xMax - 70), rect.y, 70, 18), Mathf.Lerp(row.AxisMin, row.AxisMax, i / 4f).ToString("0.##") + row.AxisUnit, EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(x, graph.y, 1, graph.height), new Color(1, 1, 1, 0.08f));
            }
            float min = 0, max = 1;
            if (curve != null)
            {
                foreach (var key in curve.keys) { min = Mathf.Min(min, key.value); max = Mathf.Max(max, key.value); }

                float pad = (max - min) * 0.1f; min -= pad; max += pad;
                if (Event.current.type == EventType.Repaint)
                {
                    Handles.BeginGUI(); Color before = Handles.color; Handles.color = Color.cyan;
                    Vector3 previous = Point(graph, 0, curve.Evaluate(0), min, max);
                    for (int i = 1; i <= 96; i++)
                    { Vector3 next = Point(graph, i / 96f, curve.Evaluate(i / 96f), min, max); Handles.DrawLine(previous, next); previous = next; }
                    Handles.color = before; Handles.EndGUI();
                }
                GUI.Label(new Rect(rect.x + 42, rect.y + 26, 170, 52), $"{max:0.##} … {min:0.##}\n点击打开 Unity 曲线编辑器\n{row.AxisLabel ?? "粒子生命周期"}", EditorStyles.miniLabel);
            }
            else
            {
                Rect strip = new Rect(graph.x, graph.y + 10, graph.width, graph.height - 20);
                for (int i = 0; i < 64; i++) EditorGUI.DrawRect(new Rect(strip.x + strip.width * i / 64, strip.y, strip.width / 64 + 1, strip.height), gradient.Evaluate(i / 63f));
                GUI.Label(new Rect(rect.x + 42, rect.y + 28, 170, 48), "点击打开 Unity 渐变编辑器\n" + row.AxisLabel, EditorStyles.miniLabel);
            }
        }

        void DrawBursts(CascadeParameterRow row, Rect graph, CascadeSession session, Action changed, bool editable)
        {
            GUI.Label(new Rect(graph.x, graph.y - 20, graph.width, 20), $"0 s — {row.Range:0.###} s（相对发射器开始；选点直接编辑，右键新增 / 删除）" + (row.Track.Bursts.Length > 600 ? " · 仅显示前 600 个" : ""), EditorStyles.miniLabel);
            int hit = -1; float best = 9;
            for (int i = 0; i < Mathf.Min(600, row.Track.Bursts.Length); i++)
            {
                var burst = row.Track.Bursts[i];
                float time = burst.time + (editingRow == row && burstEdit != null && burstEdit.BurstIndex == i ? burstEdit.Delta : 0);
                Vector2 point = new Vector2(graph.x + time / row.Range * graph.width, graph.center.y);
                Key(point, burst.probability < 1 ? new Color(1, 0.65f, 0.15f) : Color.white, selectedRow == row && selectedKey == i && keyFocus);
                GUI.Label(new Rect(point.x + 6, graph.y, 90, 18), $"{i + 1}: {time:0.##}s", EditorStyles.miniLabel);
                float distance = Vector2.Distance(point, Event.current.mousePosition); if (distance < best) { hit = i; best = distance; }
            }
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (!editable || !graph.Contains(Event.current.mousePosition)) return;
            if (Event.current.type == EventType.ContextClick)
            {
                string expected = EditorJsonUtility.ToJson(row.Track.Emitter);
                float time = (Event.current.mousePosition.x - graph.x) / graph.width * row.Range;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("新增 Burst"), false, () => { if (CascadeTimelineAuthoring.AddBurst(session, row.Track.Emitter, time)) changed?.Invoke(); });
                if (hit >= 0)
                {
                    int index = hit;
                    menu.AddItem(new GUIContent("删除 Burst " + (index + 1)), false, () => { if (row.Track.Emitter && EditorJsonUtility.ToJson(row.Track.Emitter) == expected && CascadeTimelineAuthoring.DeleteBurst(session, row.Track.Emitter, index)) changed?.Invoke(); });
                }
                menu.ShowAsContext(); Event.current.Use();
            }
            else if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && GUIUtility.hotControl == 0)
            {
                selectedRow = row; selectedKey = hit; keyFocus = true; GUI.FocusControl(null);
                if (hit < 0) { Event.current.Use(); return; }
                burstEdit = CascadeTimelineEdit.BeginRelativeBurst(session, row.Track, hit);
                if (burstEdit == null) return;
                selectedRow = row; selectedKey = hit; keyFocus = true;
                editingRow = row; StartDrag(id, graph); Event.current.Use();
            }
        }

        void StartDrag(int id, Rect graph)
        {
            control = id; GUIUtility.hotControl = id; GUI.FocusControl(null); moved = false;
            startScreen = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
            graphScreen = new Rect(GUIUtility.GUIToScreenPoint(graph.position), graph.size);
        }

        internal void HandleDrag(Action changed)
        {
            if (!IsEditing) return;
            Event e = Event.current;
            if (GUIUtility.hotControl != control || !editingRow.Track.Emitter) { CancelEdit(); return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CancelEdit(); e.Use(); return; }
            if (e.type == EventType.ScrollWheel) { e.Use(); return; }
            if (e.type != EventType.MouseDrag && !(e.type == EventType.MouseUp && e.button == 0)) return;
            Vector2 delta = GUIUtility.GUIToScreenPoint(e.mousePosition) - startScreen;
            moved |= delta.sqrMagnitude > 9;
            // Release may be outside the graph; only movement events modify the proposal.
            if (moved && e.type == EventType.MouseDrag)
            {
                burstEdit.Move(delta.x / graphScreen.width * editingRow.Range);
            }
            if (e.type == EventType.MouseUp)
            {
                var burst = burstEdit;

                burstEdit = null; editingRow = null; GUIUtility.hotControl = 0; control = 0;
                e.Use(); if (moved && burst.Commit()) changed?.Invoke();
            }
            else e.Use();
        }

        static Vector2 Point(Rect graph, float time, float value, float min, float max) => new Vector2(graph.x + time * graph.width, graph.yMax - Mathf.Clamp01((value - min) / (max - min)) * graph.height);
        internal static Rect GraphRect(CascadeParameterRow row, Rect rect, float endSeconds)
        {
            float width = Mathf.Max(1, rect.width - CascadeTimelineScale.NameWidth);
            float x = rect.x + CascadeTimelineScale.NameWidth;
            if (UsesGlobalTime(row))
            {
                x += width * DisplayOrigin(row) / Mathf.Max(0.01f, endSeconds);
                width = Mathf.Max(0.001f, width * DisplaySpan(row) / Mathf.Max(0.01f, endSeconds));
            }
            return new Rect(x, rect.y + 20, width, Mathf.Max(1, row.Height - (row.Kind == CascadeParameterRowKind.Burst ? 72 : 27)));
        }
        static void Key(Vector2 point, Color color, bool selected = false)
        {
            EditorGUI.DrawRect(new Rect(point.x - 4, point.y - 4, 9, 9), selected ? Color.yellow : Color.black);
            EditorGUI.DrawRect(new Rect(point.x - 3, point.y - 3, 7, 7), color);
        }
    }
}
