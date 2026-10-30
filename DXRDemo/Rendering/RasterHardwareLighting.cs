using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>借用共享 LOD0 AS 的一跳照明切片；只录制到光栅调用方的同一个命令列表。</summary>
internal sealed unsafe class RasterHardwareLighting : GpuResourceOwner
{
    private readonly ID3D12Device5 _device;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12StateObject _pipeline;
    private readonly ID3D12Resource _table, _constants;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly ID3D12DescriptorHeap[] _heaps = new ID3D12DescriptorHeap[2];
    private readonly ComputePass _resolve;
    private readonly int _stride;
    private readonly SizedResources _sized = new();
    private sealed class Targets : GpuResourceOwner { }
    private sealed class SizedResources : IDisposable
    {
        internal Targets Current = new();
        internal void Reset() { Current.Dispose(); Current = new(); }
        public void Dispose() => Current.Dispose();
    }
    private ID3D12Resource _diffuse = null!, _specular = null!;
    private GpuScene? _scene;
    private ID3D12Resource? _boundMaterial;
    private int _width, _height;
    private readonly bool _denoise;
    private RasterHardwareDenoiser? _denoiser;
    internal bool NrdAvailable { get; private set; }
    internal bool NrdActive => _denoiser != null;
    internal uint NrdDispatchCount => _denoiser?.DispatchCount ?? 0;
    internal uint NrdMaxAccumulatedFrames => _denoiser?.MaxAccumulatedFrames ?? 0;
    internal ID3D12Resource? NrdViewZ => _denoiser?.ViewZ;
    internal ID3D12Resource? NrdMotion => _denoiser?.Motion;
    internal string NrdStatus { get; private set; } = "NRD disabled";

