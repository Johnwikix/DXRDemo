using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Float2 = System.Numerics.Vector2;
using Float4 = System.Numerics.Vector4;

namespace DXRDemo.DXR;

/// <summary>Traces indexed, instanced PBR scenes with explicitly maintained DXR shaders.</summary>
internal sealed unsafe class DxrSceneBackend : IDisposable
{
    private const int MaxTextures = 1024, OutputCount = 22;
    private readonly DxrDevice _gpu = new();
    private readonly List<IDisposable> _owned = [];
    private readonly DxrShaderTable _table = new();
    private ID3D12RootSignature _root = null!;
    private ID3D12StateObject _pipeline = null!;
    private ID3D12Resource _constants = null!;
    private SceneGpu? _scene;
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
    private struct InstanceData
    {
        public uint VertexOffset, IndexOffset, Material, CausticCaster;
        public Matrix4x4 World, Normal;
        public Vector4 EmissionSampling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Origin, Forward, Right, Up, Size, Control, Environment, Limits, SunDirection, SunRadiance;
        public Vector4 PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, PreviousSize;
        public Vector4 Lighting, CausticBounds, CausticSettings, PathSettings, EnvironmentMap;
    }
    private sealed class SceneGpu : IDisposable
    {
        internal readonly List<IDisposable> Owned = [];
        internal SceneAsset Asset = null!;
        internal ID3D12Resource Vertices = null!, Indices = null!, Instances = null!, Materials = null!, Lights = null!, Emitters = null!, Tlas = null!;
        internal ID3D12Resource EnvironmentTexels = null!;
        internal ID3D12DescriptorHeap Heap = null!, Samplers = null!;
        internal ID3D12DescriptorHeap[] DescriptorHeaps = [];
        internal uint EmitterCount;
        internal float EmitterPower;
        internal bool HasTransmission, HasRefractiveVolumes;
        internal Vector4 CausticBounds;
        internal T Keep<T>(T value) where T : IDisposable { Owned.Add(value); return value; }
        public void Dispose() { for (int i = Owned.Count - 1; i >= 0; i--) Owned[i].Dispose(); Owned.Clear(); }
    }

    internal void Initialize(DxrDevice device)
    {
        if (_initialized) return;
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
        _initialized = true;
        _traceTimer = new GpuInterval(_gpu); _owned.Add(_traceTimer);
    }

