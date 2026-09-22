using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShurikenCascade.Tests
{
    public partial class CascadeSessionTests
    {
        static void CompleteSeek(CascadePreview preview)
        {
            for (int i = 0; i < 20000 && preview.IsSeeking; i++) preview.Tick(0);
            Assert.IsFalse(preview.IsSeeking, "Seek must eventually complete.");
        }

        static ParticleSystem.Particle[][] CaptureParticles(CascadePreview preview)
        {
            var result = new ParticleSystem.Particle[preview.PreviewSystems.Count][];
            for (int i = 0; i < result.Length; i++)
            {
                var p = preview.PreviewSystems[i];
                result[i] = new ParticleSystem.Particle[p.particleCount]; p.GetParticles(result[i]);
            }
            return result;
        }

        static void AssertParticlesEqual(ParticleSystem.Particle[][] expected, CascadePreview preview)
        {
            var actual = CaptureParticles(preview);
            Assert.AreEqual(expected.Length, actual.Length);
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.AreEqual(expected[i].Length, actual[i].Length, "Emitter " + i);
                for (int j = 0; j < actual[i].Length; j++)
                {
                    Assert.AreEqual(expected[i][j].randomSeed, actual[i][j].randomSeed);
                    Assert.Less(Vector3.Distance(expected[i][j].position, actual[i][j].position), 0.0001f);
                    Assert.Less(Vector3.Distance(expected[i][j].velocity, actual[i][j].velocity), 0.0001f);
                    Assert.AreEqual(expected[i][j].remainingLifetime, actual[i][j].remainingLifetime, 0.0001f);
                }
            }
        }

        [TestCase(false, ParticleSystemSimulationSpace.Local)]
        [TestCase(true, ParticleSystemSimulationSpace.World)]
        public void PlaybackForwardSeekAndBackwardSeekReachIdenticalParticles(bool looping, ParticleSystemSimulationSpace space)
        {
            foreach (var p in session.Emitters)
            {
                var main = p.main; main.duration = 0.8f; main.loop = looping; main.simulationSpace = space;
                main.startDelay = new ParticleSystem.MinMaxCurve(0.1f, 0.3f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 3);
                var emission = p.emission; emission.SetBursts(new[] { new ParticleSystem.Burst(0.1f, 4) { repeatInterval = 0.2f } });
                var trail = p.trails; trail.enabled = true; trail.lifetime = 0.4f;
            }
            string before = EditorJsonUtility.ToJson(session.Emitters[0]);
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                while (preview.CurrentFrame < 90) preview.Tick(1f / 60);
                var expected = CaptureParticles(preview);
                preview.Restart(); preview.RequestSeekFrame(90); CompleteSeek(preview);
                AssertParticlesEqual(expected, preview);
                preview.RequestSeekFrame(160); CompleteSeek(preview);
                preview.RequestSeekFrame(90); CompleteSeek(preview);
                AssertParticlesEqual(expected, preview);
                Assert.IsFalse(preview.Playing);
            }
            Assert.AreEqual(before, EditorJsonUtility.ToJson(session.Emitters[0]));
            Assert.IsFalse(session.Dirty);
        }

        [Test]
        public void SeekReplaysSubEmittersWithoutDoubleSimulation()
        {
            var parent = session.Emitters[0]; var child = session.Emitters[1];
            child.transform.SetParent(parent.transform, false);
            var sub = parent.subEmitters; sub.enabled = true; sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            var emission = parent.emission; emission.rateOverTime = 0; emission.SetBursts(new[] { new ParticleSystem.Burst(0.1f, 1) { repeatInterval = 0.1f } });
            var childEmission = child.emission; childEmission.rateOverTime = 0; childEmission.SetBursts(new[] { new ParticleSystem.Burst(0, 5) { repeatInterval = 0.1f } });
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                while (preview.CurrentFrame < 30) preview.Tick(1f / 60);
                Assert.AreEqual(6, preview.ParticleCount);
                var expected = CaptureParticles(preview);
                preview.RequestSeekFrame(0); CompleteSeek(preview);
                preview.RequestSeekFrame(30); CompleteSeek(preview);
                AssertParticlesEqual(expected, preview);
                preview.Statistics.RefreshSnapshot(true);
                Assert.IsTrue(preview.Statistics.Emitters[1].EventDriven);
                Assert.AreEqual(0, preview.Statistics.Emitters[1].CpuMean);
            }
        }

        [Test]
        public void TimelineStepsRangesLoopingAndCancellationArePreviewOnly()
        {
            int undoGroup = Undo.GetCurrentGroup();
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                preview.Speed = 4;
                preview.StepFrames(1); CompleteSeek(preview); Assert.AreEqual(1, preview.CurrentFrame);
                preview.StepFrames(-1); CompleteSeek(preview); Assert.AreEqual(0, preview.CurrentFrame);
                preview.RequestSeek(0.025); CompleteSeek(preview); Assert.AreEqual(2, preview.CurrentFrame);
                preview.RequestSeekFrame(500); preview.RequestSeekFrame(12); CompleteSeek(preview); Assert.AreEqual(12, preview.CurrentFrame);
                preview.RequestSeekFrame(400); preview.CancelSeek(); preview.Tick(0.1f); Assert.AreEqual(12, preview.CurrentFrame);
                preview.SetRange(60, false, 0, 60);
                preview.Speed = 1; preview.TogglePlaying();
                for (int i = 0; i < 100; i++) preview.Tick(0.1f);
                Assert.AreEqual(60, preview.CurrentFrame); Assert.IsFalse(preview.Playing);
                preview.SetRange(60, true, 20, 30);
                preview.TogglePlaying(); CompleteSeek(preview);
                Assert.AreEqual(20, preview.CurrentFrame); Assert.IsTrue(preview.Playing);
                while (preview.CurrentFrame < 30 && !preview.IsSeeking) preview.Tick(1f / 60);
                Assert.IsTrue(preview.IsSeeking); CompleteSeek(preview);
                Assert.AreEqual(20, preview.CurrentFrame);
                preview.RequestSeekFrame(40); preview.Rebuild(session.Root);
                Assert.AreEqual(0, preview.CurrentFrame); Assert.IsFalse(preview.IsSeeking); Assert.IsFalse(preview.Playing);
            }
            Assert.AreEqual(undoGroup, Undo.GetCurrentGroup());
            Assert.IsFalse(session.Dirty);
        }

        [Test]
        public void LoopReconstructionPreservesEarlierParticlesAndExcludesSeekCpuSamples()
        {
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                while (preview.CurrentFrame < 20) preview.Tick(1f / 60);
                var expected = CaptureParticles(preview);
                int samples = preview.Statistics.Simulation.Count;
                preview.RequestSeekFrame(50); CompleteSeek(preview);
                Assert.AreEqual(samples, preview.Statistics.Simulation.Count);
                preview.SetRange(60, true, 20, 30); preview.TogglePlaying(); CompleteSeek(preview);
                Assert.AreEqual(20, preview.CurrentFrame); AssertParticlesEqual(expected, preview);
                Assert.AreEqual(samples, preview.Statistics.Simulation.Count);
                Assert.GreaterOrEqual(preview.SeekMilliseconds, 0);
                Assert.GreaterOrEqual(preview.Statistics.Simulation.Mean, 0);
                int peak = preview.Statistics.ParticlePeak;
                preview.Restart(); Assert.AreEqual(peak, preview.Statistics.ParticlePeak);
                preview.Statistics.Reset(); Assert.AreEqual(preview.ParticleCount, preview.Statistics.ParticlePeak);
                Assert.AreEqual(0, preview.Statistics.Simulation.Count);
            }
        }

        [Test]
        public void StatisticsCountHiddenParticlesDeduplicateMaterialsAndEstimateMeshUpperBounds()
        {
            var material = new Material(Shader.Find("Particles/Standard Unlit"));
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var p = session.Emitters[0]; var q = session.Emitters[1];
                p.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
                var renderer = q.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = material;
                renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = mesh;
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root); preview.RequestSeek(0.5); CompleteSeek(preview);
                    preview.Statistics.RefreshSnapshot(true);
                    var stats = preview.Statistics;
                    Assert.AreEqual(1, stats.MaterialCount);
                    Assert.AreEqual(stats.Emitters[0].Particles * 4, stats.Emitters[0].Vertices);
                    Assert.AreEqual(stats.Emitters[1].Particles * 3, stats.Emitters[1].Vertices);
                    Assert.AreEqual(stats.Emitters[1].Particles, stats.Emitters[1].Triangles);
                    int total = stats.ParticleCount;
                    preview.Hidden.Add(stats.Emitters[1].Key); preview.ApplyVisibility();
                    Assert.AreEqual(total, stats.ParticleCount);
                    Assert.AreEqual(stats.Emitters[0].Vertices, stats.DrawVertices);
                    Assert.IsFalse(stats.Emitters[1].Drawable);
                    preview.Hidden.Clear(); preview.Solo = stats.Emitters[1].Key; preview.ApplyVisibility();
                    Assert.AreEqual(stats.Emitters[1].Vertices, stats.DrawVertices);
                }
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
        }

        [Test]
        public void TimingSamplesUseOnlyLatest120ValidMeasurements()
        {
            var samples = new CascadeTimingSamples();
            samples.Add(double.NaN); samples.Add(-1); samples.Add(double.PositiveInfinity);
            Assert.AreEqual(0, samples.Count);
            for (int i = 1; i <= 130; i++) samples.Add(i);
            Assert.AreEqual(120, samples.Count); Assert.AreEqual(70.5, samples.Mean); Assert.AreEqual(130, samples.Peak);
            samples.Clear(); Assert.AreEqual(0, samples.Mean); Assert.AreEqual(0, samples.Peak);
        }

        [Test]
        public void PrewarmedTrailStateIsReconstructedAndExplicitSeedsArePreserved()
        {
            var source = session.Emitters[0]; source.useAutoRandomSeed = false; source.randomSeed = 9876;
            var main = source.main; main.loop = true; main.prewarm = true;
            var trails = source.trails; trails.enabled = true; trails.minVertexDistance = 0.001f;
            using (var preview = new CascadePreview())
            {
                preview.Rebuild(session.Root);
                Assert.AreEqual(9876, preview.PreviewSystems[0].randomSeed);
                preview.RequestSeek(1); CompleteSeek(preview);
                var expectedParticles = CaptureParticles(preview);
                var trailData = new ParticleSystem.Trails();
                int expected = preview.PreviewSystems[0].GetTrails(ref trailData);
                Assert.Greater(expected, 0);
                preview.RequestSeek(2); CompleteSeek(preview);
                preview.RequestSeek(1); CompleteSeek(preview);
                AssertParticlesEqual(expectedParticles, preview);
                Assert.AreEqual(expected, preview.PreviewSystems[0].GetTrails(ref trailData));
            }
            Assert.AreEqual(9876, source.randomSeed);
        }

        [Test]
        public void GeometryMetadataRefreshUsesLargestMeshAndExcludesRenderModeNone()
        {
            var triangle = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            var quad = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one }, triangles = new[] { 0, 1, 2, 1, 3, 2 } };
            try
            {
                var renderer = session.Emitters[0].GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.SetMeshes(new[] { triangle, quad });
                session.Emitters[1].GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.None;
                using (var preview = new CascadePreview())
                {
                    preview.Rebuild(session.Root); preview.RequestSeek(1); CompleteSeek(preview);
                    preview.Statistics.RefreshSnapshot(true);
                    var row = preview.Statistics.Emitters[0];
                    Assert.AreEqual(row.Particles * 4, preview.Statistics.DrawVertices);
                    Assert.AreEqual(row.Particles * 2, preview.Statistics.DrawTriangles);
                    Assert.AreEqual(0, preview.Statistics.Emitters[1].Vertices);
                    quad.Clear(); quad.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; quad.triangles = new[] { 0, 1, 2 };
                    preview.Statistics.RefreshMetadata();
                    Assert.AreEqual(row.Particles * 3, preview.Statistics.DrawVertices);
                    preview.PreviewSystems[0].GetComponent<ParticleSystemRenderer>().SetMeshes(new Mesh[] { null });
                    preview.Statistics.RefreshMetadata();
                    Assert.IsFalse(preview.Statistics.Emitters[0].GeometryKnown);
                    Assert.IsTrue(preview.Statistics.GeometryIncomplete);
                }
            }
            finally { Object.DestroyImmediate(triangle); Object.DestroyImmediate(quad); }
        }

        [Test]
        public void LongSeekYieldsAndDisposalCancelsPendingWorkWithoutSceneChanges()
        {
            int scenes = EditorSceneManager.previewSceneCount;
            bool dirty = UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;
            var preview = new CascadePreview();
            try
            {
                preview.Rebuild(session.Root); preview.SetRange(36000, false, 0, 36000);
                preview.RequestSeek(600); preview.Tick(0);
                Assert.IsTrue(preview.IsSeeking, "A long seek must yield between simulation steps.");
                Assert.Greater(preview.CurrentFrame, 0);
                int reached = preview.CurrentFrame;
                preview.CancelSeek(); preview.Tick(0.1f);
                Assert.AreEqual(reached, preview.CurrentFrame);
                preview.RequestSeek(600);
            }
            finally { preview.Dispose(); }
            Assert.IsFalse(preview.IsSeeking);
            Assert.AreEqual(scenes, EditorSceneManager.previewSceneCount);
            Assert.AreEqual(dirty, UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
        }

        [Test]
        public void TrackProjectionUsesSimulationSpeedAndIdentifiesEventDrivenEmitters()
        {
            var p = session.Emitters[0]; var child = session.Emitters[1];
            var main = p.main; main.simulationSpeed = 2; main.duration = 4; main.startDelay = new ParticleSystem.MinMaxCurve(1, 3);
            var sub = p.subEmitters; sub.enabled = true; sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            var tracks = CascadeTimelineTrack.Build(session);
            Assert.AreEqual(0.5f, tracks[0].DelayMin); Assert.AreEqual(1.5f, tracks[0].DelayMax); Assert.AreEqual(2, tracks[0].Duration);
            Assert.IsTrue(tracks[0].UncertainDelay); Assert.IsTrue(tracks[1].EventDriven);
            Assert.AreNotEqual(tracks[0].Key, tracks[1].Key);
            var state = new CascadeTimelineState { EndFrame = -20, StartFrame = 900, StopFrame = -1 };
            using (var preview = new CascadePreview())
            {
                state.Apply(preview);
                Assert.AreEqual(60, preview.EndFrame); Assert.Less(preview.LoopStartFrame, preview.LoopEndFrame);
            }
        }

        [Test]
        public void TimelineAndPerformancePanelsDrawAtSmallAndLargeSizesWithoutDirtyingAssets()
        {
            var errors = new List<string>();
            Application.LogCallback callback = (condition, stack, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(condition); };
            Application.logMessageReceived += callback;
            int sceneCount = EditorSceneManager.previewSceneCount;
            var window = ScriptableObject.CreateInstance<ShurikenCascadeWindow>();
            try
            {
                window.Show(); window.Open(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                foreach (var size in new[] { new Vector2(850, 550), new Vector2(1500, 1000) })
                    for (int expanded = 0; expanded < 2; expanded++)
                    {
                        window.position = new Rect(Vector2.zero, size);
                        using (var so = new SerializedObject(window)) { so.FindProperty("performanceExpanded").boolValue = expanded == 1; so.ApplyModifiedPropertiesWithoutUndo(); }
                        window.SendEvent(new Event { type = EventType.Layout });
                        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) window.SendEvent(new Event { type = EventType.Repaint });
                    }
                Assert.IsFalse(window.hasUnsavedChanges);
                Assert.IsEmpty(errors, string.Join("\n", errors));
            }
            finally { window.DiscardChanges(); window.Close(); Application.logMessageReceived -= callback; }
            Assert.AreEqual(sceneCount, EditorSceneManager.previewSceneCount);
        }
    }
}
