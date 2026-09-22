using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    public static partial class CascadeModules
    {
        public sealed class Module
        {
            public readonly string Name, Path;
            public readonly bool Supported;
            public Module(string name, string path, bool supported = true) { Name = name; Path = path; Supported = supported; }
        }

        // Unity 2022.3 serialized module names; intentionally kept in one place.
        public static readonly Module[] All =
        {
            new Module("Transform", "$transform"), new Module("Main", "InitialModule"),
            new Module("Emission", "EmissionModule"), new Module("Shape", "ShapeModule"),
            new Module("Velocity over Lifetime", "VelocityModule"),
            new Module("Limit Velocity over Lifetime", "ClampVelocityModule"),
            new Module("Color over Lifetime", "ColorModule"), new Module("Size over Lifetime", "SizeModule"),
            new Module("Rotation over Lifetime", "RotationModule"), new Module("Noise", "NoiseModule"),
            new Module("Texture Sheet Animation", "UVModule"), new Module("Trails", "TrailModule"),
            new Module("Renderer", "$renderer"),
            new Module("Inherit Velocity", "InheritVelocityModule"),
            new Module("Lifetime by Emitter Speed", "LifetimeByEmitterSpeedModule"),
            new Module("Force over Lifetime", "ForceModule"),
            new Module("Color by Speed", "ColorBySpeedModule"),
            new Module("Size by Speed", "SizeBySpeedModule"),
            new Module("Rotation by Speed", "RotationBySpeedModule"),
            new Module("External Forces", "ExternalForcesModule"),
            new Module("Collision", "CollisionModule"), new Module("Triggers", "TriggerModule"),
            new Module("Sub Emitters", "SubModule"), new Module("Lights", "LightsModule"),
            new Module("Custom Data", "CustomDataModule")
        };

        internal static readonly string[] MainFields =
        {
            "lengthInSec", "looping", "prewarm", "startDelay", "simulationSpeed", "useUnscaledTime",
            "moveWithTransform", "moveWithCustomTransform", "scalingMode", "playOnAwake", "emitterVelocityMode",
            "stopAction", "cullingMode", "ringBufferMode", "ringBufferLoopRange", "autoRandomSeed", "randomSeed"
        };
        static readonly string[] CurveModes = { "Constant", "Curve", "Two Curves", "Two Constants" };
        static readonly string[] GradientModes = { "Color", "Gradient", "Two Colors", "Two Gradients", "Random Color" };
        static readonly Dictionary<string, Type> EnumTypes = new Dictionary<string, Type>
        {
            { "stopAction", typeof(ParticleSystemStopAction) }, { "cullingMode", typeof(ParticleSystemCullingMode) },
            { "ringBufferMode", typeof(ParticleSystemRingBufferMode) }, { "emitterVelocityMode", typeof(ParticleSystemEmitterVelocityMode) },
            { "moveWithTransform", typeof(ParticleSystemSimulationSpace) }, { "scalingMode", typeof(ParticleSystemScalingMode) },
            { "ShapeModule.type", typeof(ParticleSystemShapeType) }, { "ShapeModule.placementMode", typeof(ParticleSystemMeshShapeType) },
            { "ShapeModule.m_MeshSpawn.mode", typeof(ParticleSystemShapeMultiModeValue) },
            { "ShapeModule.radius.mode", typeof(ParticleSystemShapeMultiModeValue) }, { "ShapeModule.arc.mode", typeof(ParticleSystemShapeMultiModeValue) },
            { "ShapeModule.m_TextureClipChannel", typeof(ParticleSystemShapeTextureChannel) },
            { "UVModule.mode", typeof(ParticleSystemAnimationMode) }, { "UVModule.timeMode", typeof(ParticleSystemAnimationTimeMode) },
            { "UVModule.animationType", typeof(ParticleSystemAnimationType) }, { "UVModule.rowMode", typeof(ParticleSystemAnimationRowMode) },
            { "NoiseModule.quality", typeof(ParticleSystemNoiseQuality) }, { "TrailModule.mode", typeof(ParticleSystemTrailMode) },
            { "TrailModule.textureMode", typeof(ParticleSystemTrailTextureMode) },
            { "m_RenderMode", typeof(ParticleSystemRenderMode) }, { "m_MeshDistribution", typeof(ParticleSystemMeshDistribution) },
            { "m_SortMode", typeof(ParticleSystemSortMode) }, { "m_RenderAlignment", typeof(ParticleSystemRenderSpace) },
            { "m_MaskInteraction", typeof(SpriteMaskInteraction) },
            { "m_LightProbeUsage", typeof(UnityEngine.Rendering.LightProbeUsage) },
            { "m_ReflectionProbeUsage", typeof(UnityEngine.Rendering.ReflectionProbeUsage) },
            { "InheritVelocityModule.m_Mode", typeof(ParticleSystemInheritVelocityMode) },
            { "ExternalForcesModule.influenceFilter", typeof(ParticleSystemGameObjectFilter) },
            { "CollisionModule.type", typeof(ParticleSystemCollisionType) },
            { "CollisionModule.collisionMode", typeof(ParticleSystemCollisionMode) },
            { "CollisionModule.quality", typeof(ParticleSystemCollisionQuality) },
            { "TriggerModule.inside", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.outside", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.enter", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.exit", typeof(ParticleSystemOverlapAction) },
            { "TriggerModule.colliderQueryMode", typeof(ParticleSystemColliderQueryMode) },
            { "CustomDataModule.mode0", typeof(ParticleSystemCustomDataMode) },
            { "CustomDataModule.mode1", typeof(ParticleSystemCustomDataMode) }
        };
        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "lengthInSec", "Duration" }, { "moveWithTransform", "Simulation Space" },
            { "moveWithCustomTransform", "Custom Simulation Space" }, { "autoRandomSeed", "Auto Random Seed" },
            { "InitialModule.maxNumParticles", "Max Particles" }, { "EmissionModule.m_Bursts", "Bursts" },
            { "InheritVelocityModule.m_Mode", "Mode" }, { "InheritVelocityModule.m_Curve", "Multiplier" },
            { "LifetimeByEmitterSpeedModule.m_Curve", "Lifetime Multiplier" }, { "LifetimeByEmitterSpeedModule.m_Range", "Speed Range" },
            { "ColorBySpeedModule.range", "Speed Range" }, { "SizeBySpeedModule.range", "Speed Range" }, { "RotationBySpeedModule.range", "Speed Range" },
            { "CollisionModule.m_Dampen", "Dampen" }, { "CollisionModule.m_Bounce", "Bounce" }, { "CollisionModule.m_EnergyLossOnCollision", "Lifetime Loss" },
            { "LightsModule.color", "Use Particle Color" }, { "LightsModule.range", "Size Affects Range" }, { "LightsModule.intensity", "Alpha Affects Intensity" },
            { "LightsModule.rangeCurve", "Range Multiplier" }, { "LightsModule.intensityCurve", "Intensity Multiplier" }
        };

        public static bool Draw(CascadeSession session, ParticleSystem emitter, int index)
        {
            var module = All[Mathf.Clamp(index, 0, All.Length - 1)];
            bool readOnly = session.IsReadOnly(emitter) || !module.Supported;
            if (readOnly) EditorGUILayout.HelpBox("只读：保留原始数据。" + (session.IsReadOnly(emitter) ? "此发射器属于嵌套 Prefab。" : "此模块不在首版编辑范围。"), MessageType.Info);
            using (new EditorGUI.DisabledScope(readOnly))
            {
                UnityEngine.Object target = emitter;
                if (module.Path == "$transform") target = emitter.transform;
                if (module.Path == "$renderer") target = emitter.GetComponent<ParticleSystemRenderer>();
                if (!target) { EditorGUILayout.HelpBox("组件不存在。", MessageType.Info); return false; }
                using (var serialized = new SerializedObject(target))
                {
                    serialized.Update();
                    if (module.Path == "$transform")
                    {
                        Field(serialized.FindProperty("m_LocalPosition"), session);
                        Field(serialized.FindProperty("m_LocalRotation"), session);
                        Field(serialized.FindProperty("m_LocalScale"), session);
                    }
                    else if (module.Path == "$renderer")
                    {
                        EditorGUILayout.HelpBox("材质槽仅替换引用；不会修改共享材质资产。", MessageType.None);
                        var it = serialized.GetIterator();
                        if (it.NextVisible(true)) do
                        {
                            if (it.name != "m_ObjectHideFlags" && it.name != "m_GameObject" && it.name != "m_Script") Field(it.Copy(), session);
                        } while (it.NextVisible(false));
                    }
                    else
                    {
                        var property = serialized.FindProperty(module.Path);
                        if (property == null) EditorGUILayout.HelpBox("Unity 模块字段不存在：" + module.Path, MessageType.Error);
                        else if (module.Path == "InitialModule") DrawMain(serialized, session);
                        else
                        {
                            var enabled = property.FindPropertyRelative("enabled");
                            Field(enabled, session, "Enabled");
                            using (new EditorGUI.DisabledScope(enabled != null && !enabled.boolValue))
                            {
                                if (module.Path == "ShapeModule") DrawShape(serialized, session);
                                else if (module.Path == "EmissionModule") DrawEmission(serialized, session);
                                else if (!DrawAdvancedModule(property, session)) DrawOrderedModule(property, session);
                            }
                        }
                    }
                    if (module.Path == "EmissionModule" && serialized.hasModifiedProperties)
                    {
                        var count = serialized.FindProperty("EmissionModule.m_BurstCount");
                        var bursts = serialized.FindProperty("EmissionModule.m_Bursts");
                        if (count != null && bursts != null) count.intValue = bursts.arraySize;
                    }
                    if (!readOnly && serialized.hasModifiedProperties && module.Path == "SubModule" &&
                        !CascadeModuleReferences.ValidateSubEmitters(session, emitter, serialized, out string reason))
                    {
                        serialized.Update();
                        SetReferenceError(emitter, reason);
                        return false;
                    }
                    return !readOnly && serialized.ApplyModifiedProperties();
                }
            }
        }

        static void Children(SerializedProperty parent, CascadeSession session, bool skipEnabled = false)
        {
            var child = parent.Copy();
            var end = child.GetEndProperty();
            if (!child.NextVisible(true)) return;
            do
            {
                if (SerializedProperty.EqualContents(child, end)) break;
                if ((!skipEnabled || child.name != "enabled") && child.name != "serializedVersion" && child.name != "m_BurstCount") Field(child.Copy(), session);
            } while (child.NextVisible(false));
        }

        static void Field(SerializedProperty property, CascadeSession session, string labelOverride = null)
        {
            if (property == null) return;
            if (!IsVisible(property.serializedObject, property.propertyPath)) return;
            if (labelOverride == null)
            {
                var so = property.serializedObject;
                switch (property.propertyPath)
                {
                    case "InitialModule.size3D": labelOverride = "3D Start Size"; break;
                    case "InitialModule.rotation3D": labelOverride = "3D Start Rotation"; break;
                    case "InitialModule.startSize": labelOverride = Bool(so, "InitialModule.size3D") ? "Start Size X" : "Start Size"; break;
                    case "InitialModule.startRotation": labelOverride = Bool(so, "InitialModule.rotation3D") ? "Start Rotation Z" : "Start Rotation"; break;
                    case "SizeModule.curve": labelOverride = Bool(so, "SizeModule.separateAxes") ? "X" : "Size"; break;
                    case "RotationModule.curve": labelOverride = Bool(so, "RotationModule.separateAxes") ? "Z" : "Angular Velocity"; break;
                    case "SizeBySpeedModule.curve": labelOverride = Bool(so, "SizeBySpeedModule.separateAxes") ? "X" : "Size"; break;
                    case "RotationBySpeedModule.curve": labelOverride = Bool(so, "RotationBySpeedModule.separateAxes") ? "Z" : "Angular Velocity"; break;
                    case "NoiseModule.strength": labelOverride = Bool(so, "NoiseModule.separateAxes") ? "Strength X" : "Strength"; break;
                    case "NoiseModule.remap": labelOverride = Bool(so, "NoiseModule.separateAxes") ? "Remap X" : "Remap"; break;
                }
            }
            var label = new GUIContent(labelOverride ?? (Labels.TryGetValue(property.propertyPath, out string displayName) ? displayName : property.displayName), property.propertyPath);
            bool startRotation = property.propertyPath.StartsWith("InitialModule.startRotation");
            bool angularVelocity = property.propertyPath.StartsWith("RotationModule.") || property.propertyPath.StartsWith("RotationBySpeedModule.");
            if ((startRotation || angularVelocity) && property.propertyType == SerializedPropertyType.Float &&
                (property.name == "scalar" || property.name == "minScalar"))
            {
                label.text += angularVelocity ? " (°/s)" : " (°)";
                EditorGUI.BeginChangeCheck();
                float degrees = EditorGUILayout.FloatField(label, property.floatValue * Mathf.Rad2Deg);
                if (EditorGUI.EndChangeCheck()) property.floatValue = degrees * Mathf.Deg2Rad;
                return;
            }
            if (property.propertyType == SerializedPropertyType.Integer)
            {
                Type enumType = null;
                EnumTypes.TryGetValue(property.propertyPath, out enumType);
                if (property.propertyPath.StartsWith("m_VertexStreams.Array.data[") || property.propertyPath.StartsWith("m_TrailVertexStreams.Array.data["))
                    enumType = typeof(ParticleSystemVertexStream);
                if (enumType != null)
                {
                    int chosen = Convert.ToInt32(EditorGUILayout.EnumPopup(label, (Enum)Enum.ToObject(enumType, property.intValue)));
                    if (chosen != property.intValue) property.intValue = chosen;
                    return;
                }
                if (property.propertyPath == "UVModule.uvChannelMask")
                {
                    int chosen = EditorGUILayout.MaskField(label, property.intValue, new[] { "UV0", "UV1", "UV2", "UV3" });
                    if (chosen != property.intValue) property.intValue = chosen;
                    return;
                }
                if (property.propertyPath == "InitialModule.gravitySource")
                {
                    int chosen = EditorGUILayout.Popup(label, property.intValue, new[] { "Physics 3D", "Physics 2D" });
                    if (chosen != property.intValue) property.intValue = chosen;
                    return;
                }
            }
            if (property.propertyType == SerializedPropertyType.Generic && property.FindPropertyRelative("minMaxState") != null)
            {
                MinMax(property, session, label);
                return;
            }
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                if (DrawModuleReference(property, session, label)) return;
                var before = property.objectReferenceValue;
                EditorGUILayout.PropertyField(property, label, false);
                var after = property.objectReferenceValue;
                var go = after as GameObject;
                if (after is Component component) go = component.gameObject;
                if (go && !EditorUtility.IsPersistent(go) && go != session.Root && !go.transform.IsChildOf(session.Root.transform))
                {
                    property.objectReferenceValue = before;
                    EditorGUILayout.HelpBox("Prefab 参数不能引用当前场景或预览中的对象。", MessageType.Warning);
                }
                return;
            }
            if (property.propertyType == SerializedPropertyType.Quaternion)
            {
                EditorGUI.BeginChangeCheck();
                var euler = EditorGUILayout.Vector3Field("Local Rotation", property.quaternionValue.eulerAngles);
                if (EditorGUI.EndChangeCheck()) property.quaternionValue = Quaternion.Euler(euler);
                return;
            }
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
                if (property.isExpanded)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        int size = EditorGUILayout.DelayedIntField("Size", property.arraySize);
                        if (size != property.arraySize)
                        {
                            ResizeArray(property, size);
                        }
                        for (int i = 0; i < property.arraySize; i++) Field(property.GetArrayElementAtIndex(i), session);
                    }
                }
                return;
            }
            if (property.propertyType == SerializedPropertyType.Generic)
            {
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
                if (property.isExpanded) using (new EditorGUI.IndentLevelScope()) Children(property, session);
                return;
            }
            EditorGUILayout.PropertyField(property, label, true);
        }

        internal static void ResizeArray(SerializedProperty property, int size)
        {
            int oldSize = property.arraySize;
            property.arraySize = Mathf.Clamp(size, 0, 10000);
            if (property.propertyPath != "EmissionModule.m_Bursts") return;
            for (int i = oldSize; i < property.arraySize; i++)
            {
                var burst = property.GetArrayElementAtIndex(i);
                SetFloat(burst, "time", 0);
                SetFloat(burst, "probability", 1);
                SetFloat(burst, "repeatInterval", 0.01f);
                var cycles = burst.FindPropertyRelative("cycleCount");
                if (cycles != null) cycles.intValue = 1;
                var curve = burst.FindPropertyRelative("countCurve");
                if (curve != null)
                {
                    curve.FindPropertyRelative("minMaxState").intValue = 0;
                    curve.FindPropertyRelative("scalar").floatValue = 30;
                    curve.FindPropertyRelative("minScalar").floatValue = 30;
                    curve.FindPropertyRelative("minCurve").animationCurveValue = AnimationCurve.Constant(0, 1, 1);
                    curve.FindPropertyRelative("maxCurve").animationCurveValue = AnimationCurve.Constant(0, 1, 1);
                }
            }
            property.serializedObject.FindProperty("EmissionModule.m_BurstCount").intValue = property.arraySize;
        }

        static void SetFloat(SerializedProperty parent, string name, float value)
        {
            var field = parent.FindPropertyRelative(name);
            if (field != null) field.floatValue = value;
        }

        static void MinMax(SerializedProperty property, CascadeSession session, GUIContent label)
        {
            var mode = property.FindPropertyRelative("minMaxState");
            bool gradient = property.FindPropertyRelative("maxGradient") != null;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                int current = mode.intValue;
                int chosen;
                if (property.propertyPath == "CollisionModule.m_Dampen" || property.propertyPath == "CollisionModule.m_Bounce" || property.propertyPath == "CollisionModule.m_EnergyLossOnCollision")
                {
                    // Native Collision supports constants only; retain any legacy curve until explicitly changed.
                    bool legacy = current != 0 && current != 3;
                    var options = legacy ? new[] { new GUIContent("Constant"), new GUIContent("Two Constants"), new GUIContent("Existing Curve (Legacy)") }
                        : new[] { new GUIContent("Constant"), new GUIContent("Two Constants") };
                    chosen = EditorGUILayout.IntPopup(label, current, options, legacy ? new[] { 0, 3, current } : new[] { 0, 3 });
                }
                else chosen = EditorGUILayout.Popup(label, current, gradient ? GradientModes : CurveModes);
                if (chosen != current) mode.intValue = chosen;
                if (gradient)
                {
                    if (chosen == 2) Field(property.FindPropertyRelative("minColor"), session, "Color A");
                    if (chosen == 0 || chosen == 2) Field(property.FindPropertyRelative("maxColor"), session, chosen == 0 ? "Color" : "Color B");
                    if (chosen == 3) Field(property.FindPropertyRelative("minGradient"), session, "Gradient A");
                    if (chosen == 1 || chosen == 3 || chosen == 4) Field(property.FindPropertyRelative("maxGradient"), session, chosen == 3 ? "Gradient B" : "Gradient");
                }
                else
                {
                    if (chosen == 3) Field(property.FindPropertyRelative("minScalar"), session, "Min");
                    Field(property.FindPropertyRelative("scalar"), session, chosen == 0 ? "Value" : chosen == 3 ? "Max" : "Multiplier");
                    if (chosen == 2) Field(property.FindPropertyRelative("minCurve"), session, "Min Curve");
                    if (chosen == 1 || chosen == 2) Field(property.FindPropertyRelative("maxCurve"), session, chosen == 1 ? "Curve" : "Max Curve");
                }
            }
        }
    }
}
