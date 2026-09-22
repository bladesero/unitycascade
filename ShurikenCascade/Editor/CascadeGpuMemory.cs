using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ShurikenCascade
{
    internal readonly struct CascadeMemoryEstimate
    {
        internal readonly bool Known;
        internal readonly long Bytes;
        internal readonly string Detail;
        internal CascadeMemoryEstimate(long bytes, string detail) { Known = true; Bytes = bytes; Detail = detail; }
        internal CascadeMemoryEstimate(string reason) { Known = false; Bytes = 0; Detail = reason; }
        internal string Display => Known ? Format(Bytes) : "N/A";
        internal static string Format(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576.0:0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024.0:0.##} KiB" : bytes + " B";
    }

    internal static class CascadeGpuMemory
    {
        internal static long Mips(int width, int height, int depth, int layers, int mips, GraphicsFormat format)
        {
            if (format == GraphicsFormat.None) return 0;
            if (width <= 0 || height <= 0 || depth <= 0 || layers <= 0 || mips <= 0) throw new ArgumentOutOfRangeException();
            long bytes = 0;
            checked
            {
                for (int mip = 0; mip < mips; mip++)
                {
                    bytes += (long)GraphicsFormatUtility.ComputeMipmapSize(width, height, depth, format) * layers;
                    width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); depth = Math.Max(1, depth / 2);
                }
            }
            return bytes;
        }

        internal static CascadeMemoryEstimate Estimate(UnityEngine.Object resource)
        {
            try
            {
                if (resource is RenderTexture rt) return RenderTarget(rt.descriptor);
                if (resource is Texture texture)
                {
                    int layers = 1, depth = 1;
                    if (texture is Texture2DArray array) layers = array.depth;
                    else if (texture is CubemapArray cubes) layers = cubes.cubemapCount * 6;
                    else if (texture is Cubemap) layers = 6;
                    else if (texture is Texture3D volume) depth = volume.depth;
                    else if (!(texture is Texture2D)) return new CascadeMemoryEstimate("不支持的纹理类型");
                    if (texture.graphicsFormat == GraphicsFormat.None) return new CascadeMemoryEstimate("没有可读取的 GPU 格式");
                    long bytes = Mips(texture.width, texture.height, depth, layers, texture.mipmapCount, texture.graphicsFormat);
                    return new CascadeMemoryEstimate(bytes, $"{texture.width}×{texture.height}×{depth} · {layers} 层/面 · {texture.graphicsFormat} · {texture.mipmapCount} Mip");
                }
                if (resource is Mesh mesh)
                {
                    long stride = 0, indices = 0;
                    for (int i = 0; i < mesh.vertexBufferCount; i++) stride += mesh.GetVertexBufferStride(i);
                    for (int i = 0; i < mesh.subMeshCount; i++) indices = Math.Max(indices, (long)mesh.GetIndexStart(i) + mesh.GetIndexCount(i));
                    return new CascadeMemoryEstimate(checked(stride * mesh.vertexCount + indices * (mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2)),
                        $"{mesh.vertexCount:N0} 顶点 · {stride} B/顶点 · {indices:N0} 索引 · {mesh.indexFormat}");
                }
                return new CascadeMemoryEstimate("不估算此资源的 GPU 分配");
            }
            catch (Exception ex) { return new CascadeMemoryEstimate(ex.Message); }
        }

        internal static CascadeMemoryEstimate RenderTarget(RenderTextureDescriptor d)
        {
            try
            {
                int depth = d.dimension == TextureDimension.Tex3D ? d.volumeDepth : 1;
                int layers = d.dimension == TextureDimension.Cube ? 6 : d.dimension == TextureDimension.CubeArray || d.dimension == TextureDimension.Tex2DArray ? d.volumeDepth : 1;
                int mips = d.useMipMap ? d.mipCount > 0 ? d.mipCount : 1 + (int)Math.Floor(Math.Log(Math.Max(Math.Max(d.width, d.height), depth), 2)) : 1;
                int samples = Math.Max(1, d.msaaSamples);
                long color = Mips(d.width, d.height, depth, layers, mips, d.graphicsFormat);
                // An MSAA color attachment can also own a single-sample resolve surface.
                long colorBytes = checked(color * samples + (samples > 1 && !d.bindMS ? color : 0));
                long depthBytes = checked(Mips(d.width, d.height, depth, layers, 1, d.depthStencilFormat) * samples);
                return new CascadeMemoryEstimate(checked(colorBytes + depthBytes),
                    $"{d.width}×{d.height} · {layers} 层/面 · {mips} Mip · MSAA {samples} · 颜色 {CascadeMemoryEstimate.Format(colorBytes)} / 深度 {CascadeMemoryEstimate.Format(depthBytes)}");
            }
            catch (Exception ex) { return new CascadeMemoryEstimate(ex.Message); }
        }
    }
}
