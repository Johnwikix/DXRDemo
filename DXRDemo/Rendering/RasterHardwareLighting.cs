using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>借用共享 LOD0 AS 更新世界缓存和稀疏反射；真实反射运行官方 NRD，再按表面 guides 重建。</summary>
internal sealed unsafe class RasterHardwareLighting : GpuResourceOwner
{
    private readonly ID3D12Device5 _device;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12StateObject _pipeline;
    private readonly ID3D12Resource _table, _constants;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly ID3D12DescriptorHeap[] _heaps = new ID3D12DescriptorHeap[2];
    private readonly ComputePass _resolve, _sample, _upscale;
    private readonly int _configuredDivisor;
    private int _divisor;
    internal int SampleStep => _divisor;
    private readonly bool _cachedGi;
    private readonly RasterWorldCache _world;
    internal bool CachedGi => _cachedGi;
    internal ID3D12Resource WorldProbeData => _world.Data;
    internal ID3D12Resource WorldStatistics => _world.Statistics;
    internal uint WorldUpdates => _world.UpdateCount;
    private int _traceWidth, _traceHeight;
    private ID3D12Resource _sampleBase = null!, _sampleNormal = null!, _sampleDepth = null!, _sampleEmission = null!;
    private readonly GpuInterval _traceTimer, _denoiseTimer;
    internal double TraceMilliseconds { get; private set; }
    internal double DenoiseMilliseconds { get; private set; }
    internal void ReadTimings()
    {
        TraceMilliseconds = _traceTimer.ReadMilliseconds();
        DenoiseMilliseconds = _denoiseTimer.ReadMilliseconds();
    }
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
    internal ID3D12Resource? NrdDiffuseInput => _denoiser?.DiffuseInput;
    internal ID3D12Resource? NrdSpecularInput => _denoiser?.SpecularInput;
    internal ID3D12Resource? NrdViewZ => _denoiser?.ViewZ;
    internal ID3D12Resource? NrdMotion => _denoiser?.Motion;
    internal string NrdStatus { get; private set; } = "NRD disabled";

