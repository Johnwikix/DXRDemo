using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.Scene;
using DXRDemo.Rendering;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Float2 = System.Numerics.Vector2;
using Float4 = System.Numerics.Vector4;

namespace DXRDemo.DXR;

/// <summary>Traces indexed, instanced PBR scenes with explicitly maintained DXR shaders.</summary>
internal sealed unsafe class DxrSceneBackend : IDisposable, ISceneUploader
{
    private const int MaxTextures = GpuSceneCache.MaxTextures, OutputCount = 22;
    private readonly DxrDevice _gpu = new();
    private readonly List<IDisposable> _owned = [];
    private readonly DxrShaderTable _table = new();
    private ID3D12RootSignature _root = null!;
    private ID3D12StateObject _pipeline = null!;
    private ID3D12Resource _constants = null!;
    private readonly GpuSceneCache _scenes;
    private readonly bool _ownsScenes;
    private GpuSceneLease? _sceneLease;
    private GpuScene? _scene => _sceneLease?.Scene;
    private ID3D12DescriptorHeap _heap = null!;
    private readonly ID3D12DescriptorHeap[] _descriptorHeaps = new ID3D12DescriptorHeap[2];
    private bool _disposed;
    internal GpuScene? SharedScene => _scene;
    internal RayTracingScene? SharedRayTracing => _sceneLease?.RayTracing;
    internal ID3D12DescriptorHeap OutputHeap => _heap;

