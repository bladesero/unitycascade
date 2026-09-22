using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    // Inspect serialized MinMax containers, never their dormant min/max storage independently.
    internal static class CascadeTimelineDiscovery
    {
        internal static List<CascadeParameterRow> Collect(SerializedObject so, CascadeTimelineTrack track, int module)
        {
            var rows = new List<CascadeParameterRow>();
            string modulePath = CascadeModules.All[module].Path;
            var root = so.FindProperty(modulePath);
            if (root == null) return rows;
            var shapeFields = modulePath == "ShapeModule" ? CascadeModules.ShapeFields(so) : null;

            bool Visible(SerializedProperty property)
            {
                string path = property.propertyPath;
                if (!CascadeModules.IsVisible(so, path)) return false;
                if (shapeFields == null || path == modulePath) return true;
                string relative = path.Substring(modulePath.Length + 1);
                bool included = Array.Exists(shapeFields, f => f == relative || relative.StartsWith(f + ".", StringComparison.Ordinal) || f.StartsWith(relative + ".", StringComparison.Ordinal));
                if (!included) return false;
                if (!path.EndsWith(".speed", StringComparison.Ordinal)) return true;
                string parent = path.Substring(0, path.Length - 6);
                int mode = so.FindProperty(parent + ".mode").intValue;
                return (mode == 1 || mode == 2) && !(parent.EndsWith("m_MeshSpawn", StringComparison.Ordinal) &&
                    so.FindProperty("ShapeModule.placementMode").intValue == (int)ParticleSystemMeshShapeType.Vertex);
            }

            void Add(SerializedProperty value, string label, string ownerPath, bool random = false)
            {
                bool curve = value.propertyType == SerializedPropertyType.AnimationCurve;
                var row = new CascadeParameterRow { Track = track, Module = module, Path = value.propertyPath, Label = label,
                    Kind = curve ? CascadeParameterRowKind.Curve : CascadeParameterRowKind.Gradient,
                    Height = curve ? 92 : 74, Curve = curve ? value.animationCurveValue : null,
                    Gradient = curve ? null : value.gradientValue, RandomDomain = random };
                SetDomain(row, so, modulePath, ownerPath);
                rows.Add(row);
            }

            void Visit(SerializedProperty property, string label)
            {
                if (!Visible(property)) return;
                if (property.propertyType == SerializedPropertyType.AnimationCurve || property.propertyType == SerializedPropertyType.Gradient)
                { Add(property, label, property.propertyPath); return; }
                if (property.propertyType != SerializedPropertyType.Generic) return;
                var mode = property.FindPropertyRelative("minMaxState");
                if (mode != null)
                {
                    var curve = property.FindPropertyRelative("maxCurve");
                    var gradient = property.FindPropertyRelative("maxGradient");
                    if (curve != null)
                    {
                        if (mode.intValue == 2) Add(property.FindPropertyRelative("minCurve"), label + " Min", property.propertyPath);
                        if (mode.intValue == 1 || mode.intValue == 2) Add(curve, label + (mode.intValue == 2 ? " Max" : ""), property.propertyPath);
                    }
                    else if (gradient != null)
                    {
                        if (mode.intValue == 3) Add(property.FindPropertyRelative("minGradient"), label + " A", property.propertyPath);
                        if (mode.intValue == 1 || mode.intValue == 3 || mode.intValue == 4)
                            Add(gradient, label + (mode.intValue == 3 ? " B" : ""), property.propertyPath, mode.intValue == 4);
                    }
                    return;
                }
                if (property.isArray)
                {
                    for (int i = 0; i < property.arraySize; i++) Visit(property.GetArrayElementAtIndex(i), label + " " + (i + 1));
                    return;
                }
                var child = property.Copy(); var end = property.GetEndProperty();
                if (!child.Next(true)) return;
                do
                {
                    if (SerializedProperty.EqualContents(child, end)) break;
                    string name = child.displayName;
                    if (child.name == "m_Bursts") name = "Burst";
                    if (child.name == "countCurve") name = "Count";
                    Visit(child.Copy(), string.IsNullOrEmpty(label) ? name : label + " / " + name);
                } while (child.Next(false));
            }

            Visit(root, "");
            if (modulePath == "InitialModule")
                foreach (string path in CascadeModules.MainFields)
                { var field = so.FindProperty(path); if (field != null) Visit(field, field.displayName); }
            return rows;
        }

        static void SetDomain(CascadeParameterRow row, SerializedObject so, string module, string path)
        {
            row.AxisLabel = "粒子生命周期";
            if (module == "InitialModule" || module == "EmissionModule" || module == "ShapeModule" || path == "NoiseModule.scrollSpeed")
            { row.EmitterTime = true; row.AxisLabel = "发射器周期"; }
            if (module.EndsWith("BySpeedModule", StringComparison.Ordinal) || module == "LifetimeByEmitterSpeedModule" ||
                (module == "UVModule" && path.EndsWith("frameOverTime", StringComparison.Ordinal) && so.FindProperty("UVModule.timeMode").intValue == (int)ParticleSystemAnimationTimeMode.Speed))
            {
                var range = so.FindProperty(module + (module == "LifetimeByEmitterSpeedModule" ? ".m_Range" : module == "UVModule" ? ".speedRange" : ".range"));
                row.NonTemporal = true; row.AxisLabel = "速度"; row.AxisUnit = "m/s";
                if (range != null) { row.AxisMin = range.vector2Value.x; row.AxisMax = range.vector2Value.y; }
            }
            if (path.StartsWith("NoiseModule.remap", StringComparison.Ordinal)) { row.NonTemporal = true; row.AxisLabel = "噪声输入"; }
            if (path == "TrailModule.widthOverTrail" || path == "TrailModule.colorOverTrail")
            { row.NonTemporal = true; row.AxisLabel = "拖尾长度"; }
            if (path == "startDelay" || path == "UVModule.startFrame" || module == "CollisionModule" || path == "TrailModule.lifetime")
            { row.NonTemporal = true; row.AxisLabel = "参数采样"; }
            if (row.RandomDomain) { row.NonTemporal = true; row.AxisLabel = "随机颜色采样"; }
        }
    }
}
