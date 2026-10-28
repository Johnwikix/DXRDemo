using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Scene;
using Vortice.Direct3D12;

namespace DXRDemo.Rendering;

// 与 SceneLighting.hlsli 的 InstanceData 保持 160 字节布局；两条管线读取同一实例表。
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneInstance
{
    public uint VertexOffset, IndexOffset, Material, CausticCaster;
    public Matrix4x4 World, Normal;
    public Vector4 EmissionSampling;
}

internal abstract class GpuResourceOwner : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    internal T Keep<T>(T value) where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }
    public void Dispose()
    {
        Exception? failure = null;
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception error) { failure ??= error; }
        }
        _owned.Clear();
        if (failure != null)
            throw failure;
    }
}

/// <summary>设备内共享的只读场景资源，不包含任何渲染器的可写输出或历史。</summary>
internal sealed class GpuScene : GpuResourceOwner
{
    internal required SceneAsset Asset;
    internal (int Vertex, int Index)[] MeshOffsets = [];
    internal ID3D12Resource Vertices = null!, Indices = null!, Instances = null!, Materials = null!;
    internal ID3D12Resource Lights = null!, Emitters = null!, EnvironmentTexels = null!;
    // CPU SRV 堆供各管线复制；采样器堆只读，可以直接绑定。
    internal ID3D12DescriptorHeap TextureViews = null!, Samplers = null!;
    internal uint EmitterCount;
    internal float EmitterPower;
    internal bool HasTransmission, HasRefractiveVolumes;
    internal Vector4 CausticBounds;
}

/// <summary>按需构建的 LOD0 BLAS/TLAS；生存期从属于对应的 GpuScene 缓存项。</summary>
internal sealed class RayTracingScene : GpuResourceOwner
{
    internal ID3D12Resource Tlas = null!;
}
