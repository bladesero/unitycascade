using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    public static partial class CascadeModules
    {
        static int Int(SerializedObject so, string path) => so.FindProperty(path)?.intValue ?? 0;
        static bool Bool(SerializedObject so, string path) => so.FindProperty(path)?.boolValue ?? false;

        // Conditions only control presentation. Switching a mode never erases its inactive values.
        internal static bool IsVisible(SerializedObject so, string path)
        {
            if (!IsAdvancedFieldVisible(so, path)) return false;
            switch (path)
            {
                case "prewarm": return Bool(so, "looping");
                case "startDelay": return !(Bool(so, "looping") && Bool(so, "prewarm"));
                case "moveWithCustomTransform": return Int(so, "moveWithTransform") == (int)ParticleSystemSimulationSpace.Custom;
                case "randomSeed": return !Bool(so, "autoRandomSeed");
                case "ringBufferLoopRange": return Int(so, "ringBufferMode") == (int)ParticleSystemRingBufferMode.LoopUntilReplaced;
                case "InitialModule.startSizeY": case "InitialModule.startSizeZ": return Bool(so, "InitialModule.size3D");
                case "InitialModule.startRotationX": case "InitialModule.startRotationY": return Bool(so, "InitialModule.rotation3D");
                case "InitialModule.customEmitterVelocity": return Int(so, "emitterVelocityMode") == (int)ParticleSystemEmitterVelocityMode.Custom;
                case "SizeModule.y": case "SizeModule.z": return Bool(so, "SizeModule.separateAxes");
                case "RotationModule.x": case "RotationModule.y": return Bool(so, "RotationModule.separateAxes");
                case "ClampVelocityModule.x": case "ClampVelocityModule.y": case "ClampVelocityModule.z": case "ClampVelocityModule.inWorldSpace":
                    return Bool(so, "ClampVelocityModule.separateAxis");
                case "ClampVelocityModule.magnitude": return !Bool(so, "ClampVelocityModule.separateAxis");
                case "NoiseModule.strengthY": case "NoiseModule.strengthZ": return Bool(so, "NoiseModule.separateAxes");
                case "NoiseModule.octaveMultiplier": case "NoiseModule.octaveScale": return Int(so, "NoiseModule.octaves") > 1;
                case "NoiseModule.remap": return Bool(so, "NoiseModule.remapEnabled");
                case "NoiseModule.remapY": case "NoiseModule.remapZ": return Bool(so, "NoiseModule.remapEnabled") && Bool(so, "NoiseModule.separateAxes");
                case "UVModule.tilesX": case "UVModule.tilesY": case "UVModule.animationType": return Int(so, "UVModule.mode") == (int)ParticleSystemAnimationMode.Grid;
                case "UVModule.sprites": return Int(so, "UVModule.mode") == (int)ParticleSystemAnimationMode.Sprites;
                case "UVModule.rowMode": return Int(so, "UVModule.mode") == (int)ParticleSystemAnimationMode.Grid && Int(so, "UVModule.animationType") == (int)ParticleSystemAnimationType.SingleRow;
                case "UVModule.rowIndex": return IsVisible(so, "UVModule.rowMode") && Int(so, "UVModule.rowMode") == (int)ParticleSystemAnimationRowMode.Custom;
                case "UVModule.fps": return Int(so, "UVModule.timeMode") == (int)ParticleSystemAnimationTimeMode.FPS;
                case "UVModule.speedRange": return Int(so, "UVModule.timeMode") == (int)ParticleSystemAnimationTimeMode.Speed;
                case "UVModule.frameOverTime": case "UVModule.cycles": return Int(so, "UVModule.timeMode") != (int)ParticleSystemAnimationTimeMode.FPS;
                case "TrailModule.ribbonCount": case "TrailModule.splitSubEmitterRibbons": case "TrailModule.attachRibbonsToTransform":
                    return Int(so, "TrailModule.mode") == (int)ParticleSystemTrailMode.Ribbon;
                case "TrailModule.ratio": case "TrailModule.lifetime": case "TrailModule.minVertexDistance": case "TrailModule.worldSpace":
                case "TrailModule.dieWithParticles": case "TrailModule.sizeAffectsLifetime":
                    return Int(so, "TrailModule.mode") == (int)ParticleSystemTrailMode.PerParticle;
                case "m_Mesh": case "m_Mesh1": case "m_Mesh2": case "m_Mesh3": case "m_Meshes": case "m_MeshDistribution": case "m_EnableGPUInstancing":
                    return Int(so, "m_RenderMode") == (int)ParticleSystemRenderMode.Mesh;
                case "m_MeshWeighting": case "m_MeshWeighting1": case "m_MeshWeighting2": case "m_MeshWeighting3":
                    return Int(so, "m_RenderMode") == (int)ParticleSystemRenderMode.Mesh && Int(so, "m_MeshDistribution") == (int)ParticleSystemMeshDistribution.NonUniformRandom;
                case "m_CameraVelocityScale": case "m_VelocityScale": case "m_LengthScale": case "m_FreeformStretching":
                    return Int(so, "m_RenderMode") == (int)ParticleSystemRenderMode.Stretch;
                case "m_RotateWithStretchDirection": return Int(so, "m_RenderMode") == (int)ParticleSystemRenderMode.Stretch && Bool(so, "m_FreeformStretching");
                case "m_VertexStreams": return Bool(so, "m_UseCustomVertexStreams");
                case "m_TrailVertexStreams": return Bool(so, "m_UseCustomTrailVertexStreams");
                case "m_NormalDirection": case "m_MinParticleSize": case "m_MaxParticleSize":
                    return Int(so, "m_RenderMode") != (int)ParticleSystemRenderMode.Mesh && Int(so, "m_RenderMode") != (int)ParticleSystemRenderMode.None;
            }
            return true;
        }

        static void Fields(SerializedObject so, CascadeSession session, string prefix, params string[] names)
        {
            foreach (string name in names) Field(so.FindProperty(prefix + name), session);
        }

        static void DrawMain(SerializedObject so, CascadeSession session)
        {
            Fields(so, session, "", "lengthInSec", "looping", "prewarm", "startDelay");
            Fields(so, session, "InitialModule.", "startLifetime", "startSpeed", "size3D", "startSize", "startSizeY", "startSizeZ",
                "rotation3D", "startRotationX", "startRotationY", "startRotation", "randomizeRotationDirection", "startColor", "gravitySource", "gravityModifier");
            EditorGUILayout.Space();
            Fields(so, session, "", "moveWithTransform", "moveWithCustomTransform", "simulationSpeed", "useUnscaledTime", "scalingMode", "playOnAwake", "emitterVelocityMode");
            Fields(so, session, "InitialModule.", "customEmitterVelocity", "maxNumParticles");
            Fields(so, session, "", "autoRandomSeed", "randomSeed", "stopAction", "cullingMode", "ringBufferMode", "ringBufferLoopRange");
        }

        static void DrawOrderedModule(SerializedProperty module, CascadeSession session)
        {
            string[] order = null;
            switch (module.name)
            {
                case "SizeModule": order = new[] { "separateAxes", "curve", "y", "z" }; break;
                case "RotationModule": order = new[] { "separateAxes", "x", "y", "curve" }; break;
                case "ClampVelocityModule": order = new[] { "separateAxis", "x", "y", "z", "magnitude", "inWorldSpace", "dampen", "drag", "multiplyDragByParticleSize", "multiplyDragByParticleVelocity" }; break;
                case "NoiseModule": order = new[] { "separateAxes", "strength", "strengthY", "strengthZ", "frequency", "damping", "octaves", "octaveMultiplier", "octaveScale", "quality", "scrollSpeed", "remapEnabled", "remap", "remapY", "remapZ", "positionAmount", "rotationAmount", "sizeAmount" }; break;
                case "UVModule": order = new[] { "mode", "tilesX", "tilesY", "sprites", "animationType", "rowMode", "rowIndex", "timeMode", "fps", "speedRange", "frameOverTime", "startFrame", "cycles", "uvChannelMask" }; break;
            }
            if (order == null) Children(module, session, true);
            else Fields(module.serializedObject, session, module.propertyPath + ".", order);
        }

        // 2022.3 enum values also cover legacy shell shapes without rewriting them on repaint.
        static readonly int[] ShapeChoices = { 0, 2, 4, 17, 5, 6, 13, 14, 19, 20, 10, 12, 18 };
        static readonly string[] ShapeNames = { "Sphere", "Hemisphere", "Cone", "Donut", "Box", "Mesh", "Mesh Renderer", "Skinned Mesh Renderer", "Sprite", "Sprite Renderer", "Circle", "Edge", "Rectangle" };
        static int ShapeFamily(int type)
        {
            switch (type) { case 1: return 0; case 3: return 2; case 7: case 8: case 9: return 4; case 11: return 10; case 15: case 16: return 5; default: return type; }
        }

        internal static string[] ShapeFields(SerializedObject so)
        {
            int type = Int(so, "ShapeModule.type");
            int family = ShapeFamily(type);
            var fields = new System.Collections.Generic.List<string>();
            if (family == 0 || family == 2 || family == 4 || family == 10 || family == 17)
            {
                if (family == 4) fields.Add("angle");
                fields.Add("radius.value");
                if (family == 17) fields.Add("donutRadius");
                fields.Add("radiusThickness");
                fields.Add("arc");
                if (type == 8 || type == 9) fields.Add("length");
            }
            if (family == 12) fields.Add("radius");
            if (type == 15 || type == 16) fields.Add("boxThickness");
            bool mesh = family == 6 || family == 13 || family == 14;
            bool sprite = family == 19 || family == 20;
            if (mesh || sprite)
            {
                fields.Add("placementMode");
                if (mesh && Int(so, "ShapeModule.placementMode") != (int)ParticleSystemMeshShapeType.Triangle) fields.Add("m_MeshSpawn");
                fields.Add(family == 6 ? "m_Mesh" : family == 13 ? "m_MeshRenderer" : family == 14 ? "m_SkinnedMeshRenderer" : family == 19 ? "m_Sprite" : "m_SpriteRenderer");
                if (mesh)
                {
                    fields.Add("m_UseMeshMaterialIndex");
                    if (Bool(so, "ShapeModule.m_UseMeshMaterialIndex")) fields.Add("m_MeshMaterialIndex");
                    fields.Add("m_UseMeshColors");
                }
                fields.Add("m_MeshNormalOffset");
            }
            fields.Add("m_Texture");
            if (so.FindProperty("ShapeModule.m_Texture").objectReferenceValue)
            {
                fields.AddRange(new[] { "m_TextureClipChannel", "m_TextureClipThreshold", "m_TextureColorAffectsParticles", "m_TextureAlphaAffectsParticles", "m_TextureBilinearFiltering" });
                if (mesh) fields.Add("m_TextureUVChannel");
            }
            fields.AddRange(new[] { "m_Position", "m_Rotation", "m_Scale", "alignToDirection", "randomDirectionAmount", "sphericalDirectionAmount", "randomPositionAmount" });
            return fields.ToArray();
        }

        static void DrawShape(SerializedObject so, CascadeSession session)
        {
            var property = so.FindProperty("ShapeModule.type");
            int family = ShapeFamily(property.intValue);
            int current = Array.IndexOf(ShapeChoices, family);
            int next = EditorGUILayout.Popup("Shape", current, ShapeNames);
            if (next != current && next >= 0) { property.intValue = ShapeChoices[next]; family = ShapeChoices[next]; }
            if (family == 4 || family == 5)
            {
                int mode = family == 4 ? (property.intValue == 8 || property.intValue == 9 ? 1 : 0) : (property.intValue == 15 ? 1 : property.intValue == 16 ? 2 : 0);
                int selectedMode = EditorGUILayout.Popup("Emit From", mode, family == 4 ? new[] { "Base", "Volume" } : new[] { "Volume", "Shell", "Edge" });
                if (selectedMode != mode) property.intValue = family == 4 ? (selectedMode == 0 ? 4 : 8) : (selectedMode == 0 ? 5 : selectedMode == 1 ? 15 : 16);
            }
            foreach (string field in ShapeFields(so))
            {
                string path = "ShapeModule." + field;
                if (field == "arc" || field == "radius" || field == "m_MeshSpawn")
                {
                    EditorGUILayout.LabelField(field == "arc" ? "Arc" : field == "radius" ? "Radius" : "Mesh Spawn", EditorStyles.boldLabel);
                    var parent = so.FindProperty(path);
                    Field(parent.FindPropertyRelative("value"), session, field == "arc" ? "Angle" : "Radius");
                    Field(parent.FindPropertyRelative("mode"), session, "Mode");
                    bool vertex = field == "m_MeshSpawn" && Int(so, "ShapeModule.placementMode") == (int)ParticleSystemMeshShapeType.Vertex;
                    if (!vertex) Field(parent.FindPropertyRelative("spread"), session, "Spread");
                    int mode = Int(so, path + ".mode");
                    if (!vertex && (mode == 1 || mode == 2)) Field(parent.FindPropertyRelative("speed"), session, "Speed");
                }
                else Field(so.FindProperty(path), session, field == "radius.value" ? "Radius" : null);
            }
        }

        static void DrawEmission(SerializedObject so, CascadeSession session)
        {
            Fields(so, session, "EmissionModule.", "rateOverTime", "rateOverDistance");
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bursts", EditorStyles.boldLabel);
            var bursts = so.FindProperty("EmissionModule.m_Bursts");
            Rect header = GUILayoutUtility.GetRect(700, 20, GUILayout.ExpandWidth(true));
            string[] titles = { "Time (s)", "Count", "Cycles · 0=∞", "Interval (s)", "Probability" };
            for (int c = 0; c < titles.Length; c++) EditorGUI.LabelField(BurstCell(header, c), titles[c], EditorStyles.miniBoldLabel);
            int remove = -1;
            for (int i = 0; i < bursts.arraySize; i++)
            {
                var burst = bursts.GetArrayElementAtIndex(i);
                Rect row = GUILayoutUtility.GetRect(700, 46, GUILayout.ExpandWidth(true));
                if ((i & 1) == 0) EditorGUI.DrawRect(row, new Color(0.5f, 0.5f, 0.5f, 0.08f));
                BurstNumber(BurstCell(row, 0), burst.FindPropertyRelative("time"), 0, float.MaxValue);
                BurstCount(BurstCell(row, 1), burst.FindPropertyRelative("countCurve"));
                BurstNumber(BurstCell(row, 2), burst.FindPropertyRelative("cycleCount"), 0, int.MaxValue);
                using (new EditorGUI.DisabledScope(burst.FindPropertyRelative("cycleCount").intValue == 1))
                    BurstNumber(BurstCell(row, 3), burst.FindPropertyRelative("repeatInterval"), 0.01f, float.MaxValue);
                BurstNumber(BurstCell(row, 4), burst.FindPropertyRelative("probability"), 0, 1);
                if (GUI.Button(new Rect(row.xMax - 26, row.y + 2, 24, 18), new GUIContent("−", "删除此 Burst"))) remove = i;
            }
            if (bursts.arraySize == 0) EditorGUILayout.LabelField("暂无 Burst。点击新增添加一次瞬时发射。", EditorStyles.miniLabel);
            bool add = GUILayout.Button("+ 新增 Burst", GUILayout.Width(120));
            if (remove >= 0) RemoveBurst(bursts, remove);
            if (add) ResizeArray(bursts, bursts.arraySize + 1);
        }

        static Rect BurstCell(Rect row, int column)
        {
            float unit = (row.width - 42) / 7f;
            float[] offsets = { 0, 1, 4, 5, 6 };
            return new Rect(row.x + unit * offsets[column] + 3, row.y + 2, unit * (column == 1 ? 3 : 1) - 6, 18);
        }

        static void BurstNumber(Rect rect, SerializedProperty property, float min, float max)
        {
            EditorGUI.BeginChangeCheck();
            if (property.propertyType == SerializedPropertyType.Integer)
            {
                int value = EditorGUI.IntField(rect, property.intValue);
                if (EditorGUI.EndChangeCheck()) property.intValue = Math.Max(0, value);
            }
            else
            {
                float value = EditorGUI.FloatField(rect, property.floatValue);
                if (EditorGUI.EndChangeCheck()) property.floatValue = Mathf.Clamp(value, min, max);
            }
        }

        static void BurstCount(Rect rect, SerializedProperty count)
        {
            var mode = count.FindPropertyRelative("minMaxState");
            int next = EditorGUI.Popup(new Rect(rect.x, rect.y, 98, 18), mode.intValue, CurveModes);
            if (next != mode.intValue) mode.intValue = next;
            Rect valueRect = new Rect(rect.x + 102, rect.y, rect.width - 102, 18);
            if (next != 0)
            {
                GUI.Label(new Rect(valueRect.x, valueRect.y, 26, 18), next == 3 ? "Max" : "×", EditorStyles.miniLabel);
                valueRect.x += 26; valueRect.width -= 26;
            }
            BurstNumber(valueRect, count.FindPropertyRelative("scalar"), 0, float.MaxValue);
            if (next == 3)
            {
                GUI.Label(new Rect(rect.x, rect.y + 22, 26, 18), "Min", EditorStyles.miniLabel);
                BurstNumber(new Rect(rect.x + 26, rect.y + 22, rect.width - 26, 18), count.FindPropertyRelative("minScalar"), 0, float.MaxValue);
            }
            if (next == 1 || next == 2)
            {
                float width = next == 2 ? (rect.width - 4) / 2 : rect.width;
                EditorGUI.PropertyField(new Rect(rect.x, rect.y + 22, width, 18), count.FindPropertyRelative("maxCurve"), new GUIContent("", "Max Curve"));
                if (next == 2) EditorGUI.PropertyField(new Rect(rect.x + width + 4, rect.y + 22, width, 18), count.FindPropertyRelative("minCurve"), new GUIContent("", "Min Curve"));
            }
        }

        internal static void RemoveBurst(SerializedProperty bursts, int index)
        {
            bursts.DeleteArrayElementAtIndex(index);
            bursts.serializedObject.FindProperty("EmissionModule.m_BurstCount").intValue = bursts.arraySize;
        }
    }
}
