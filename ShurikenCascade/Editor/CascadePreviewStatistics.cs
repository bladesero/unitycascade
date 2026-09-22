using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    internal sealed class CascadeTimingSamples
    {
        readonly double[] values = new double[120];
        int cursor;
        double sum;
        internal int Count { get; private set; }
        internal double Mean => Count == 0 ? 0 : sum / Count;
        internal double Peak { get { double result = 0; for (int i = 0; i < Count; i++) result = Math.Max(result, values[i]); return result; } }
        internal void Add(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            sum -= values[cursor]; values[cursor] = milliseconds; sum += milliseconds;
            cursor = (cursor + 1) % values.Length;
            Count = Math.Min(Count + 1, values.Length);
        }
        internal void Clear() { Array.Clear(values, 0, values.Length); cursor = Count = 0; sum = 0; }
    }

    internal sealed class CascadePreviewStatistics
    {
        internal struct EmitterSnapshot
        {
            internal string Key, Name;
            internal int Particles, Peak, MaxParticles, Materials, CpuSamples;
            internal bool Drawable, EventDriven, GeometryKnown, Mesh, Trails;
            internal long Vertices, Triangles;
            internal double CpuMean, CpuPeak;
        }

        sealed class Entry
        {
            internal ParticleSystem System;
            internal ParticleSystemRenderer Renderer;
            internal string Key;
            internal int Count, Peak, MaterialCount;
            internal bool EventDriven, Known;
            internal long Vertices, Triangles;
            internal Material[] Materials = Array.Empty<Material>();
            internal readonly CascadeTimingSamples Cpu = new CascadeTimingSamples();
        }

        Entry[] entries = Array.Empty<Entry>();
        EmitterSnapshot[] snapshot = Array.Empty<EmitterSnapshot>();
        IReadOnlyList<EmitterSnapshot> readOnlySnapshot = Array.AsReadOnly(Array.Empty<EmitterSnapshot>());
        readonly Dictionary<ParticleSystem, Entry> lookup = new Dictionary<ParticleSystem, Entry>();
        readonly HashSet<Material> materials = new HashSet<Material>();
        double nextRefresh;
        internal readonly CascadeTimingSamples Simulation = new CascadeTimingSamples();
        internal readonly CascadeTimingSamples Rendering = new CascadeTimingSamples();
        internal IReadOnlyList<EmitterSnapshot> Emitters => readOnlySnapshot;
        internal int ParticleCount { get; private set; }
        internal int ParticlePeak { get; private set; }
        internal int LiveEmitters { get; private set; }
        internal int MaterialCount { get; private set; }
        internal long DrawVertices { get; private set; }
        internal long DrawTriangles { get; private set; }
        internal bool GeometryIncomplete { get; private set; }
        internal bool HasTrails { get; private set; }
        internal int Revision { get; private set; }

        internal void Rebuild(ParticleSystem[] systems, ParticleSystem[] drivers, Transform root)
        {
            Clear();
            var independent = new HashSet<ParticleSystem>(drivers);
            entries = new Entry[systems.Length]; snapshot = new EmitterSnapshot[systems.Length];
            readOnlySnapshot = Array.AsReadOnly(snapshot);
            for (int i = 0; i < systems.Length; i++)
            {
                var p = systems[i];
                entries[i] = new Entry { System = p, Renderer = p.GetComponent<ParticleSystemRenderer>(), Key = CascadeSession.Key(p.transform, root), EventDriven = !independent.Contains(p) };
                lookup[p] = entries[i];
            }
            RefreshMetadata();
        }

        internal void RefreshMetadata()
        {
            foreach (var entry in entries)
            {
                var r = entry.Renderer;
                entry.Materials = r ? r.sharedMaterials : Array.Empty<Material>();
                materials.Clear();
                foreach (var material in entry.Materials) if (material) materials.Add(material);
                if (r && entry.System.trails.enabled && r.trailMaterial) materials.Add(r.trailMaterial);
                entry.MaterialCount = materials.Count;
                entry.Vertices = 4; entry.Triangles = 2; entry.Known = true;
                if (!r || r.renderMode == ParticleSystemRenderMode.None) { entry.Vertices = entry.Triangles = 0; continue; }
                if (!r || r.renderMode != ParticleSystemRenderMode.Mesh) continue;
                entry.Vertices = entry.Triangles = 0;
                var meshes = new Mesh[r.meshCount];
                int count = r.GetMeshes(meshes);
                entry.Known = count > 0;
                for (int i = 0; i < count; i++)
                {
                    var mesh = meshes[i];
                    if (!mesh) { entry.Known = false; continue; }
                    try
                    {
                        long triangles = 0;
                        for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        {
                            if (mesh.GetTopology(sub) != MeshTopology.Triangles) entry.Known = false;
                            else triangles += (long)mesh.GetIndexCount(sub) / 3;
                        }
                        entry.Vertices = Math.Max(entry.Vertices, mesh.vertexCount);
                        entry.Triangles = Math.Max(entry.Triangles, triangles);
                    }
                    catch (UnityException) { entry.Known = false; }
                }
            }
            RefreshSnapshot(true);
        }

        internal void RecordDriver(ParticleSystem driver, double ms)
        {
            if (lookup.TryGetValue(driver, out var entry)) entry.Cpu.Add(ms);
        }

        internal void ObserveParticles()
        {
            ParticleCount = LiveEmitters = 0;
            foreach (var entry in entries)
            {
                entry.Count = entry.System ? entry.System.particleCount : 0;
                entry.Peak = Math.Max(entry.Peak, entry.Count);
                ParticleCount += entry.Count;
                if (entry.Count > 0) LiveEmitters++;
            }
            ParticlePeak = Math.Max(ParticlePeak, ParticleCount);
        }

        internal bool RefreshSnapshot(bool force = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < nextRefresh) return false;
            nextRefresh = now + 0.1;
            DrawVertices = DrawTriangles = 0;
            GeometryIncomplete = HasTrails = false;
            materials.Clear();
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i]; var p = entry.System; var r = entry.Renderer;
                if (!p) continue;
                bool drawable = p.gameObject.activeInHierarchy && r && r.enabled && !r.forceRenderingOff && r.renderMode != ParticleSystemRenderMode.None;
                bool trails = p.trails.enabled;
                snapshot[i] = new EmitterSnapshot {
                    Key = entry.Key, Name = p.name, Particles = entry.Count, Peak = entry.Peak,
                    MaxParticles = p.main.maxParticles, Materials = entry.MaterialCount,
                    Drawable = drawable, EventDriven = entry.EventDriven, GeometryKnown = entry.Known,
                    Mesh = r && r.renderMode == ParticleSystemRenderMode.Mesh, Trails = trails,
                    Vertices = entry.Vertices * entry.Count, Triangles = entry.Triangles * entry.Count,
                    CpuMean = entry.Cpu.Mean, CpuPeak = entry.Cpu.Peak, CpuSamples = entry.Cpu.Count
                };
                foreach (var material in entry.Materials) if (material) materials.Add(material);
                if (r && trails && r.trailMaterial) materials.Add(r.trailMaterial);
                if (!drawable) continue;
                HasTrails |= trails;
                if (!entry.Known) { GeometryIncomplete = true; continue; }
                DrawVertices += snapshot[i].Vertices; DrawTriangles += snapshot[i].Triangles;
            }
            MaterialCount = materials.Count;
            Revision++;
            return true;
        }

        internal void Reset()
        {
            Simulation.Clear(); Rendering.Clear(); ParticlePeak = 0;
            foreach (var entry in entries) { entry.Peak = 0; entry.Cpu.Clear(); }
            ObserveParticles(); RefreshSnapshot(true);
        }

        internal void Clear()
        {
            entries = Array.Empty<Entry>(); snapshot = Array.Empty<EmitterSnapshot>();
            readOnlySnapshot = Array.AsReadOnly(snapshot);
            lookup.Clear(); materials.Clear();
            ParticleCount = ParticlePeak = LiveEmitters = MaterialCount = 0;
            DrawVertices = DrawTriangles = 0;
            GeometryIncomplete = HasTrails = false;
            Simulation.Clear(); Rendering.Clear(); nextRefresh = 0; Revision++;
        }
    }
}
