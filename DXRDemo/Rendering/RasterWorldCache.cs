using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;

namespace DXRDemo.Rendering;

/// <summary>固定预算的滚动世界辐照度缓存；双版本发布，独立于像素数量和窗口尺寸。</summary>
internal sealed class RasterWorldCache : GpuResourceOwner
{
    internal const int SurfaceCapacity = 65536, SurfaceBudget = 2048, SurfaceStride = 48;
    internal const int Count = 16 * 8 * 16, Budget = 256, Rays = 64, Stride = 224;
    private readonly ID3D12Resource[] _probes = new ID3D12Resource[2];
    private readonly ID3D12Resource _rays, _statistics;
    private readonly ID3D12Resource[] _surfaces = new ID3D12Resource[2];
    private readonly ComputePass _clearSurface, _resetBudget;
    private bool _surfacesInitialized;
    private uint _epoch;
    internal ID3D12Resource Statistics => _statistics;
    private readonly ComputePass _initialize;
    private readonly ComputePass[] _blend = new ComputePass[2];
    private GpuScene? _scene;
    private SunLightSettings _sun;
    private float _environment;
    private bool _reset;
    private int _current;
    private uint _frame;
    internal float Spacing { get; private set; }
    internal uint UpdateCount => _frame;
    internal ID3D12Resource Data => _probes[_current];
    internal ulong Bytes => Count * Stride * 2UL + (Rays + 1) * Budget * 32UL + SurfaceCapacity*(ulong)SurfaceStride*2 + 16;

