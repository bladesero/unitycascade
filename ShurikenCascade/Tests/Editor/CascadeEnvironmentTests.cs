using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void EnvironmentNativePropertiesAreEditable(bool useSource)
        {
            var source = ScriptableObject.CreateInstance<VolumeProfile>();
            var sourceColor = source.Add<ColorAdjustments>();
            source.hideFlags = sourceColor.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                using (var environment = new CascadePreviewEnvironment())
                {
                    environment.SetSource(useSource ? source : null);
                    using (var serialized = new SerializedObject(environment.Profile))
                        Assert.IsTrue(serialized.FindProperty("components").editable,
                            "Native Profile controls must be editable, not merely writable through code.");
                    foreach (var component in environment.Profile.components)
                    using (var serialized = new SerializedObject(component))
                    {
                        Assert.IsTrue(serialized.FindProperty("active").editable, component.GetType().Name);
                        Assert.AreEqual(HideFlags.DontSave, component.hideFlags & HideFlags.DontSave);
                    }
                    environment.Profile.TryGet<ColorAdjustments>(out var color);
                    using (var serialized = new SerializedObject(color))
                    {
                        Assert.IsTrue(serialized.FindProperty("postExposure.m_OverrideState").editable);
                        Assert.IsTrue(serialized.FindProperty("postExposure.m_Value").editable);
                    }
                    Assert.AreEqual(HideFlags.HideAndDontSave, source.hideFlags);
                    Assert.AreEqual(HideFlags.HideAndDontSave, sourceColor.hideFlags);
                }
            }
            finally { Object.DestroyImmediate(sourceColor); Object.DestroyImmediate(source); }
        }

        [Test]
        public void EnvironmentProfileIsDetachedReloadableAndDisposed()
        {
            var source = ScriptableObject.CreateInstance<VolumeProfile>();
            var color = source.Add<ColorAdjustments>(); color.postExposure.Override(2);
            AssetDatabase.CreateAsset(source, folder + "/Preview.asset");
            AssetDatabase.AddObjectToAsset(color, source); AssetDatabase.SaveAssets();
            var disk = File.ReadAllBytes(folder + "/Preview.asset");
            VolumeProfile clone; VolumeComponent component;
            using (var environment = new CascadePreviewEnvironment())
            {
                environment.SetSource(source); clone = environment.Profile; component = clone.components[0];
                Assert.AreNotSame(source, clone); Assert.AreNotSame(color, component);
                Assert.IsFalse(EditorUtility.IsPersistent(component));
                ((ColorAdjustments)component).postExposure.value = -3;
                Assert.AreEqual(2, color.postExposure.value); Assert.IsTrue(environment.PollChanges());
                Assert.IsFalse(environment.PollChanges());
                environment.SetSource(source); Assert.IsFalse(clone); Assert.IsFalse(component);
                clone = environment.Profile; component = clone.components[0];
                Assert.AreEqual(2, ((ColorAdjustments)component).postExposure.value);
            }
            Assert.IsFalse(clone); Assert.IsFalse(component);
            CollectionAssert.AreEqual(disk, File.ReadAllBytes(folder + "/Preview.asset"));
            Assert.IsFalse(session.Dirty);
        }

        [Test]
        public void EnvironmentStackBlendsOverridesAndResetsWithoutSceneVolumes()
        {
            var manager = VolumeManager.instance; var original = manager.stack;
            var global = new GameObject("Scene Volume");
            var sceneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            sceneProfile.Add<ColorAdjustments>().postExposure.Override(9);
            var volume = global.AddComponent<Volume>(); volume.isGlobal = true; volume.sharedProfile = sceneProfile;
            try
            {
                using (var environment = new CascadePreviewEnvironment())
                {
                    environment.SetSource(null); environment.Enabled = true; environment.Weight = 0.5f;
                    environment.Profile.TryGet<ColorAdjustments>(out var color); color.postExposure.Override(4);
                    environment.NotifyChanged();
                    var stack = environment.PrepareStack(); Assert.AreNotSame(original, stack);
                    Assert.AreEqual(2, stack.GetComponent<ColorAdjustments>().postExposure.value, 0.001f);
                    color.postExposure.overrideState = false; environment.NotifyChanged();
                    Assert.AreEqual(0, environment.PrepareStack().GetComponent<ColorAdjustments>().postExposure.value);
                    color.postExposure.Override(4); color.active = false; environment.NotifyChanged();
                    Assert.AreEqual(0, environment.PrepareStack().GetComponent<ColorAdjustments>().postExposure.value);
                    color.active = true; environment.Enabled = false; environment.NotifyChanged();
                    Assert.AreEqual(0, environment.PrepareStack().GetComponent<ColorAdjustments>().postExposure.value);
                }
                Assert.AreSame(original, manager.stack);
            }
            finally
            {
                Object.DestroyImmediate(global);
                foreach (var c in sceneProfile.components) Object.DestroyImmediate(c);
                Object.DestroyImmediate(sceneProfile);
            }
        }

        [Test]
        public void EnvironmentChangesDoNotRestartSimulationDirtyPrefabOrAddUndo()
        {
            byte[] disk = File.ReadAllBytes(path); int undo = Undo.GetCurrentGroup();
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root); preview.RequestSeekFrame(30); CompleteSeek(preview);
                var particles = CaptureParticles(preview);
                preview.Background = Color.blue;
                preview.Environment.SetSource(null); preview.Environment.Enabled = true;
                preview.Environment.Profile.TryGet<ColorAdjustments>(out var color); color.postExposure.Override(3);
                preview.RefreshEnvironment();
                Assert.AreEqual(30, preview.CurrentFrame); Assert.IsFalse(preview.Playing);
                AssertParticlesEqual(particles, preview); Assert.IsFalse(session.Dirty);
                var profile = preview.Environment.Profile;
                preview.Rebuild(session.Root); Assert.AreSame(profile, preview.Environment.Profile);
            }
            Assert.AreEqual(undo, Undo.GetCurrentGroup()); CollectionAssert.AreEqual(disk, File.ReadAllBytes(path));
        }

        [UnityTest]
        public IEnumerator EnvironmentWindowDrawsOverridesAndClosesWithOwner()
        {
            var oldPipeline = GraphicsSettings.defaultRenderPipeline; var oldQuality = QualitySettings.renderPipeline;
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            var owner = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            CascadeEnvironmentWindow settings = null;
            try
            {
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                yield return null;
                owner.Show(); owner.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path)); owner.Preview.Playing = false;
                CascadeEnvironmentWindow.Open(owner, owner.Preview);
                settings = Resources.FindObjectsOfTypeAll<CascadeEnvironmentWindow>().Single();
                yield return null;
                DrawTestFrame(settings);
                var nativeEditor = settings.ProfileEditor;
                Assert.AreEqual("UnityEditor.Rendering.VolumeProfileEditor", nativeEditor.GetType().FullName);
                Assert.AreSame(owner.Preview.Environment.Profile, nativeEditor.target);
                NativeVolumeAction(NativeVolumeList(nativeEditor), "ExpandComponents");
                DrawTestFrame(settings);
                owner.Preview.Environment.SetSource(null); DrawTestFrame(settings);
                Assert.IsFalse(nativeEditor, "Replacing the preview must dispose its native editors first.");
                Assert.AreSame(owner.Preview.Environment.Profile, settings.ProfileEditor.target);
                owner.Preview.RequestSeekFrame(12); CompleteSeek(owner.Preview);
                owner.Preview.Environment.Profile.TryGet<ColorAdjustments>(out var previewColor);
                Undo.IncrementCurrentGroup();
                using (var serialized = new SerializedObject(previewColor))
                {
                    serialized.FindProperty("postExposure.m_Value").floatValue = 4;
                    serialized.ApplyModifiedProperties();
                }
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                yield return null;
                DrawTestFrame(settings);
                Assert.AreEqual(0, previewColor.postExposure.value);
                Assert.AreEqual(12, owner.Preview.CurrentFrame, "Volume Undo must not restart particle simulation.");
                Assert.IsFalse(owner.hasUnsavedChanges);
                nativeEditor = settings.ProfileEditor;
                owner.Close(); yield return null;
                Assert.IsFalse(settings);
                Assert.IsFalse(nativeEditor);
            }
            finally
            {
                if (settings) settings.Close(); if (owner) owner.Close();
                GraphicsSettings.defaultRenderPipeline = oldPipeline; QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(pipeline); Object.DestroyImmediate(renderer);
            }
        }

        [Test]
        public void EnvironmentNativeInspectorEditsResetsRemovesAndUndoesWithoutSavingAssets()
        {
            var source = ScriptableObject.CreateInstance<VolumeProfile>();
            var color = source.Add<ColorAdjustments>(); color.postExposure.Override(2);
            string assetPath = folder + "/NativePreview.asset";
            AssetDatabase.CreateAsset(source, assetPath);
            AssetDatabase.AddObjectToAsset(color, source); AssetDatabase.SaveAssets();
            var disk = File.ReadAllBytes(assetPath);
            // An unrelated pending asset edit detects an accidental global SaveAssets from a menu.
            color.postExposure.value = 6; EditorUtility.SetDirty(color);
            using (var environment = new CascadePreviewEnvironment())
            {
                environment.SetSource(source);
                var editor = Editor.CreateEditor(environment.Profile);
                try
                {
                    Assert.AreEqual("UnityEditor.Rendering.VolumeProfileEditor", editor.GetType().FullName);
                    object list = NativeVolumeList(editor);
                    var children = (System.Collections.IList)list.GetType().GetField("m_Editors", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list);
                    var nativeColor = (Editor)children[0];
                    Assert.AreEqual("UnityEditor.Rendering.VolumeComponentEditor", nativeColor.GetType().FullName);
                    Undo.IncrementCurrentGroup();
                    nativeColor.serializedObject.Update();
                    nativeColor.serializedObject.FindProperty("postExposure.m_Value").floatValue = -3;
                    nativeColor.serializedObject.ApplyModifiedProperties();
                    Undo.FlushUndoRecordObjects();
                    Assert.AreEqual(-3, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);
                    Assert.IsTrue(environment.PollChanges());
                    Undo.PerformUndo();
                    Assert.AreEqual(6, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);
                    Undo.PerformRedo();
                    Assert.AreEqual(-3, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);

                    Undo.IncrementCurrentGroup();
                    NativeVolumeAction(list, "ResetComponent", typeof(ColorAdjustments), 0);
                    Undo.FlushUndoRecordObjects();
                    Assert.AreEqual(0, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);
                    Assert.IsFalse(EditorUtility.IsPersistent(environment.Profile.components[0]));
                    Assert.AreEqual(HideFlags.DontSave, environment.Profile.components[0].hideFlags & HideFlags.DontSave);
                    Undo.PerformUndo();
                    Assert.AreEqual(-3, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);
                    Undo.PerformRedo();
                    Assert.AreEqual(0, ((ColorAdjustments)environment.Profile.components[0]).postExposure.value);

                    // Rebuild the native cache as OnGUI would after Undo/Redo.
                    NativeVolumeAction(list, "RefreshEditors");
                    Undo.IncrementCurrentGroup();
                    NativeVolumeAction(list, "RemoveComponent", 0);
                    Undo.FlushUndoRecordObjects();
                    Assert.IsEmpty(environment.Profile.components);
                    Undo.PerformUndo();
                    Assert.AreEqual(1, environment.Profile.components.Count);
                    Assert.IsTrue(environment.Profile.components[0]);
                    NativeVolumeAction(list, "RefreshEditors");
                    Undo.IncrementCurrentGroup();
                    NativeVolumeAction(list, "AddComponent", typeof(Bloom));
                    Assert.AreEqual("UnityEditor.Rendering.Universal.BloomEditor", ((Editor)children[1]).GetType().FullName);
                    Undo.FlushUndoRecordObjects();
                    Assert.IsTrue(environment.Profile.Has<Bloom>());
                    Undo.PerformUndo();
                    Assert.IsFalse(environment.Profile.Has<Bloom>());
                    Undo.PerformRedo();
                    Assert.IsTrue(environment.Profile.Has<Bloom>());
                    Assert.AreEqual(6, color.postExposure.value);
                    CollectionAssert.AreEqual(disk, File.ReadAllBytes(assetPath));
                    Assert.IsFalse(session.Dirty);
                    LogAssert.NoUnexpectedReceived();
                }
                finally { Object.DestroyImmediate(editor); }
            }
        }

        static void NativeVolumeAction(object list, string method, params object[] arguments)
        {
            list.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, arguments);
        }

        static object NativeVolumeList(Editor editor)
        {
            return editor.GetType().GetField("m_ComponentList", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
        }

        [Test]
        public void EnvironmentBackgroundChangesRenderedPixelsWithoutAdvancingTime()
        {
            Texture2D red = null, blue = null;
            try
            {
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root); preview.Playing = false;
                    preview.Background = Color.red; red = preview.RenderSnapshot(80, 80);
                    preview.Background = Color.blue; blue = preview.RenderSnapshot(80, 80);
                    Assert.Greater(red.GetPixel(40, 40).r, 0.8f); Assert.Less(red.GetPixel(40, 40).b, 0.1f);
                    Assert.Greater(blue.GetPixel(40, 40).b, 0.8f); Assert.Less(blue.GetPixel(40, 40).r, 0.1f);
                    Assert.AreEqual(0, preview.CurrentFrame); Assert.IsFalse(preview.Playing); Assert.IsFalse(session.Dirty);
                }
            }
            finally { if (red) Object.DestroyImmediate(red); if (blue) Object.DestroyImmediate(blue); }
        }

        [UnityTest]
        public IEnumerator EnvironmentUrpRenderChangesPixelsAndRestoresCameraAndVolumeStack()
        {
            var oldPipeline = GraphicsSettings.defaultRenderPipeline; var oldQuality = QualitySettings.renderPipeline;
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(UniversalRenderPipelineAsset.packagePath + "/Runtime/Data/PostProcessData.asset");
            Assert.NotNull(renderer.postProcessData);
            ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            Texture2D plain = null, processed = null, restored = null;
            try
            {
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                yield return null;
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root); preview.Playing = false;
                    preview.Background = new Color(0.5f, 0.5f, 0.5f, 1);
                    plain = preview.RenderSnapshot(160, 100);
                    var originalStack = VolumeManager.instance.stack;
                    var camera = preview.HandleCameraObject; var type = camera.cameraType; var fov = camera.fieldOfView;
                    preview.Environment.SetSource(null); preview.Environment.Enabled = true;
                    preview.Environment.Profile.TryGet<ColorAdjustments>(out var color); color.postExposure.Override(-4);
                    processed = preview.RenderSnapshot(160, 100);
                    Assert.AreSame(originalStack, VolumeManager.instance.stack); Assert.AreEqual(type, camera.cameraType);
                    Assert.AreEqual(fov, camera.fieldOfView); Assert.AreEqual(0, preview.CurrentFrame);
                    Assert.Greater(plain.GetPixel(80, 50).grayscale - processed.GetPixel(80, 50).grayscale, 0.1f,
                        "Exposure must visibly affect the rendered preview, not only the Volume stack.");
                    preview.Environment.Enabled = false;
                    restored = preview.RenderSnapshot(160, 100);
                    Assert.AreEqual(plain.GetPixel(80, 50).grayscale, restored.GetPixel(80, 50).grayscale, 0.02f);
                    Directory.CreateDirectory("EnvironmentEvidence");
                    File.WriteAllBytes("EnvironmentEvidence/plain.png", plain.EncodeToPNG());
                    File.WriteAllBytes("EnvironmentEvidence/postprocessed.png", processed.EncodeToPNG());
                    Assert.IsFalse(session.Dirty);
                }
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldPipeline; QualitySettings.renderPipeline = oldQuality;
                if (plain) Object.DestroyImmediate(plain); if (processed) Object.DestroyImmediate(processed); if (restored) Object.DestroyImmediate(restored);
                Object.DestroyImmediate(pipeline); Object.DestroyImmediate(renderer);
            }
            yield return null;
        }
    }
}