    private void Begin() { _gpu.CommandAllocator.Reset(); _gpu.CommandList.Reset(_gpu.CommandAllocator); }
    private void End() { _gpu.CommandList.Close(); _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList); _gpu.SignalAndWait(); }
    private ID3D12Resource Upload(SceneGpu scene, ReadOnlySpan<byte> bytes)
    {
        using var staging = DxrResources.CreateUploadBuffer(_gpu.Device, (ulong)Math.Max(bytes.Length, 16));
        void* pointer; staging.Map(0, null, &pointer).CheckError();
        NativeMemory.Clear(pointer, (nuint)Math.Max(bytes.Length, 16)); bytes.CopyTo(new Span<byte>(pointer, bytes.Length)); staging.Unmap(0);
        var output = scene.Keep(DxrResources.CreateDefaultBuffer(_gpu.Device, (ulong)Math.Max(bytes.Length, 16), ResourceFlags.None, ResourceStates.CopyDest));
        Begin(); _gpu.CommandList.CopyBufferRegion(output, 0, staging, 0, (ulong)Math.Max(bytes.Length, 16));
        _gpu.CommandList.ResourceBarrierTransition(output, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource); End(); return output;
    }

    internal void SetScene(SceneAsset asset, CancellationToken cancellation = default)
    {
        if (ReferenceEquals(asset, _scene?.Asset)) return;
        if (asset.Textures.Length > MaxTextures) throw new NotSupportedException($"场景贴图数超过 {MaxTextures}。");
        if (asset.Instances.Length == 0) throw new InvalidDataException("所选场景没有可渲染网格。");
        if (asset.Instances.Length > 1 << 24) throw new NotSupportedException("DXR instance IDs are limited to 24 bits.");
        var scene = new SceneGpu { Asset = asset };
        try
        {
            int vertexCount = 0, indexCount = 0;
            var offsets = new (int Vertex, int Index)[asset.Meshes.Length];
            for (int i = 0; i < offsets.Length; i++)
            { offsets[i] = (vertexCount, indexCount); vertexCount = checked(vertexCount + asset.Meshes[i].Vertices.Length); indexCount = checked(indexCount + asset.Meshes[i].Indices.Length); }
            var vertices = new SceneVertex[vertexCount]; var indices = new uint[indexCount];
            for (int i = 0; i < offsets.Length; i++)
            { asset.Meshes[i].Vertices.CopyTo(vertices, offsets[i].Vertex); asset.Meshes[i].Indices.CopyTo(indices, offsets[i].Index); }
            scene.Vertices = Upload(scene, MemoryMarshal.AsBytes(vertices.AsSpan()));
            scene.Indices = Upload(scene, MemoryMarshal.AsBytes(indices.AsSpan()));
            scene.Materials = Upload(scene, MemoryMarshal.AsBytes(asset.Materials.AsSpan()));
            SceneLight[] lights = asset.Lights.Length == 0 ? new SceneLight[1] : asset.Lights;
            scene.Lights = Upload(scene, MemoryMarshal.AsBytes(lights.AsSpan()));
            var blas = new ID3D12Resource[asset.Meshes.Length];
            for (int i = 0; i < blas.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                SceneMesh mesh = asset.Meshes[i];
                SceneMaterial material = asset.Materials[mesh.Material];
                bool opaque = material.Flags.X == 0 && material.Transmission.X == 0;
                var geometry = new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(
                    new GpuVirtualAddressAndStride(scene.Vertices.GPUVirtualAddress + (ulong)offsets[i].Vertex * 80, 80),
                    Format.R32G32B32_Float, (uint)mesh.Vertices.Length, transform3x4: 0,
                    indexBuffer: scene.Indices.GPUVirtualAddress + (ulong)offsets[i].Index * 4,
                    indexFormat: Format.R32_UInt, indexCount: (uint)mesh.Indices.Length),
                    opaque ? RaytracingGeometryFlags.Opaque : RaytracingGeometryFlags.None);
                var inputs = new BuildRaytracingAccelerationStructureInputs { Type = RaytracingAccelerationStructureType.BottomLevel,
                    Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace, DescriptorsCount = 1, Layout = ElementsLayout.Array, GeometryDescriptions = [geometry] };
                blas[i] = BuildAcceleration(scene, inputs);
            }
            var distribution = new SceneLightDistribution(asset, cancellation);
            var instanceData = new InstanceData[asset.Instances.Length];
            var descriptions = new byte[checked(asset.Instances.Length * 64)];
            Vector3 glassMin = new(float.PositiveInfinity), glassMax = new(float.NegativeInfinity);
            for (int i = 0; i < instanceData.Length; i++)
            {
                SceneInstance instance = asset.Instances[i]; var mesh = asset.Meshes[instance.Mesh];
                SceneMaterial material = asset.Materials[mesh.Material];
                scene.HasTransmission |= material.Transmission.X > 0;
                bool causticCaster = CausticGeometry.IsCaster(mesh, material, instance.Transform);
                if (causticCaster)
                {
                    scene.HasRefractiveVolumes = true;
                    foreach (ref readonly SceneVertex vertex in mesh.Vertices.AsSpan())
                    {
                        Vector3 point = Vector3.Transform(new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), instance.Transform);
                        glassMin = Vector3.Min(glassMin, point); glassMax = Vector3.Max(glassMax, point);
                    }
                }
                Matrix4x4.Invert(instance.Transform, out Matrix4x4 inverse);
                instanceData[i] = new() { VertexOffset = (uint)offsets[instance.Mesh].Vertex, IndexOffset = (uint)offsets[instance.Mesh].Index,
                    Material = (uint)mesh.Material, CausticCaster = causticCaster ? 1u : 0u, World = instance.Transform, Normal = Matrix4x4.Transpose(inverse),
                    EmissionSampling = new(distribution.InstanceAreaPdf[i], distribution.InstanceEmitterStart[i] + 1, 0, 0) };
                Span<byte> desc = descriptions.AsSpan(i * 64, 64); Span<float> transform = MemoryMarshal.Cast<byte, float>(desc[..48]);
                Matrix4x4 m = Matrix4x4.Transpose(instance.Transform);
                MemoryMarshal.CreateReadOnlySpan(ref m.M11, 12).CopyTo(transform);
                // Bit 0: ordinary rays. Bit 1: ordered transmission-only shadow rays.
                // Reject opaque instances in TLAS traversal instead of invoking any-hit
                // on every wall, road and piece of furniture for every glass query.
                uint visibilityMask = material.Transmission.X > 0 ? 3u : 5u;
                MemoryMarshal.Write(desc[48..], (uint)i | (visibilityMask << 24));
                // glTF front faces follow the object-space cross-product normal.
                // DXR classifies winding before the instance transform, matching
                // our inverse-transpose normal even for mirrored instances.
                uint instanceFlags = 0;
                if (material.Flags.Z != 0 || material.Transmission.X > 0)
                    instanceFlags |= (uint)RaytracingInstanceFlags.TriangleCullDisable;
                MemoryMarshal.Write(desc[52..], instanceFlags << 24);
                MemoryMarshal.Write(desc[56..], blas[instance.Mesh].GPUVirtualAddress);
            }
            scene.EmitterCount = distribution.TotalPower > 0 ? (uint)distribution.Emitters.Length : 0;
            scene.EmitterPower = distribution.TotalPower;
            if (scene.HasRefractiveVolumes) scene.CausticBounds = new((glassMin + glassMax) * .5f, MathF.Max(Vector3.Distance(glassMin, glassMax) * .501f, 1e-4f));
            scene.Emitters = Upload(scene, MemoryMarshal.AsBytes(distribution.Emitters.AsSpan()));
            EnvironmentDistribution.Texel[] environmentTexels = asset.EnvironmentMap is { } map ? new EnvironmentDistribution(map).Texels : [default];
            scene.EnvironmentTexels = Upload(scene, MemoryMarshal.AsBytes(environmentTexels.AsSpan()));
            scene.Instances = Upload(scene, MemoryMarshal.AsBytes(instanceData.AsSpan()));
            var instanceBuffer = Upload(scene, descriptions);
            scene.Tlas = BuildAcceleration(scene, new BuildRaytracingAccelerationStructureInputs { Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace, DescriptorsCount = (uint)asset.Instances.Length,
                Layout = ElementsLayout.Array, InstanceDescriptions = instanceBuffer.GPUVirtualAddress });
            scene.Heap = scene.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, MaxTextures + OutputCount + 3, DescriptorHeapFlags.ShaderVisible)));
            scene.Samplers = scene.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.Sampler, MaxTextures, DescriptorHeapFlags.ShaderVisible)));
            scene.DescriptorHeaps = [scene.Heap, scene.Samplers];
            var nullView = new ShaderResourceViewDescription { Format = Format.R8G8B8A8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 } };
            int samplerStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
            for (int i = 0; i < MaxTextures; i++)
            {
                _gpu.Device.CreateShaderResourceView(null, nullView, scene.Heap.GetCPUDescriptorHandleForHeapStart() + (i + OutputCount) * _stride);
                var texture = i < asset.Textures.Length ? asset.Textures[i] : null;
                int min = texture?.MinFilter ?? 9987, mag = texture?.MagFilter ?? 9729;
                bool minLinear = min is 9729 or 9985 or 9987, mipLinear = min is 9986 or 9987;
                var samplerDescription = new SamplerDescription { Filter = (Filter)((minLinear ? 16 : 0) | (mag == 9729 ? 4 : 0) | (mipLinear ? 1 : 0)),
                    AddressU = Address(texture?.WrapS ?? 10497), AddressV = Address(texture?.WrapT ?? 10497), AddressW = TextureAddressMode.Wrap,
                    MinLOD = 0, MaxLOD = min is 9728 or 9729 ? 0 : float.MaxValue, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Always };
                _gpu.Device.CreateSampler(ref samplerDescription, scene.Samplers.GetCPUDescriptorHandleForHeapStart() + i * samplerStride);
            }
            for (int i = 0; i < 3; i++)
                _gpu.Device.CreateShaderResourceView(null, nullView, scene.Heap.GetCPUDescriptorHandleForHeapStart() + (MaxTextures + OutputCount + i) * _stride);
            for (int i = 0; i < asset.Textures.Length; i++) { cancellation.ThrowIfCancellationRequested(); UploadTexture(scene, asset.Textures[i], i); }
            cancellation.ThrowIfCancellationRequested();
            // 已完成的 fence 保护旧场景释放；候选场景完全成功后才替换。
            _scene?.Dispose(); _scene = scene; _boundOutput = null; _historyValid = false; _photonsValid = false;
        }
        catch { _gpu.SignalAndWait(); scene.Dispose(); throw; }
    }
    private static TextureAddressMode Address(int wrap) => wrap == 33071 ? TextureAddressMode.Clamp : wrap == 33648 ? TextureAddressMode.Mirror : TextureAddressMode.Wrap;

    private ID3D12Resource BuildAcceleration(SceneGpu scene, BuildRaytracingAccelerationStructureInputs inputs)
    {
        var info = _gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        var result = scene.Keep(DxrResources.CreateDefaultBuffer(_gpu.Device, info.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure));
        using var scratch = DxrResources.CreateDefaultBuffer(_gpu.Device, info.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        Begin(); _gpu.CommandList.BuildRaytracingAccelerationStructure(new(result.GPUVirtualAddress, inputs, 0, scratch.GPUVirtualAddress));
        _gpu.CommandList.ResourceBarrierUnorderedAccessView(result); End(); return result;
    }

    private void UploadTexture(SceneGpu scene, SceneTexture source, int index)
    {
        bool compressed = source.Bc7Mips != null;
        byte[][] mips = source.Bc7Mips ?? source.Mips;
        Format format = compressed ? (source.Srgb ? Format.BC7_UNorm_SRgb : Format.BC7_UNorm) :
            (source.Srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
        var desc = ResourceDescription.Texture2D(format,
            (uint)source.Width, (uint)source.Height, 1, (ushort)mips.Length);
        var texture = scene.Keep(_gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, desc, ResourceStates.CopyDest));
        // Explicit D3D12 row/slice alignment; no temporary allocations in frame rendering.
        ulong total = 0; int w = source.Width, h = source.Height;
        int RowBytes(int width) => compressed ? ((width + 3) / 4) * 16 : width * 4;
        int Rows(int height) => compressed ? (height + 3) / 4 : height;
        for (int i = 0; i < mips.Length; i++) { total = (total + 511) & ~511ul; total += (ulong)((RowBytes(w) + 255) & ~255) * (uint)Rows(h); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        using var staging = DxrResources.CreateUploadBuffer(_gpu.Device, total);
        void* p; staging.Map(0, null, &p).CheckError(); w = source.Width; h = source.Height; ulong offset = 0;
        Begin();
        for (int i = 0; i < mips.Length; i++)
        {
            int rowBytes = RowBytes(w), rows = Rows(h);
            offset = (offset + 511) & ~511ul; uint pitch = (uint)((rowBytes + 255) & ~255);
            for (int y = 0; y < rows; y++) mips[i].AsSpan(y * rowBytes, rowBytes).CopyTo(new Span<byte>((byte*)p + offset + (ulong)y * pitch, rowBytes));
            var footprint = new PlacedSubresourceFootPrint { Offset = offset, Footprint = new SubresourceFootPrint { Format = desc.Format, Width = (uint)w, Height = (uint)h, Depth = 1, RowPitch = pitch } };
            _gpu.CommandList.CopyTextureRegion(new TextureCopyLocation(texture, (uint)i), 0, 0, 0, new TextureCopyLocation(staging, footprint));
            offset += pitch * (uint)rows; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        staging.Unmap(0); _gpu.CommandList.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource); End();
        _gpu.Device.CreateShaderResourceView(texture, null, scene.Heap.GetCPUDescriptorHandleForHeapStart() + (index + OutputCount) * _stride);
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
            for (int i = 0; i < OutputCount; i++) _gpu.Device.CreateUnorderedAccessView(_outputs[i], null, null, scene.Heap.GetCPUDescriptorHandleForHeapStart() + i * _stride);
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
        cmd.SetDescriptorHeaps(2, scene.DescriptorHeaps); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(1, scene.Tlas.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(3, scene.Indices.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(4, scene.Instances.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(5, scene.Materials.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(6, scene.Lights.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(7, scene.Emitters.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(14, scene.EnvironmentTexels.GPUVirtualAddress);
        cmd.SetComputeRootDescriptorTable(8, scene.Heap.GetGPUDescriptorHandleForHeapStart()); cmd.SetComputeRootDescriptorTable(9, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
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
        var start = scene.Heap.GetCPUDescriptorHandleForHeapStart();
        _gpu.Device.CreateUnorderedAccessView(output, null, null, start);
        _gpu.Device.CreateShaderResourceView(opaqueColor, null, start + (MaxTextures + OutputCount) * _stride);
        _gpu.Device.CreateShaderResourceView(_outputs[2], null, start + (MaxTextures + OutputCount + 1) * _stride);
        _gpu.Device.CreateShaderResourceView(_outputs[3], null, start + (MaxTextures + OutputCount + 2) * _stride);
        Begin(); var cmd = _gpu.CommandList;
        cmd.SetDescriptorHeaps(2, scene.DescriptorHeaps); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(1, scene.Tlas.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(3, scene.Indices.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(4, scene.Instances.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(5, scene.Materials.GPUVirtualAddress); cmd.SetComputeRootShaderResourceView(6, scene.Lights.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(7, scene.Emitters.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(14, scene.EnvironmentTexels.GPUVirtualAddress);
        cmd.SetComputeRootDescriptorTable(8, scene.Heap.GetGPUDescriptorHandleForHeapStart()); cmd.SetComputeRootDescriptorTable(9, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
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
        if (_initialized) _gpu.SignalAndWait();
        _scene?.Dispose(); _table.Dispose();
        foreach (var reservoir in _reservoirs) reservoir?.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose(); _gpu.Dispose();
    }
}