    internal RasterHardwareLighting(DxrDevice device, bool denoise, int divisor, bool cachedGi)
    {
        _device = device.Device;
        _denoise = denoise;
        _configuredDivisor = _divisor = divisor;
        _cachedGi = cachedGi;
        try
        {
            Keep(_sized);
            _world = Keep(new RasterWorldCache(_device));
            _traceTimer = Keep(new GpuInterval(device));
            _denoiseTimer = Keep(new GpuInterval(device));
            var parameters = new List<RootParameter1>
            { new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) };
            for (uint i = 0; i < 8; i++)
                parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(i, 0), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, GpuSceneCache.MaxTextures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler, GpuSceneCache.MaxTextures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 0, 3),
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 3)), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 4), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 4), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(2, 4), ShaderVisibility.All));
            _root = Keep(_device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray())));
            using var compiler = new DxrShaderCompiler();
            // 支持 SM 6.9 时复用 PT 的内联遍历，减少 Hit Lighting 多次 TraceRay 的状态保存；旧设备仍走 6.5。
            bool inline = device.SupportsShaderModel69 && device.Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5).RaytracingTier >= RaytracingTier.Tier1_1;
            compiler.CompileLibrary(Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "RasterHardware.hlsl"),
                useShaderModel69: inline, useSer: inline, reorderThreads: false);
            _pipeline = Keep(_device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(StateObjectType.RaytracingPipeline,
            [new(new DxilLibraryDescription(compiler.DxilBytes, [new("RasterLightingRayGen"), new("WorldProbeRayGen"), new("ClosestHit"), new("AnyHit"), new("Miss")])),
             new(new HitGroupDescription("RasterHitGroup", HitGroupType.Triangles, anyHitShaderImport: "AnyHit", closestHitShaderImport: "ClosestHit")),
             new(new GlobalRootSignature(_root)), new(new RaytracingPipelineConfig(1)), new(new RaytracingShaderConfig(28, 8))])));
            _table = Keep(DxrResources.CreateUploadBuffer(_device, 256));
            using var properties = _pipeline.QueryInterface<ID3D12StateObjectProperties>();
            void* pointer;
            _table.Map(0, null, &pointer).CheckError();
            NativeMemory.Clear(pointer, 256);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("RasterLightingRayGen"), pointer, 32);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("Miss"), (byte*)pointer + 64, 32);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("RasterHitGroup"), (byte*)pointer + 128, 32);
            NativeMemory.Copy((void*)properties.GetShaderIdentifier("WorldProbeRayGen"), (byte*)pointer + 192, 32);
            _table.Unmap(0);
            _constants = Keep(DxrResources.CreateUploadBuffer(_device, 512));
            _stride = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _heap = Keep(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                GpuSceneCache.MaxTextures + 6, DescriptorHeapFlags.ShaderVisible)));
            _heaps[0] = _heap;
            _sample = Keep(new ComputePass(_device, "RasterHardwareSampling.hlsl", "SampleGuides", 4, 4));
            _upscale = Keep(new ComputePass(_device, File.Exists(Path.Combine(AppContext.BaseDirectory,"Shaders","DXR","NRD","NRD.hlsli")) ? "RasterHardwareUpscale.hlsl" : "RasterHardwareUpscaleFallback.hlsl", "Composite", 11, 1));
            _resolve = Keep(new ComputePass(_device, "RasterHardwareResolve.hlsl", "Composite", 7, 1));
        }
        catch { Dispose(); throw; }
    }

    internal void Prepare(int width, int height, ID3D12Resource material, ID3D12Resource normal, ID3D12Resource depth,
        ID3D12Resource emission, ID3D12Resource direct, ID3D12Resource color)
    {
        if (_width == width && _height == height && ReferenceEquals(_boundMaterial, material)) return;
        _denoiser = null;
        _sized.Reset();
        _width = _height = 0;
        _boundMaterial = null;
        int minimumEdge = Math.Min(width,height);
        _divisor = Math.Min(_configuredDivisor, minimumEdge >= 4 ? 4 : minimumEdge >= 2 ? 2 : 1);
        _traceWidth = (width + _divisor - 1) / _divisor;
        _traceHeight = (height + _divisor - 1) / _divisor;
        ID3D12Resource SampleTexture(Format format) => _sized.Current.Keep(DxrResources.CreateUnorderedAccessTexture2D(_device, (uint)_traceWidth, (uint)_traceHeight, format, ResourceStates.NonPixelShaderResource));
        _sampleBase = _divisor == 1 ? material : SampleTexture(Format.R8G8B8A8_UNorm);
        _sampleNormal = _divisor == 1 ? normal : SampleTexture(Format.R16G16B16A16_Float);
        _sampleDepth = _divisor == 1 ? depth : SampleTexture(Format.R32_Float);
        _sampleEmission = _divisor == 1 ? emission : SampleTexture(Format.R16G16B16A16_Float);
        _sample.Srv(3, emission);
        _sample.Srv(0, material); _sample.Srv(1, normal); _sample.Srv(2, depth);
        if (_divisor > 1) { _sample.Uav(0, _sampleBase); _sample.Uav(1, _sampleNormal); _sample.Uav(2, _sampleDepth); _sample.Uav(3, _sampleEmission); }
        _diffuse = _sized.Current.Keep(DxrResources.CreateUnorderedAccessTexture2D(_device, (uint)_traceWidth, (uint)_traceHeight, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource));
        _specular = _sized.Current.Keep(DxrResources.CreateUnorderedAccessTexture2D(_device, (uint)_traceWidth, (uint)_traceHeight, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource));
        CpuDescriptorHandle start = _heap.GetCPUDescriptorHandleForHeapStart() + GpuSceneCache.MaxTextures * _stride;
        _device.CreateShaderResourceView(_sampleBase, null, start);
        _device.CreateShaderResourceView(_sampleNormal, null, start + _stride);
        _device.CreateShaderResourceView(_sampleDepth, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 }
        }, start + 2 * _stride);
        _device.CreateShaderResourceView(_sampleEmission, null, start + 3 * _stride);
        _device.CreateUnorderedAccessView(_diffuse, null, null, start + 4 * _stride);
        _device.CreateUnorderedAccessView(_specular, null, null, start + 5 * _stride);
        _resolve.Srv(0, material); _resolve.Srv(1, normal); _resolve.Srv(2, depth); _resolve.Srv(3, direct);
        _resolve.Srv(6, emission);
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
                    _denoiser = _sized.Current.Keep(new RasterHardwareDenoiser(_device, _traceWidth, _traceHeight, _sampleBase, _sampleNormal, _sampleDepth, _sampleEmission, direct, _diffuse, _specular, color, width, height, _divisor, _cachedGi));
                    NrdStatus = _cachedGi ? "WORLD DIFFUSE CACHE + NRD REFLECTIONS" : "NRD RELAX DIFFUSE + SPECULAR";
                }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                NrdStatus = "NRD fallback: " + error.Message;
            }
        }
        _upscale.Srv(0, material); _upscale.Srv(1, normal); _upscale.Srv(2, depth); _upscale.Srv(3, direct);
        _upscale.Srv(4, _sampleBase); _upscale.Srv(5, _sampleNormal); _upscale.Srv(6, _sampleDepth);
        _upscale.Srv(7, _cachedGi ? _diffuse : _denoiser?.DenoisedDiffuse ?? _diffuse); _upscale.Srv(8, _denoiser?.DenoisedSpecular ?? _specular);
        _upscale.Srv(9, emission); _upscale.Srv(10, _specular);
        _upscale.Uav(0, color);
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
        data[6] = new(environment, scene.EmitterCount, scene.EmitterPower, scene.EmitterGroupCount);
        data[7] = new(MathF.Max(camera.Near * .01f, 1e-6f), Vector3.Distance(scene.Asset.Minimum, scene.Asset.Maximum), 0, scene.HasTransmission ? 1 : 0);
        data[8] = new(sun.Direction, 0); data[9] = new(sun.Radiance, 0);
        data[14] = new(_traceWidth, _traceHeight, _divisor, _denoiser != null ? 1 : 0);
        data[19] = scene.Asset.EnvironmentMap is { } map ? new(map.Width, map.Height, map.Rotation, 0) : default;
        if (_cachedGi) _world.Frame(data, scene, camera, environment, sun);
        _constants.Unmap(0);
        if (_divisor > 1)
        {
            SampleState(commands, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            _sample.Dispatch(commands, _constants, _traceWidth, _traceHeight);
            SampleState(commands, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        }
        commands.ResourceBarrierTransition(_diffuse, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        commands.ResourceBarrierTransition(_specular, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        _traceTimer.Begin(commands);
        if (_cachedGi)
        {
            _world.Begin(commands, _constants);
            BindRays(commands, lease);
            _world.BindTrace(commands);
            commands.DispatchRays(new(new(_table.GPUVirtualAddress + 192, 64), new(_table.GPUVirtualAddress + 64, 64, 64),
                new(_table.GPUVirtualAddress + 128, 64, 64), default, RasterWorldCache.Rays + 1, RasterWorldCache.Budget, 1));
            _world.Publish(commands, _constants);
        }
        BindRays(commands, lease);
        commands.SetComputeRootShaderResourceView(13, _world.Data.GPUVirtualAddress);
        commands.DispatchRays(new(new(_table.GPUVirtualAddress, 64), new(_table.GPUVirtualAddress + 64, 64, 64),
            new(_table.GPUVirtualAddress + 128, 64, 64), default, (uint)_traceWidth, (uint)_traceHeight, 1));
        commands.ResourceBarrierTransition(_diffuse, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(_specular, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        _traceTimer.End(commands);
        _denoiseTimer.Begin(commands);
        if (_denoiser != null)
            _denoiser.Record(commands, camera, previous, jitter, previousJitter, frame, seconds);
        else if (_divisor == 1)
            _resolve.Dispatch(commands, _constants, _width, _height);
        if (_divisor > 1) _upscale.Dispatch(commands, _constants, _width, _height);
        _denoiseTimer.End(commands);
    }

    private void BindRays(ID3D12GraphicsCommandList4 commands, GpuSceneLease lease)
    {
        var scene = lease.Scene;
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
        commands.SetComputeRootShaderResourceView(12, scene.EmitterGroups.GPUVirtualAddress);
        commands.SetPipelineState1(_pipeline);
    }

    private void SampleState(ID3D12GraphicsCommandList4 commands, ResourceStates before, ResourceStates after)
    {
        commands.ResourceBarrierTransition(_sampleBase, before, after);
        commands.ResourceBarrierTransition(_sampleNormal, before, after);
        commands.ResourceBarrierTransition(_sampleDepth, before, after);
        commands.ResourceBarrierTransition(_sampleEmission, before, after);
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