    internal RasterHardwareLighting(DxrDevice device, bool denoise)
    {
        _device = device.Device;
        _denoise = denoise;
        try
        {
            Keep(_sized);
            var parameters = new List<RootParameter1>
            { new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) };
            for (uint i = 0; i < 8; i++)
                parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(i, 0), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, GpuSceneCache.MaxTextures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler, GpuSceneCache.MaxTextures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, 0, 3),
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 3)), ShaderVisibility.All));
            _root = Keep(_device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray())));
            using var compiler = new DxrShaderCompiler();
            // SM 6.5 TraceRay 是 Tier 1.0 基线，不要求内联 RayQuery/SER。
            compiler.CompileLibrary(Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "RasterHardware.hlsl"));
            _pipeline = Keep(_device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(StateObjectType.RaytracingPipeline,
            [new(new DxilLibraryDescription(compiler.DxilBytes, [new("RasterLightingRayGen"), new("ClosestHit"), new("AnyHit"), new("Miss")])),
             new(new HitGroupDescription("RasterHitGroup", HitGroupType.Triangles, anyHitShaderImport: "AnyHit", closestHitShaderImport: "ClosestHit")),
             new(new GlobalRootSignature(_root)), new(new RaytracingPipelineConfig(1)), new(new RaytracingShaderConfig(28, 8))])));
            _table = Keep(DxrResources.CreateUploadBuffer(_device, 192));
            using var properties = _pipeline.QueryInterface<ID3D12StateObjectProperties>();
            void* pointer;
            _table.Map(0, null, &pointer).CheckError();
            NativeMemory.Clear(pointer, 192);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("RasterLightingRayGen"), pointer, 32);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("Miss"), (byte*)pointer + 64, 32);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("RasterHitGroup"), (byte*)pointer + 128, 32);
            _table.Unmap(0);
            _constants = Keep(DxrResources.CreateUploadBuffer(_device, 512));
            _stride = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _heap = Keep(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                GpuSceneCache.MaxTextures + 5, DescriptorHeapFlags.ShaderVisible)));
            _heaps[0] = _heap;
            _resolve = Keep(new ComputePass(_device, "RasterHardwareResolve.hlsl", "Composite", 6, 1));
        }
        catch { Dispose(); throw; }
    }

    internal void Prepare(int width, int height, ID3D12Resource material, ID3D12Resource normal, ID3D12Resource depth,
        ID3D12Resource direct, ID3D12Resource color)
    {
        if (_width == width && _height == height && ReferenceEquals(_boundMaterial, material)) return;
        _denoiser = null;
        _sized.Reset();
        _width = _height = 0;
        _boundMaterial = null;
        _diffuse = _sized.Current.Keep(DxrResources.CreateUnorderedAccessTexture2D(_device, (uint)width, (uint)height, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource));
        _specular = _sized.Current.Keep(DxrResources.CreateUnorderedAccessTexture2D(_device, (uint)width, (uint)height, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource));
        CpuDescriptorHandle start = _heap.GetCPUDescriptorHandleForHeapStart() + GpuSceneCache.MaxTextures * _stride;
        _device.CreateShaderResourceView(material, null, start);
        _device.CreateShaderResourceView(normal, null, start + _stride);
        _device.CreateShaderResourceView(depth, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 }
        }, start + 2 * _stride);
        _device.CreateUnorderedAccessView(_diffuse, null, null, start + 3 * _stride);
        _device.CreateUnorderedAccessView(_specular, null, null, start + 4 * _stride);
        _resolve.Srv(0, material); _resolve.Srv(1, normal); _resolve.Srv(2, depth); _resolve.Srv(3, direct);
        _resolve.Srv(4, _diffuse); _resolve.Srv(5, _specular); _resolve.Uav(0, color);
        if (_denoise)
        {
            try
            {
                NrdAvailable = NativeNrd.Available() != 0;
                if (!NrdAvailable)
                    NrdStatus = Marshal.PtrToStringUTF8(NativeNrd.Diagnostic()) ?? "NRD unavailable";
                else
                {
                    _denoiser = _sized.Current.Keep(new RasterHardwareDenoiser(_device, width, height, material, normal, depth, direct, _diffuse, _specular, color));
                    NrdStatus = "NRD RELAX DIFFUSE + SPECULAR";
                }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                NrdStatus = "NRD fallback: " + error.Message;
            }
        }
        _width = width; _height = height;
        _boundMaterial = material;
    }

    internal void Record(ID3D12GraphicsCommandList4 commands, GpuSceneLease lease, in CameraFrame camera, Vector2 jitter,
        uint frame, float environment, SunLightSettings sun, in CameraFrame previous, Vector2 previousJitter, double seconds)
    {
        var scene = lease.Scene;
        if (!ReferenceEquals(_scene, scene))
        {
            _device.CopyDescriptorsSimple(GpuSceneCache.MaxTextures, _heap.GetCPUDescriptorHandleForHeapStart(), scene.TextureViews.GetCPUDescriptorHandleForHeapStart(), DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _scene = scene;
            _heaps[1] = scene.Samplers;
        }
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        NativeMemory.Clear(pointer, 512);
        var data = new Span<Vector4>(pointer, 20);
        // 与 SceneRayCore.Frame 的 20 个 float4 同步；不借用路径追踪的逐帧状态。
        data[0] = new(camera.Origin, camera.Near);
        data[1] = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f));
        data[2] = new(camera.Right, camera.Far);
        data[3] = new(camera.Up, 0);
        data[4] = new(_width, _height, jitter.X, jitter.Y);
        data[5] = new(1, 1, frame, scene.Asset.Lights.Length);
        data[6] = new(environment, scene.EmitterCount, scene.EmitterPower, 0);
        data[7] = new(MathF.Max(camera.Near * .01f, 1e-6f), Vector3.Distance(scene.Asset.Minimum, scene.Asset.Maximum), 0, scene.HasTransmission ? 1 : 0);
        data[8] = new(sun.Direction, 0); data[9] = new(sun.Radiance, 0);
        data[19] = scene.Asset.EnvironmentMap is { } map ? new(map.Width, map.Height, map.Rotation, 0) : default;
        _constants.Unmap(0);
        commands.ResourceBarrierTransition(_diffuse, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        commands.ResourceBarrierTransition(_specular, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        commands.SetDescriptorHeaps(2, _heaps);
        commands.SetComputeRootSignature(_root);
        commands.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(1, lease.RayTracing!.Tlas.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(3, scene.Indices.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(4, scene.Instances.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(5, scene.Materials.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(6, scene.Lights.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(7, scene.Emitters.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(8, scene.EnvironmentTexels.GPUVirtualAddress);
        commands.SetComputeRootDescriptorTable(9, _heap.GetGPUDescriptorHandleForHeapStart());
        commands.SetComputeRootDescriptorTable(10, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
        commands.SetComputeRootDescriptorTable(11, _heap.GetGPUDescriptorHandleForHeapStart() + GpuSceneCache.MaxTextures * _stride);
        commands.SetPipelineState1(_pipeline);
        commands.DispatchRays(new(new(_table.GPUVirtualAddress, 64), new(_table.GPUVirtualAddress + 64, 64, 64),
            new(_table.GPUVirtualAddress + 128, 64, 64), default, (uint)_width, (uint)_height, 1));
        commands.ResourceBarrierTransition(_diffuse, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(_specular, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        if (_denoiser != null)
            _denoiser.Record(commands, camera, previous, jitter, previousJitter, frame, seconds);
        else
            _resolve.Dispatch(commands, _constants, _width, _height);
    }

    internal void ReleaseSize()
    {
        _denoiser = null;
        _sized.Dispose();
        _width = _height = 0;
        _boundMaterial = null;
        _scene = null;
    }
}
