using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    public static partial class CascadeModules
    {
        internal static bool IsAdvancedFieldVisible(SerializedObject so, string path)
        {
            bool world = Int(so, "CollisionModule.type") == (int)ParticleSystemCollisionType.World;
            bool high = Int(so, "CollisionModule.quality") == (int)ParticleSystemCollisionQuality.High;
            switch (path)
            {
                case "SizeBySpeedModule.y": case "SizeBySpeedModule.z": return Bool(so, "SizeBySpeedModule.separateAxes");
                case "RotationBySpeedModule.x": case "RotationBySpeedModule.y": return Bool(so, "RotationBySpeedModule.separateAxes");
                case "ExternalForcesModule.influenceMask": return Int(so, "ExternalForcesModule.influenceFilter") != (int)ParticleSystemGameObjectFilter.List;
                case "ExternalForcesModule.influenceList": return Int(so, "ExternalForcesModule.influenceFilter") != (int)ParticleSystemGameObjectFilter.LayerMask;
                case "CollisionModule.m_Planes": return !world;
                case "CollisionModule.collisionMode": case "CollisionModule.collidesWith": case "CollisionModule.maxCollisionShapes":
                case "CollisionModule.colliderForce": return world;
                case "CollisionModule.quality": return world;
                case "CollisionModule.collidesWithDynamic": return world && high;
                case "CollisionModule.voxelSize": return world && !high;
                case "CollisionModule.multiplyColliderForceByParticleSize": case "CollisionModule.multiplyColliderForceByParticleSpeed": case "CollisionModule.multiplyColliderForceByCollisionAngle":
                    return world && so.FindProperty("CollisionModule.colliderForce").floatValue != 0;
            }
            if (path.StartsWith("CustomDataModule.", StringComparison.Ordinal))
            {
                string field = path.Substring("CustomDataModule.".Length);
                for (int stream = 0; stream < 2; stream++)
                {
                    int mode = Int(so, "CustomDataModule.mode" + stream);
                    if (field == "color" + stream || field == "colorLabel" + stream) return mode == (int)ParticleSystemCustomDataMode.Color;
                    if (field == "vectorComponentCount" + stream) return mode == (int)ParticleSystemCustomDataMode.Vector;
                    for (int component = 0; component < 4; component++)
                        if (field == "vector" + stream + "_" + component || field == "vectorLabel" + stream + "_" + component)
                            return mode == (int)ParticleSystemCustomDataMode.Vector && component < Int(so, "CustomDataModule.vectorComponentCount" + stream);
                }
            }
            return true;
        }

        static bool DrawAdvancedModule(SerializedProperty module, CascadeSession session)
        {
            var so = module.serializedObject;
            string prefix = module.name + ".";
            switch (module.name)
            {
                case "InheritVelocityModule":
                    if (Int(so, "moveWithTransform") == (int)ParticleSystemSimulationSpace.Local)
                        EditorGUILayout.HelpBox("Inherit Velocity 在 World Simulation Space 下生效。", MessageType.Info);
                    Fields(so, session, prefix, "m_Mode", "m_Curve"); break;
                case "LifetimeByEmitterSpeedModule": Fields(so, session, prefix, "m_Curve", "m_Range"); break;
                case "ForceModule": Fields(so, session, prefix, "x", "y", "z", "inWorldSpace", "randomizePerFrame"); break;
                case "ColorBySpeedModule": Fields(so, session, prefix, "gradient", "range"); break;
                case "SizeBySpeedModule": Fields(so, session, prefix, "separateAxes", "curve", "y", "z", "range"); break;
                case "RotationBySpeedModule": Fields(so, session, prefix, "separateAxes", "x", "y", "curve", "range"); break;
                case "ExternalForcesModule":
                    Fields(so, session, prefix, "multiplierCurve", "influenceFilter", "influenceMask");
                    DrawReferenceList(so.FindProperty(prefix + "influenceList"), session, "Force Fields"); break;
                case "CollisionModule":
                    Fields(so, session, prefix, "type", "collisionMode");
                    DrawReferenceList(so.FindProperty(prefix + "m_Planes"), session, "Planes");
                    Fields(so, session, prefix, "m_Dampen", "m_Bounce", "m_EnergyLossOnCollision", "minKillSpeed", "maxKillSpeed", "radiusScale",
                        "quality", "collidesWith", "collidesWithDynamic", "maxCollisionShapes", "voxelSize", "colliderForce",
                        "multiplyColliderForceByParticleSize", "multiplyColliderForceByParticleSpeed", "multiplyColliderForceByCollisionAngle", "collisionMessages", "interiorCollisions");
                    if (Bool(so, prefix + "collisionMessages")) EditorGUILayout.HelpBox("碰撞回调配置会保存；预览不执行游戏脚本。", MessageType.None);
                    break;
                case "TriggerModule":
                    DrawReferenceList(so.FindProperty(prefix + "primitives"), session, "Colliders (3D / 2D)");
                    Fields(so, session, prefix, "inside", "outside", "enter", "exit", "colliderQueryMode", "radiusScale");
                    EditorGUILayout.HelpBox("Callback 用于运行时脚本；预览不会执行 OnParticleTrigger。", MessageType.None);
                    break;
                case "SubModule": DrawSubEmitters(so, session); break;
                case "LightsModule":
                    Fields(so, session, prefix, "ratio", "light", "randomDistribution", "color", "range", "intensity", "rangeCurve", "intensityCurve", "maxLights"); break;
                case "CustomDataModule": DrawCustomData(so, session); break;
                default: return false;
            }
            DrawReferenceError(so.targetObject);
            return true;
        }

        static void DrawCustomData(SerializedObject so, CascadeSession session)
        {
            for (int stream = 0; stream < 2; stream++)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("Custom " + (stream + 1), EditorStyles.boldLabel);
                    Field(so.FindProperty("CustomDataModule.mode" + stream), session, "Mode");
                    var count = so.FindProperty("CustomDataModule.vectorComponentCount" + stream);
                    if (IsVisible(so, count.propertyPath))
                    {
                        EditorGUI.BeginChangeCheck();
                        int value = EditorGUILayout.IntSlider("Components", count.intValue, 1, 4);
                        if (EditorGUI.EndChangeCheck()) count.intValue = value;
                    }
                    for (int component = 0; component < 4; component++)
                    {
                        string suffix = stream + "_" + component;
                        Field(so.FindProperty("CustomDataModule.vectorLabel" + suffix), session, "XYZW"[component] + " Label");
                        Field(so.FindProperty("CustomDataModule.vector" + suffix), session,
                            so.FindProperty("CustomDataModule.vectorLabel" + suffix).stringValue);
                    }
                    Field(so.FindProperty("CustomDataModule.colorLabel" + stream), session, "Color Label");
                    Field(so.FindProperty("CustomDataModule.color" + stream), session,
                        so.FindProperty("CustomDataModule.colorLabel" + stream).stringValue);
                }
            }
            EditorGUILayout.HelpBox("输出到材质时，在 Renderer 的 Custom Vertex Streams 中添加对应 Custom1 / Custom2 通道。", MessageType.None);
        }

        static void DrawReferenceList(SerializedProperty list, CascadeSession session, string label)
        {
            if (list == null || !IsVisible(list.serializedObject, list.propertyPath)) return;
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            int remove = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Field(list.GetArrayElementAtIndex(i), session, (i + 1).ToString());
                    if (GUILayout.Button("−", GUILayout.Width(24))) remove = i;
                }
            }
            bool add = GUILayout.Button("+ " + label, GUILayout.Width(160));
            if (remove >= 0) RemoveReference(list, remove);
            if (add) { int index = list.arraySize; list.arraySize++; list.GetArrayElementAtIndex(index).objectReferenceValue = null; }
        }

        internal static void RemoveReference(SerializedProperty list, int index)
        {
            // Unity clears a non-null object slot on its first delete; explicitly clear then shrink.
            list.GetArrayElementAtIndex(index).objectReferenceValue = null;
            list.DeleteArrayElementAtIndex(index);
        }

        static void DrawSubEmitters(SerializedObject so, CascadeSession session)
        {
            var list = so.FindProperty("SubModule.subEmitters");
            int remove = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                var row = list.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Sub Emitter " + (i + 1), EditorStyles.boldLabel);
                        if (GUILayout.Button("删除", GUILayout.Width(48))) remove = i;
                    }
                    Field(row.FindPropertyRelative("emitter"), session, "Emitter");
                    var type = row.FindPropertyRelative("type");
                    EditorGUI.BeginChangeCheck();
                    var nextType = (ParticleSystemSubEmitterType)EditorGUILayout.EnumPopup("Event", (ParticleSystemSubEmitterType)type.intValue);
                    if (EditorGUI.EndChangeCheck()) type.intValue = (int)nextType;
                    var properties = row.FindPropertyRelative("properties");
                    EditorGUI.BeginChangeCheck();
                    var flags = (ParticleSystemSubEmitterProperties)EditorGUILayout.EnumFlagsField("Inherit", (ParticleSystemSubEmitterProperties)properties.intValue);
                    if (EditorGUI.EndChangeCheck()) properties.intValue = (int)flags & (int)ParticleSystemSubEmitterProperties.InheritEverything;
                    var probability = row.FindPropertyRelative("emitProbability");
                    EditorGUI.BeginChangeCheck();
                    float value = EditorGUILayout.Slider("Emit Probability", probability.floatValue, 0, 1);
                    if (EditorGUI.EndChangeCheck()) probability.floatValue = value;
                    if (type.intValue == (int)ParticleSystemSubEmitterType.Manual || type.intValue == (int)ParticleSystemSubEmitterType.Trigger)
                        EditorGUILayout.HelpBox("此事件需要运行时脚本触发。", MessageType.None);
                    if (type.intValue == (int)ParticleSystemSubEmitterType.Collision && !Bool(so, "CollisionModule.enabled"))
                        EditorGUILayout.HelpBox("Collision 事件需要启用父发射器的 Collision 模块。", MessageType.Info);
                }
            }
            bool add = GUILayout.Button("+ Sub Emitter", GUILayout.Width(150));
            if (remove >= 0) list.DeleteArrayElementAtIndex(remove);
            if (add) AddSubEmitterRow(list);
        }

        internal static void AddSubEmitterRow(SerializedProperty list)
        {
            int index = list.arraySize; list.arraySize++;
            var row = list.GetArrayElementAtIndex(index);
            row.FindPropertyRelative("emitter").objectReferenceValue = null;
            row.FindPropertyRelative("type").intValue = (int)ParticleSystemSubEmitterType.Birth;
            row.FindPropertyRelative("properties").intValue = 0;
            row.FindPropertyRelative("emitProbability").floatValue = 1;
        }
    }
}