    internal DxrSceneBackend() : this(new GpuSceneCache(), true) { }
    internal DxrSceneBackend(GpuSceneCache scenes) : this(scenes, false) { }
    private DxrSceneBackend(GpuSceneCache scenes, bool ownsScenes) { _scenes = scenes; _ownsScenes = ownsScenes; }
    private bool _initialized;
    private int _stride;
    private readonly ID3D12Resource?[] _outputs = new ID3D12Resource?[OutputCount];
    private object? _boundOutput;
    // Structured-buffer ABIs are declared in SceneLighting.hlsli.
    private const int ReservoirStride = 64, PhotonStride = 64, PhotonCount = 131072, PhotonGridSize = 65536;
    private readonly ID3D12Resource?[] _reservoirs = new ID3D12Resource?[2];
    private ID3D12Resource _photons = null!, _photonGrid = null!, _lightingTable = null!;
    private int _reservoirWidth, _reservoirHeight, _reservoirWrite, _previousFrame = -1;
    private Constants _previous;
    private bool _historyValid;
    private bool _photonsValid;
    private Constants _photonFrame;
    internal int PhotonBuildCount { get; private set; }
    internal bool RestirEnabled { get; set; } = true;
    internal bool AdaptiveLightingEnabled { get; set; } = true;
    internal float FireflyThreshold { get; set; } = 64;
    internal bool StablePlanesEnabled { get; set; } = true;
    internal bool StablePlanesActive => _previous.PathSettings.W != 0;
    internal bool CausticsEnabled { get; set; } = true;
    /// <summary>Independent camera samples; disables temporal light reuse and photon estimates.</summary>
    internal bool ReferenceMode { get; set; }
    internal int TraceDispatchCount { get; private set; }
    internal string ShaderLibraryFile { get; init; } = "SceneTrace.hlsl";
    internal bool UseInlineTraversal { get; init; } = true;
    // The monolithic guide/path shader currently spills too much live state at
    // reorder points. Keep the RTXPT SER variant for comparison; inline traversal
    // without reordering is faster on the measured RTX 5070 Ti.
    internal bool ReorderThreads { get; init; }
    internal bool InlineTraversalActive => UseInlineTraversal && _gpu.SupportsShaderModel69;
    internal bool SerActive => InlineTraversalActive && ReorderThreads;
    internal string ActualShaderModel => _gpu.HighestShaderModel.ToString();
    private GpuInterval? _traceTimer;
    internal double TraceGpuMilliseconds { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Origin, Forward, Right, Up, Size, Control, Environment, Limits, SunDirection, SunRadiance;
        public Vector4 PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, PreviousSize;
        public Vector4 Lighting, CausticBounds, CausticSettings, PathSettings, EnvironmentMap;
    }
    internal void Initialize(DxrDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        _scenes.Initialize(device.Device);
        _gpu.Initialize(device.Device);
        var parameters = new List<RootParameter1>
        { new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) };
        for (uint i = 0; i < 7; i++) parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(i, 0), ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 9, 0, 0),
            new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 7, 13, 0),
            new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 6, 20, 0),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaxTextures, 0, 1),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, 0, 2)), ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler, MaxTextures, 0, 1)), ShaderVisibility.All));
        for (uint i = 9; i < 13; i++) parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(i, 0), ShaderVisibility.All));
        parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All));
        _root = _gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray())); _owned.Add(_root);
        using var compiler = new DxrShaderCompiler();
        compiler.CompileLibrary(
            Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", ShaderLibraryFile),
            _gpu.SupportsShaderModel69,
            _gpu.SupportsShaderModel69 && UseInlineTraversal, ReorderThreads);
        _pipeline = _gpu.Device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(StateObjectType.RaytracingPipeline,
        [new(new DxilLibraryDescription(compiler.DxilBytes, [new("RayGen"), new("TransparentRayGen"), new("PhotonGen"), new("ClearPhotonGrid"), new("ClosestHit"), new("AnyHit"), new("Miss")])),
         new(new HitGroupDescription("HitGroup_Sphere", HitGroupType.Triangles, anyHitShaderImport: "AnyHit", closestHitShaderImport: "ClosestHit")),
         new(new GlobalRootSignature(_root)), new(new RaytracingPipelineConfig(1)), new(new RaytracingShaderConfig(28, 8))]));
        _owned.Add(_pipeline);
        using (var props = _pipeline.QueryInterface<ID3D12StateObjectProperties>())
        {
            _table.Build(_gpu.Device, props);
            _lightingTable = DxrResources.CreateUploadBuffer(_gpu.Device, 192); _owned.Add(_lightingTable);
            void* records; _lightingTable.Map(0, null, &records).CheckError(); NativeMemory.Clear(records, 192);
            NativeMemory.Copy((void*)props.GetShaderIdentifier("ClearPhotonGrid"), records, 32);
            NativeMemory.Copy((void*)props.GetShaderIdentifier("PhotonGen"), (byte*)records + 64, 32);
            NativeMemory.Copy((void*)props.GetShaderIdentifier("TransparentRayGen"), (byte*)records + 128, 32);
            _lightingTable.Unmap(0);
        }
        _constants = DxrResources.CreateUploadBuffer(_gpu.Device, 512); _owned.Add(_constants);
        _photons = DxrResources.CreateDefaultBuffer(_gpu.Device, PhotonCount * PhotonStride, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess); _owned.Add(_photons);
        _photonGrid = DxrResources.CreateDefaultBuffer(_gpu.Device, PhotonGridSize * 4, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess); _owned.Add(_photonGrid);
        _stride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        _heap = _gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            MaxTextures + OutputCount + 3, DescriptorHeapFlags.ShaderVisible));
        _owned.Add(_heap);
        _descriptorHeaps[0] = _heap;
        var nullView = new ShaderResourceViewDescription { Format = Format.R8G8B8A8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 } };
        for (int i = 0; i < 3; i++)
            _gpu.Device.CreateShaderResourceView(null, nullView, _heap.GetCPUDescriptorHandleForHeapStart() + (MaxTextures + OutputCount + i) * _stride);
        _initialized = true;
        _traceTimer = new GpuInterval(_gpu); _owned.Add(_traceTimer);
    }

    private void Begin() { _gpu.CommandAllocator.Reset(); _gpu.CommandList.Reset(_gpu.CommandAllocator); }
    private void End() { _gpu.CommandList.Close(); _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList); _gpu.SignalAndWait(); }
    internal void SetScene(SceneAsset asset, CancellationToken cancellation = default)
    {
        using IPreparedScene prepared = PrepareScene(asset, cancellation);
        prepared.Commit();
    }

    IPreparedScene ISceneUploader.PrepareScene(SceneAsset asset, CancellationToken cancellation) => PrepareScene(asset, cancellation);

    private IPreparedScene PrepareScene(SceneAsset asset, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (ReferenceEquals(asset, _scene?.Asset)) return UnchangedScene.Instance;
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new PreparedScene(this, _scenes.Acquire(asset, rayTracing: true, cancellation));
    }

    private sealed class UnchangedScene : IPreparedScene
    {
        internal static readonly UnchangedScene Instance = new();
        public void Commit() { }
        public void Dispose() { }
    }

    private sealed class PreparedScene(DxrSceneBackend owner, GpuSceneLease candidate) : IPreparedScene
    {
        private GpuSceneLease? _candidate = candidate;
        public void Commit()
        {
            GpuSceneLease next = _candidate ?? throw new InvalidOperationException("候选场景已提交或释放。");
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            owner._gpu.Device.CopyDescriptorsSimple(MaxTextures,
                owner._heap.GetCPUDescriptorHandleForHeapStart() + OutputCount * owner._stride,
                next.Scene.TextureViews.GetCPUDescriptorHandleForHeapStart(), DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            owner._sceneLease?.Dispose();
            owner._sceneLease = next;
            owner._descriptorHeaps[1] = next.Scene.Samplers;
            owner._boundOutput = null;
            owner._historyValid = false;
            owner._photonsValid = false;
            _candidate = null;
        }
        public void Dispose() { _candidate?.Dispose(); _candidate = null; }
    }
    internal void Trace(in CameraFrame camera, int frame, int samples, int bounces, Float2 jitter, float environment,
        GpuTexture<Float4> raw, GpuTexture<Rgba32> normal, GpuTexture<Float4> surface,
        GpuTexture<Float4> guide, PbrSignals signals,
        SunLightSettings sun = default, bool rayReconstruction = false, bool resetHistory = false, bool opaqueForNrd = false,
        bool optixGuides = false, float textureLodBias = 0, bool stabilizeGlass = false)
    {
        var scene = _scene ?? throw new InvalidOperationException("Upload scene before tracing.");
        if (samples is < 1 or > 4096 || bounces is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(samples));
        if (!float.IsFinite(FireflyThreshold) || FireflyThreshold < 0) throw new ArgumentOutOfRangeException(nameof(FireflyThreshold));
        if (!float.IsFinite(textureLodBias)) throw new ArgumentOutOfRangeException(nameof(textureLodBias));
        bool stablePlanes = StablePlanesEnabled && opaqueForNrd;
        if (ReferenceMode || stablePlanes) opaqueForNrd = false;
        if (raw.Width != _reservoirWidth || raw.Height != _reservoirHeight)
        {
            _historyValid = false;
            for (int i = 0; i < 2; i++)
            {
                _reservoirs[i]?.Dispose();
                _reservoirs[i] = DxrResources.CreateDefaultBuffer(_gpu.Device, checked((ulong)raw.Width * (ulong)raw.Height * ReservoirStride), ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
            }
            _reservoirWidth = raw.Width; _reservoirHeight = raw.Height;
        }
        if (!ReferenceEquals(raw, _boundOutput))
        {
            _outputs[0] = raw.Resource; _outputs[1] = normal.Resource; _outputs[2] = surface.Resource; _outputs[3] = guide.Resource;
            _outputs[4] = signals.Diffuse.Resource; _outputs[5] = signals.Specular.Resource; _outputs[6] = signals.Albedo.Resource; _outputs[7] = signals.Unfiltered.Resource;
            _outputs[8] = signals.SpecularGuide.Resource;
            _outputs[9] = signals.DiffuseSh.Resource; _outputs[10] = signals.SpecularSh.Resource; _outputs[11] = signals.GlassSurface.Resource;
            _outputs[12] = signals.DiffuseFactor.Resource; _outputs[13] = signals.SpecularFactor.Resource;
            _outputs[14] = signals.GlassFallback.Resource; _outputs[15] = signals.GlassNormal.Resource;
            _outputs[16] = signals.StableReflection.Resource; _outputs[17] = signals.StableReflectionSurface.Resource; _outputs[18] = signals.StableReflectionNormal.Resource;
            _outputs[19] = signals.StableTransmission.Resource; _outputs[20] = signals.StableTransmissionSurface.Resource; _outputs[21] = signals.StableTransmissionNormal.Resource;
            for (int i = 0; i < OutputCount; i++) _gpu.Device.CreateUnorderedAccessView(_outputs[i], null, null, _heap.GetCPUDescriptorHandleForHeapStart() + i * _stride);
            _boundOutput = raw;
        }
        Constants data = new() { Origin = new(camera.Origin, camera.Near), Forward = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f)),
            Right = new(camera.Right, camera.Far), Up = new(camera.Up, 0), Size = new(raw.Width, raw.Height, jitter.X, jitter.Y),
            Control = new(samples, bounces, frame, scene.Asset.Lights.Length), Environment = new(environment, scene.EmitterCount, scene.EmitterPower, (optixGuides ? 1 : 0) | (stabilizeGlass ? 2 : 0)),
            Limits = new(MathF.Max(camera.Near * .01f, 1e-6f), Vector3.Distance(scene.Asset.Minimum, scene.Asset.Maximum), rayReconstruction ? 1 : 0, scene.HasTransmission ? 1 : 0),
            SunDirection = new(sun.Direction, sun.ConeHeight), SunRadiance = new(sun.Radiance, 0),
            PreviousOrigin = _previous.Origin, PreviousForward = _previous.Forward, PreviousRight = _previous.Right, PreviousUp = _previous.Up, PreviousSize = _previous.Size,
            Lighting = new(RestirEnabled && !ReferenceMode ? 1 : 0, 0, 8, 32), CausticBounds = scene.CausticBounds,
            CausticSettings = new(CausticsEnabled && !ReferenceMode && scene.HasRefractiveVolumes && (scene.Asset.Lights.Length > 0 || sun.Radiance.LengthSquared() > 0) ? PhotonCount : 0,
                MathF.Max(scene.CausticBounds.W * .015f, camera.Near * 8), PhotonGridSize, 12),
            PathSettings = new(ReferenceMode ? 1 : 0, AdaptiveLightingEnabled && !ReferenceMode ? 1 : 0, ReferenceMode ? 0 : FireflyThreshold, stablePlanes ? 1 : 0),
            EnvironmentMap = scene.Asset.EnvironmentMap is { } environmentMap ? new(environmentMap.Width, environmentMap.Height, environmentMap.Rotation, 0) : default };
        data.Up.W = opaqueForNrd ? 1 : 0;
        data.EnvironmentMap.W = Math.Clamp(textureLodBias, -8, 0);
        bool compatible = _historyValid && !resetHistory && frame > 0 && frame == _previousFrame + 1 && data.Up.W == _previous.Up.W && data.PathSettings == _previous.PathSettings &&
            data.Environment == _previous.Environment && data.EnvironmentMap == _previous.EnvironmentMap && data.SunDirection == _previous.SunDirection && data.SunRadiance == _previous.SunRadiance &&
            data.Control.X == _previous.Control.X && data.Control.Y == _previous.Control.Y && data.Lighting.X == _previous.Lighting.X &&
            data.CausticSettings == _previous.CausticSettings && data.Forward.W == _previous.Forward.W &&
            Vector3.Dot(camera.Forward, new(_previous.Forward.X, _previous.Forward.Y, _previous.Forward.Z)) > .9f &&
            Vector3.Distance(camera.Origin, new(_previous.Origin.X, _previous.Origin.Y, _previous.Origin.Z)) < data.Limits.Y * .1f;
        data.Lighting.Y = compatible ? 1 : 0;
        void* pointer; _constants.Map(0, null, &pointer).CheckError(); *(Constants*)pointer = data; _constants.Unmap(0);
        Begin(); var cmd = _gpu.CommandList;
        cmd.SetDescriptorHeaps(2, _descriptorHeaps); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(1, _sceneLease!.RayTracing!.Tlas.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(3, scene.Indices.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(4, scene.Instances.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(5, scene.Materials.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(6, scene.Lights.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(7, scene.Emitters.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(14, scene.EnvironmentTexels.GPUVirtualAddress);
        cmd.SetComputeRootDescriptorTable(8, _heap.GetGPUDescriptorHandleForHeapStart()); cmd.SetComputeRootDescriptorTable(9, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
        cmd.SetPipelineState1(_pipeline);
        cmd.SetComputeRootUnorderedAccessView(10, _reservoirs[1 - _reservoirWrite]!.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(11, _reservoirs[_reservoirWrite]!.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(12, _photons.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(13, _photonGrid.GPUVirtualAddress);
        // Static geometry/light photon map is camera-independent. Do not replace it with another
        // sparse random map every frame or on a denoiser/camera-history reset.
        bool rebuildPhotons = data.CausticSettings.X > 0 && (!_photonsValid ||
            data.SunDirection != _photonFrame.SunDirection || data.SunRadiance != _photonFrame.SunRadiance ||
            data.CausticSettings != _photonFrame.CausticSettings || data.Limits.X != _photonFrame.Limits.X ||
            data.Right.W != _photonFrame.Right.W);
        if (rebuildPhotons)
        {
            cmd.DispatchRays(new(new(_lightingTable.GPUVirtualAddress, 64), default, default, default, PhotonGridSize, 1, 1));
            cmd.ResourceBarrierUnorderedAccessView(_photonGrid);
            cmd.DispatchRays(new(new(_lightingTable.GPUVirtualAddress + 64, 64), new(_table.Buffer.GPUVirtualAddress + 64, 64, 64),
                new(_table.Buffer.GPUVirtualAddress + 128, 64, 64), default, PhotonCount, 1, 1));
            cmd.ResourceBarrierUnorderedAccessView(_photons); cmd.ResourceBarrierUnorderedAccessView(_photonGrid);
        }
        _traceTimer!.Begin(cmd);
        cmd.DispatchRays(new(new(_table.Buffer.GPUVirtualAddress, 64), new(_table.Buffer.GPUVirtualAddress + 64, 64, 64),
            new(_table.Buffer.GPUVirtualAddress + 128, 64, 64), default, (uint)raw.Width, (uint)raw.Height, 1));
        foreach (var output in _outputs) cmd.ResourceBarrierUnorderedAccessView(output!);
        cmd.ResourceBarrierUnorderedAccessView(_reservoirs[_reservoirWrite]!);
        _traceTimer.End(cmd);
        End();
        TraceGpuMilliseconds = _traceTimer.ReadMilliseconds();
        if (rebuildPhotons) { _photonFrame = data; _photonsValid = true; PhotonBuildCount++; }
        TraceDispatchCount++;
        _previous = data; _previousFrame = frame; _historyValid = true; _reservoirWrite = 1 - _reservoirWrite;
    }

    /// <summary>Composes glass from the current denoised opaque frame, falling back to traced lighting on cache misses.</summary>
    internal void ComposeTransparent(ID3D12Resource opaqueColor, ID3D12Resource output)
    {
        var scene = _scene ?? throw new InvalidOperationException("Upload scene before composing glass.");
        // .NET 10: 复用原生资源和已上传帧常量；此阶段只改写输出描述符，不分配帧缓冲。
        var start = _heap.GetCPUDescriptorHandleForHeapStart();
        _gpu.Device.CreateUnorderedAccessView(output, null, null, start);
        _gpu.Device.CreateShaderResourceView(opaqueColor, null, start + (MaxTextures + OutputCount) * _stride);
        _gpu.Device.CreateShaderResourceView(_outputs[2], null, start + (MaxTextures + OutputCount + 1) * _stride);
        _gpu.Device.CreateShaderResourceView(_outputs[3], null, start + (MaxTextures + OutputCount + 2) * _stride);
        Begin(); var cmd = _gpu.CommandList;
        cmd.SetDescriptorHeaps(2, _descriptorHeaps); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(1, _sceneLease!.RayTracing!.Tlas.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(3, scene.Indices.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(4, scene.Instances.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(5, scene.Materials.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(6, scene.Lights.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(7, scene.Emitters.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(14, scene.EnvironmentTexels.GPUVirtualAddress);
        cmd.SetComputeRootDescriptorTable(8, _heap.GetGPUDescriptorHandleForHeapStart()); cmd.SetComputeRootDescriptorTable(9, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
        cmd.SetComputeRootUnorderedAccessView(10, _reservoirs[_reservoirWrite]!.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(11, _reservoirs[1 - _reservoirWrite]!.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(12, _photons.GPUVirtualAddress); cmd.SetComputeRootUnorderedAccessView(13, _photonGrid.GPUVirtualAddress);
        cmd.SetPipelineState1(_pipeline);
        cmd.DispatchRays(new(new(_lightingTable.GPUVirtualAddress + 128, 64), new(_table.Buffer.GPUVirtualAddress + 64, 64, 64),
            new(_table.Buffer.GPUVirtualAddress + 128, 64, 64), default, (uint)_reservoirWidth, (uint)_reservoirHeight, 1));
        cmd.ResourceBarrierUnorderedAccessView(output); cmd.ResourceBarrierUnorderedAccessView(_outputs[11]!);
        cmd.ResourceBarrierUnorderedAccessView(_outputs[14]!); cmd.ResourceBarrierUnorderedAccessView(_outputs[15]!);
        End();
        // Restore the opaque output after the glass fence; other UAV bindings are unchanged.
        _gpu.Device.CreateUnorderedAccessView(_outputs[0], null, null, start);
        // The next opaque pass writes these guides as UAVs. Clear borrowed SRVs after the fence,
        // so descriptor-table validation cannot retain an SRV alias across frames or resizes.
        var nullView = new ShaderResourceViewDescription { Format = Format.R32G32B32A32_Float,
            ViewDimension = ShaderResourceViewDimension.Texture2D, Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new() { MipLevels = 1 } };
        for (int i = 0; i < 3; i++) _gpu.Device.CreateShaderResourceView(null, nullView, start + (MaxTextures + OutputCount + i) * _stride);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        void Release(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        try { if (_initialized) _gpu.SignalAndWait(); }
        catch (Exception error) { failure = error; }
        // End 已等待本消费者的每次提交；设备移除或清理异常也不能跳过其他所有者。
        Release(_sceneLease); _sceneLease = null;
        Release(_table);
        foreach (var reservoir in _reservoirs) Release(reservoir);
        for (int i = _owned.Count - 1; i >= 0; i--) Release(_owned[i]);
        _owned.Clear();
        Release(_gpu);
        if (_ownsScenes) Release(_scenes);
        if (failure != null) throw failure;
    }
}
