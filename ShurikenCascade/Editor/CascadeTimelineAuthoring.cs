using System;
using UnityEditor;
using UnityEngine;

namespace ShurikenCascade
{
    internal static class CascadeTimelineAuthoring
    {
        internal static bool Editable(CascadeSession session, ParticleSystem emitter) => session != null && session.Root && emitter &&
            emitter.transform.IsChildOf(session.Root.transform) && !session.IsReadOnly(emitter);

        internal static bool Apply(CascadeSession session, ParticleSystem emitter, string name, Action<SerializedObject> modify)
        {
            if (!Editable(session, emitter)) return false;
            using (var so = new SerializedObject(emitter))
            {
                modify(so);
                if (!so.hasModifiedProperties) return false;
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(name);
                try { so.ApplyModifiedProperties(); Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); }
                catch { Undo.RevertAllDownToGroup(group); throw; }
                finally { Undo.IncrementCurrentGroup(); }
            }
            session.MarkDirty(); return true;
        }

        internal static bool SetEnabled(CascadeSession session, ParticleSystem p, string module, bool enabled) =>
            Apply(session, p, "Toggle Particle Module", so => {
                var field = so.FindProperty(module + ".enabled");
                if (module != "InitialModule" && field != null) field.boolValue = enabled;
            });

        internal static bool AddBurst(CascadeSession session, ParticleSystem p, float time) =>
            Apply(session, p, "Add Particle Burst", so => {
                var bursts = so.FindProperty("EmissionModule.m_Bursts");
                int index = bursts.arraySize;
                if (index >= 10000) return;
                CascadeModules.ResizeArray(bursts, index + 1);
                bursts.GetArrayElementAtIndex(index).FindPropertyRelative("time").floatValue = Mathf.Max(0, time);
            });

        internal static bool DeleteBurst(CascadeSession session, ParticleSystem p, int index) =>
            Apply(session, p, "Delete Particle Burst", so => {
                var bursts = so.FindProperty("EmissionModule.m_Bursts");
                if (index < 0 || index >= bursts.arraySize) return;
                bursts.DeleteArrayElementAtIndex(index);
                so.FindProperty("EmissionModule.m_BurstCount").intValue = bursts.arraySize;
            });

