using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal sealed partial class CascadeTimelineParameters
    {
        void DrawModuleFields(CascadeParameterRow row, Rect rect, CascadeSession session, Action changed)
        {
            string module = CascadeModules.All[row.Module].Path;
            Rect field = new Rect(rect.x + 218, rect.y + 2, Mathf.Max(1, rect.width - 232), 20);
            if (module == "InitialModule") GUI.Label(field, row.Timing.Absolute && !row.Lifetime.Normalized ?
                $"全局 {row.Timing.Origin:0.###}–{row.Timing.Origin + row.Lifetime.Seconds:0.###}s · {row.Timing.Note}" : row.Lifetime.Normalized ? row.Lifetime.Label : row.Timing.Note, EditorStyles.miniLabel);
            if (module != "SizeModule") return;
            using (new EditorGUI.DisabledScope(!CascadeTimelineAuthoring.Editable(session, row.Track.Emitter)))
            {
                bool separate = EditorGUI.ToggleLeft(new Rect(field.x, field.y, 160, 20), "Separate Axes (X/Y/Z)", row.SeparateAxes);
                if (separate != row.SeparateAxes && CascadeTimelineAuthoring.Apply(session, row.Track.Emitter, "Separate Particle Size Axes", so =>
                    so.FindProperty("SizeModule.separateAxes").boolValue = separate)) changed?.Invoke();
            }
        }

        internal static bool UsesGlobalTime(CascadeParameterRow row) =>
            (row.Kind == CascadeParameterRowKind.Curve || row.Kind == CascadeParameterRowKind.Gradient) &&
            !row.RandomDomain && !row.NonTemporal && (row.EmitterTime ? row.Track.Duration > 0 : !row.Lifetime.Normalized) && (row.Timing == null || row.Timing.Absolute);
        internal static float DisplaySpan(CascadeParameterRow row) => UsesGlobalTime(row) ? row.EmitterTime ? row.Track.Duration : row.Lifetime.Seconds : Mathf.Max(0.0001f, row.AxisMax - row.AxisMin);
        internal static float DisplayOrigin(CascadeParameterRow row) => UsesGlobalTime(row) ? row.EmitterTime ? row.Track.DelayMin : row.Timing?.Origin ?? 0 : row.AxisMin;
        internal static float ToDisplayTime(CascadeParameterRow row, float normalized) => DisplayOrigin(row) + normalized * DisplaySpan(row);
        internal static float FromDisplayTime(CascadeParameterRow row, float displayed) => (displayed - DisplayOrigin(row)) / DisplaySpan(row);

        void DrawBurstFields(CascadeParameterRow row, Rect rect, CascadeSession session, Action changed)
        {
            Rect line = new Rect(rect.x + 218, rect.y + row.Height - 48, Mathf.Max(1, rect.width - 232), 44);
            if (selectedRow != row || selectedKey < 0 || selectedKey >= row.Track.Bursts.Length)
            { GUI.Label(line, "选择 Burst，直接编辑 Time / Count / Cycles / Interval / Probability", EditorStyles.miniLabel); return; }
            using (var so = new SerializedObject(row.Track.Emitter))
            using (new EditorGUI.DisabledScope(IsEditing || !CascadeTimelineAuthoring.Editable(session, row.Track.Emitter)))
            {
                var bursts = so.FindProperty("EmissionModule.m_Bursts");
                if (selectedKey >= bursts.arraySize) return;
                var burst = bursts.GetArrayElementAtIndex(selectedKey); var count = burst.FindPropertyRelative("countCurve");
                Rect Cell(int i, int y = 0) => new Rect(line.x + line.width * i / 4, line.y + y * 22, line.width / 4 - 5, 20);
                float previous = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 58;
                try
                {
                    EditorGUI.BeginChangeCheck();
                    float time = EditorGUI.DelayedFloatField(Cell(0), "Time", burst.FindPropertyRelative("time").floatValue);
                    int mode = EditorGUI.Popup(Cell(1), "Count", count.FindPropertyRelative("minMaxState").intValue, new[] { "Constant", "Curve", "Two Curves", "Two Constants" });
                    if (mode == 0 || mode == 3)
                    {
                        if (mode == 3) count.FindPropertyRelative("minScalar").floatValue = Mathf.Max(0, EditorGUI.DelayedFloatField(Cell(2), "Min", count.FindPropertyRelative("minScalar").floatValue));
                        count.FindPropertyRelative("scalar").floatValue = Mathf.Max(0, EditorGUI.DelayedFloatField(Cell(3), mode == 3 ? "Max" : "数量", count.FindPropertyRelative("scalar").floatValue));
                    }
                    else
                    {
                        if (mode == 2) count.FindPropertyRelative("minCurve").animationCurveValue = EditorGUI.CurveField(Cell(2), count.FindPropertyRelative("minCurve").animationCurveValue);
                        count.FindPropertyRelative("maxCurve").animationCurveValue = EditorGUI.CurveField(Cell(3), count.FindPropertyRelative("maxCurve").animationCurveValue);
                    }
                    int cycles = EditorGUI.DelayedIntField(Cell(0, 1), "Cycles", burst.FindPropertyRelative("cycleCount").intValue);
                    float interval = EditorGUI.DelayedFloatField(Cell(1, 1), "Interval", burst.FindPropertyRelative("repeatInterval").floatValue);
                    float probability = EditorGUI.Slider(new Rect(Cell(2, 1).x, Cell(2, 1).y, line.width / 2 - 5, 20), "概率", burst.FindPropertyRelative("probability").floatValue, 0, 1);
                    if (EditorGUI.EndChangeCheck())
                    {
                        burst.FindPropertyRelative("time").floatValue = Mathf.Max(0, time);
                        burst.FindPropertyRelative("cycleCount").intValue = Mathf.Max(0, cycles);
                        burst.FindPropertyRelative("repeatInterval").floatValue = Mathf.Max(0.01f, interval);
                        burst.FindPropertyRelative("probability").floatValue = probability;
                        count.FindPropertyRelative("minMaxState").intValue = mode;
                        int index = selectedKey;
                        if (CascadeTimelineAuthoring.Apply(session, row.Track.Emitter, "Edit Particle Burst", target =>
                            target.CopyFromSerializedProperty(bursts.GetArrayElementAtIndex(index)))) changed?.Invoke();
                    }
                }
                finally { EditorGUIUtility.labelWidth = previous; }
            }
        }
    }
}