    internal RasterWorldCache(ID3D12Device5 device)
    {
        try
        {
            for (int i = 0; i < 2; i++)
                _probes[i] = Keep(DxrResources.CreateDefaultBuffer(device, Count * Stride, ResourceFlags.AllowUnorderedAccess, ResourceStates.NonPixelShaderResource));
            _rays = Keep(DxrResources.CreateDefaultBuffer(device, (Rays + 1) * Budget * 32, ResourceFlags.AllowUnorderedAccess, ResourceStates.NonPixelShaderResource));
            for (int i=0;i<2;i++) _surfaces[i]=Keep(DxrResources.CreateDefaultBuffer(device,SurfaceCapacity*SurfaceStride,ResourceFlags.AllowUnorderedAccess,ResourceStates.NonPixelShaderResource));
            _statistics=Keep(DxrResources.CreateDefaultBuffer(device,16,ResourceFlags.AllowUnorderedAccess,ResourceStates.UnorderedAccess));
            _clearSurface=Keep(new ComputePass(device,"RasterSurfaceCache.hlsl","Initialize",1,3));
            _clearSurface.BufferUav(0,_surfaces[0],SurfaceStride,SurfaceCapacity); _clearSurface.BufferUav(1,_surfaces[1],SurfaceStride,SurfaceCapacity);
            _resetBudget=Keep(new ComputePass(device,"RasterSurfaceCache.hlsl","ResetBudget",1,3));
            _resetBudget.BufferUav(2,_statistics,4,4);
            _initialize = Keep(new ComputePass(device,"RasterWorldBlend.hlsl","Initialize",2,2));
            _initialize.BufferUav(0,_probes[0],Stride,Count); _initialize.BufferUav(1,_probes[1],Stride,Count);
            for (int i = 0; i < 2; i++)
            {
                var pass = _blend[i] = Keep(new ComputePass(device,"RasterWorldBlend.hlsl","Blend",2,2));
                pass.BufferSrv(0,_probes[i],Stride,Count); pass.BufferSrv(1,_rays,32,(Rays+1)*Budget);
                pass.BufferUav(0,_probes[1-i],Stride,Count);
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>仅场景或灯光变化清空缓存；相机切换/窗口缩放保留已完成的世界格子。</summary>
    internal static Vector4 Grid(SceneAsset asset,in CameraFrame camera)
    {
        Vector3 extent=asset.Maximum-asset.Minimum;
        float spacing=Math.Clamp(MathF.Max(extent.X,MathF.Max(extent.Y,extent.Z))/24,.5f,2);
        return new(MathF.Floor(camera.Origin.X/spacing)-8,MathF.Floor(camera.Origin.Y/spacing)-4,MathF.Floor(camera.Origin.Z/spacing)-8,spacing);
    }
    internal void Frame(Span<Vector4> data, GpuScene scene, in CameraFrame camera, float environment, SunLightSettings sun)
    {
        if (!ReferenceEquals(_scene,scene) || _sun != sun || _environment != environment)
        {
            _epoch = _epoch % 16777214 + 1;
            _reset = true; _frame = 0; _current = 0; _scene = scene; _sun = sun; _environment = environment;
            Spacing = Grid(scene.Asset,camera).W;
        }
        data[10] = Grid(scene.Asset,camera);
        data[11] = new(16,8,16,Count);
        data[12] = new((_frame*Budget)%Count,Budget,Rays,_frame);
        data[13] = new(_reset?1:0,0,MathF.Min(camera.Far,Spacing*64),0);
        data[15] = new(1,_epoch,SurfaceCapacity,SurfaceBudget);
    }

    internal void Begin(ID3D12GraphicsCommandList4 commands, ID3D12Resource constants)
    {
        if (_reset)
        {
            for (int i=0;i<2;i++) commands.ResourceBarrierTransition(_probes[i],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess);
            _initialize.Dispatch(commands,constants,Count,1);
            for (int i=0;i<2;i++) commands.ResourceBarrierTransition(_probes[i],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
            _reset=false;
        }
        if (!_surfacesInitialized)
        {
            for(int i=0;i<2;i++) commands.ResourceBarrierTransition(_surfaces[i],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess);
            _clearSurface.Dispatch(commands,constants,SurfaceCapacity,1);
            for(int i=0;i<2;i++) commands.ResourceBarrierTransition(_surfaces[i],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
            _surfacesInitialized=true;
        }
        _resetBudget.Dispatch(commands,constants,4,1);
        commands.ResourceBarrierUnorderedAccessView(_statistics);
        commands.ResourceBarrierTransition(_surfaces[_current],ResourceStates.NonPixelShaderResource,ResourceStates.CopySource);
        commands.ResourceBarrierTransition(_surfaces[1-_current],ResourceStates.NonPixelShaderResource,ResourceStates.CopyDest);
        commands.CopyResource(_surfaces[1-_current],_surfaces[_current]);
        commands.ResourceBarrierTransition(_surfaces[_current],ResourceStates.CopySource,ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(_surfaces[1-_current],ResourceStates.CopyDest,ResourceStates.UnorderedAccess);
        var next=_probes[1-_current];
        commands.ResourceBarrierTransition(Data,ResourceStates.NonPixelShaderResource,ResourceStates.CopySource);
        commands.ResourceBarrierTransition(next,ResourceStates.NonPixelShaderResource,ResourceStates.CopyDest);
        commands.CopyResource(next,Data);
        commands.ResourceBarrierTransition(Data,ResourceStates.CopySource,ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(next,ResourceStates.CopyDest,ResourceStates.UnorderedAccess);
        commands.ResourceBarrierTransition(_rays,ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess);
    }
    internal void BindTrace(ID3D12GraphicsCommandList4 commands)
    {
        commands.SetComputeRootShaderResourceView(13,Data.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(14,_rays.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(15,_surfaces[_current].GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(16,_surfaces[1-_current].GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(17,_statistics.GPUVirtualAddress);
    }
    internal void Publish(ID3D12GraphicsCommandList4 commands, ID3D12Resource constants)
    {
        commands.ResourceBarrierTransition(_rays,ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
        _blend[_current].Dispatch(commands,constants,Budget,1);
        commands.ResourceBarrierTransition(_probes[1-_current],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(_surfaces[1-_current],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
        _current=1-_current;
        _frame++;
    }
}
