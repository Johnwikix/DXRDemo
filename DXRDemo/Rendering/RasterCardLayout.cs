using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Scene;

namespace DXRDemo.Rendering;

/// <summary>保存共享 mesh 的局部 Card 布局边界；每轴允许四个深度区域以覆盖凹面。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RasterCardBounds
{
    internal Vector4 Minimum, Maximum;

    /// <summary>在场景上传线程生成布局，复用源 mesh，取消不会发布部分结果。</summary>
    internal static RasterCardBounds[] Build(SceneAsset asset, CancellationToken cancellation)
    {
        var result = new RasterCardBounds[asset.Meshes.Length];
        for (int mesh = 0; mesh < result.Length; mesh++)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            var vertices = asset.Meshes[mesh].Vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                Vector3 p = new(vertices[i].Position.X, vertices[i].Position.Y, vertices[i].Position.Z);
                minimum = Vector3.Min(minimum,p); maximum = Vector3.Max(maximum,p);
            }
            bool valid = vertices.Length > 0 && asset.Meshes[mesh].Indices.Length is > 0 and < 50331645 &&
                float.IsFinite(minimum.X+minimum.Y+minimum.Z+maximum.X+maximum.Y+maximum.Z);
            result[mesh] = valid ? new() { Minimum=new(minimum,1), Maximum=new(maximum,0) } : default;
        }
        return result;
    }
}

/// <summary>同 HLSL CardData 的 80 字节布局；几何与光照版本分离，实例间不共享光照 tile。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RasterCardData
{
    internal uint Instance, Mesh, Axis, Generation;
    internal Vector4 Center, U, V, N;
}