        internal static bool ResetModule(CascadeSession session, ParticleSystem p, CascadeModules.Module module)
        {
            if (!Editable(session, p)) return false;
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var go = new GameObject("Cascade Module Defaults") { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                var defaults = go.AddComponent<ParticleSystem>();
                // Reset keeps an enabled module visible. Defaults may have this module disabled.
                using (var so = new SerializedObject(defaults))
                { var enabled = so.FindProperty(module.Path + ".enabled"); if (enabled != null && module.Path != "InitialModule") { enabled.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); } }
                using (var clipboard = new CascadeModuleClipboard()) { clipboard.Copy(defaults, module); clipboard.Paste(session, p, module); }
                session.MarkDirty(); return true;
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }
    }

    // Key drags edit detached values until mouse-up. The JSON guard rejects stale transactions.
    internal sealed class CascadeTimelineKeyEdit
    {
        readonly CascadeSession session;
        internal readonly ParticleSystem Emitter;
        internal readonly string Path;
        readonly string originalJson;
        readonly AnimationCurve sourceCurve;
        readonly Gradient sourceGradient;
        readonly int sourceIndex;
        internal readonly bool Alpha;
        bool finished;
        internal AnimationCurve Curve { get; private set; }
        internal Gradient Gradient { get; private set; }
        internal int Index { get; private set; }

        internal CascadeTimelineKeyEdit(CascadeSession session, ParticleSystem p, string path, int index, bool alpha = false)
        {
            this.session = session; Emitter = p; Path = path; sourceIndex = Index = index; Alpha = alpha;
            originalJson = EditorJsonUtility.ToJson(p);
            using (var so = new SerializedObject(p))
            {
                var property = so.FindProperty(path);
                if (property.propertyType == SerializedPropertyType.AnimationCurve) sourceCurve = Clone(property.animationCurveValue);
                else sourceGradient = Clone(property.gradientValue);
            }
            Curve = sourceCurve == null ? null : Clone(sourceCurve);
            Gradient = sourceGradient == null ? null : Clone(sourceGradient);
        }

        internal static AnimationCurve Clone(AnimationCurve value) => new AnimationCurve(value.keys) { preWrapMode = value.preWrapMode, postWrapMode = value.postWrapMode };
        internal static Gradient Clone(Gradient value)
        { var result = new Gradient { mode = value.mode }; result.SetKeys(value.colorKeys, value.alphaKeys); return result; }

        internal void Move(float time, float value)
        {
            if (finished) return;
            time = Mathf.Clamp01(time);
            if (sourceCurve != null)
            {
                Curve = Clone(sourceCurve);
                var key = Curve.keys[sourceIndex]; key.time = time; key.value = value;
                Index = Curve.MoveKey(sourceIndex, key);
            }
            else
            {
                Gradient = Clone(sourceGradient);
                var colors = Gradient.colorKeys; var alphas = Gradient.alphaKeys;
                if (Alpha) { alphas[sourceIndex].time = time; alphas[sourceIndex].alpha = Mathf.Clamp01(value); }
                else colors[sourceIndex].time = time;
                Gradient.SetKeys(colors, alphas);
                Index = Alpha ? Array.FindIndex(Gradient.alphaKeys, k => Mathf.Abs(k.time - time) < 0.001f && Mathf.Abs(k.alpha - Mathf.Clamp01(value)) < 0.005f) :
                    Array.FindIndex(Gradient.colorKeys, k => Mathf.Abs(k.time - time) < 0.001f && k.color == colors[sourceIndex].color);
            }
        }

        internal void Configure(float time, float value, Color color, float inTangent, float outTangent)
        {
            if (finished) return;
            time = Mathf.Clamp01(time);
            if (Curve != null)
            {
                Move(time, value);
                var key = Curve.keys[Index]; key.inTangent = inTangent; key.outTangent = outTangent; Curve.MoveKey(Index, key);
            }
            else
            {
                var colors = sourceGradient.colorKeys; var alphas = sourceGradient.alphaKeys;
                if (Alpha) { alphas[sourceIndex].time = Mathf.Clamp01(time); alphas[sourceIndex].alpha = Mathf.Clamp01(value); }
                else { colors[sourceIndex].time = Mathf.Clamp01(time); colors[sourceIndex].color = color; }
                Gradient.SetKeys(colors, alphas);
                Index = Alpha ? Array.FindIndex(Gradient.alphaKeys, k => Mathf.Abs(k.time - time) < 0.001f && Mathf.Abs(k.alpha - Mathf.Clamp01(value)) < 0.005f) :
                    Array.FindIndex(Gradient.colorKeys, k => Mathf.Abs(k.time - time) < 0.001f && k.color == color);
            }
        }

        internal void Add(float time, float value)
        {
            if (Curve != null) Index = Curve.AddKey(new Keyframe(Mathf.Clamp01(time), value));
            else if (Alpha)
            {
                var keys = Gradient.alphaKeys; if (keys.Length >= 8) return;
                Array.Resize(ref keys, keys.Length + 1); keys[keys.Length - 1] = new GradientAlphaKey(Mathf.Clamp01(value), Mathf.Clamp01(time));
                Gradient.SetKeys(Gradient.colorKeys, keys);
            }
            else
            {
                var keys = Gradient.colorKeys; if (keys.Length >= 8) return;
                Color color = Gradient.Evaluate(time);
                Array.Resize(ref keys, keys.Length + 1); keys[keys.Length - 1] = new GradientColorKey(color, Mathf.Clamp01(time));
                Gradient.SetKeys(keys, Gradient.alphaKeys);
            }
        }

        internal void Delete()
        {
            if (Curve != null) { if (Curve.length > 1) Curve.RemoveKey(sourceIndex); }
            else if (Alpha)
            {
                var keys = Gradient.alphaKeys; if (keys.Length <= 2) return;
                var list = new System.Collections.Generic.List<GradientAlphaKey>(keys); list.RemoveAt(sourceIndex);
                Gradient.SetKeys(Gradient.colorKeys, list.ToArray());
            }
            else
            {
                var keys = Gradient.colorKeys; if (keys.Length <= 2) return;
                var list = new System.Collections.Generic.List<GradientColorKey>(keys); list.RemoveAt(sourceIndex);
                Gradient.SetKeys(list.ToArray(), Gradient.alphaKeys);
            }
        }

        internal void Cancel() { finished = true; }
        static bool Same<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
            return true;
        }
        internal bool Commit()
        {
            if (finished) return false; finished = true;
            if (!CascadeTimelineAuthoring.Editable(session, Emitter) || EditorJsonUtility.ToJson(Emitter) != originalJson) return false;
            if (Curve != null ? Same(Curve.keys, sourceCurve.keys) && Curve.preWrapMode == sourceCurve.preWrapMode && Curve.postWrapMode == sourceCurve.postWrapMode :
                Same(Gradient.colorKeys, sourceGradient.colorKeys) && Same(Gradient.alphaKeys, sourceGradient.alphaKeys) && Gradient.mode == sourceGradient.mode) return false;
            return CascadeTimelineAuthoring.Apply(session, Emitter, "Edit Particle Parameter Key", so => {
                var field = so.FindProperty(Path);
                if (Curve != null) field.animationCurveValue = Curve; else field.gradientValue = Gradient;
            });
        }
    }
}
