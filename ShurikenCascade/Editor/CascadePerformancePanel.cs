using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal sealed class CascadePerformancePanel
    {
        readonly List<CascadePreviewStatistics.EmitterSnapshot> rows = new List<CascadePreviewStatistics.EmitterSnapshot>();
        Vector2 scroll;
        int revision = -1, sort;
        bool descending = true;

        internal void Draw(CascadeSession session, CascadePreview preview, ref ParticleSystem selected, Action<ParticleSystem> selectEmitter = null, Func<ParticleSystem, bool> isSelected = null, Action extraSummary = null)
        {
            var stats = preview.Statistics;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            extraSummary?.Invoke();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"粒子 {stats.ParticleCount:N0} / 峰值 {stats.ParticlePeak:N0}    存活发射器 {stats.LiveEmitters}    材质引用 {stats.MaterialCount}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("重置统计", EditorStyles.toolbarButton, GUILayout.Width(70))) { stats.Reset(); revision = -1; }
            }
            EditorGUILayout.LabelField($"模拟 CPU：{stats.Simulation.Mean:0.###} / {stats.Simulation.Peak:0.###} ms/步    渲染提交 CPU：{stats.Rendering.Mean:0.###} / {stats.Rendering.Peak:0.###} ms/次", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"最近 120 次有效样本的均值 / 峰值 · 定位重算 {preview.SeekMilliseconds:0.##} ms（独立统计）", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"可绘制几何估算：{stats.DrawVertices:N0} 顶点 / {stats.DrawTriangles:N0} 三角形" + (stats.GeometryIncomplete ? " · 部分 Mesh 为 N/A" : "") + (stats.HasTrails ? " · 不含 Trails" : ""), EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("统计仅描述当前编辑器预览。隐藏仍参与模拟；父系统 CPU 包含其子发射器。Mesh 使用上界估算，不包含相机裁剪、实际 Draw Calls、GPU 时间或 Overdraw。", MessageType.None);
            if (revision != stats.Revision)
            {
                rows.Clear();
                for (int i = 0; i < stats.Emitters.Count; i++) rows.Add(stats.Emitters[i]);
                rows.Sort(Compare);
                revision = stats.Revision;
            }
            using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(920)))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("发射器", GUILayout.Width(170));
                    GUILayout.Label("绘制", GUILayout.Width(55));
                    SortButton("粒子 / 峰值", 0, 110);
                    GUILayout.Label("上限", GUILayout.Width(60));
                    GUILayout.Label("材质", GUILayout.Width(40));
                    SortButton("顶点 / 三角形估算", 1, 200);
                    SortButton("CPU 均值 / 峰值", 2, 180);
                }
                var selectedKeys = new HashSet<string>();
                if (isSelected != null) foreach (var emitter in session.Emitters)
                    if (isSelected(emitter)) selectedKeys.Add(CascadeSession.Key(emitter.transform, session.Root.transform));
                string selectedKey = selected ? CascadeSession.Key(selected.transform, session.Root.transform) : null;
                foreach (var row in rows)
                {
                    using (new EditorGUILayout.HorizontalScope((isSelected != null ? selectedKeys.Contains(row.Key) : row.Key == selectedKey) ? EditorStyles.helpBox : GUIStyle.none))
                    {
                        if (GUILayout.Button(new GUIContent(row.Name + " [" + row.Key + "]", row.Key), EditorStyles.miniButton, GUILayout.Width(170)))
                            foreach (var emitter in session.Emitters)
                                if (CascadeSession.Key(emitter.transform, session.Root.transform) == row.Key)
                                { if (selectEmitter != null) selectEmitter(emitter); else selected = emitter; break; }
                        GUILayout.Label(row.Drawable ? "可绘制" : "不绘制", GUILayout.Width(55));
                        GUILayout.Label($"{row.Particles:N0} / {row.Peak:N0}", GUILayout.Width(110));
                        GUILayout.Label(row.MaxParticles.ToString("N0"), GUILayout.Width(60));
                        GUILayout.Label(row.Materials.ToString(), GUILayout.Width(40));
                        string geometry = row.GeometryKnown ? $"{row.Vertices:N0} / {row.Triangles:N0}" + (row.Mesh ? " 上界" : "") : "N/A";
                        if (row.Trails) geometry += " + Trails 未计";
                        GUILayout.Label(geometry, GUILayout.Width(200));
                        GUILayout.Label(row.EventDriven ? "计入父系统" : row.CpuSamples == 0 ? "尚无播放样本" : $"{row.CpuMean:0.###} / {row.CpuPeak:0.###} ms", GUILayout.Width(180));
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void SortButton(string label, int column, float width)
        {
            if (!GUILayout.Button(label + (sort == column ? descending ? " ▼" : " ▲" : ""), EditorStyles.toolbarButton, GUILayout.Width(width))) return;
            descending = sort == column ? !descending : true;
            sort = column; revision = -1;
        }

        int Compare(CascadePreviewStatistics.EmitterSnapshot a, CascadePreviewStatistics.EmitterSnapshot b)
        {
            int result = sort == 0 ? a.Particles.CompareTo(b.Particles) : sort == 1 ? a.Triangles.CompareTo(b.Triangles) : a.CpuMean.CompareTo(b.CpuMean);
            return result == 0 ? string.CompareOrdinal(a.Key, b.Key) : descending ? -result : result;
        }
    }
}
