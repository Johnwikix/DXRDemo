using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.Camera;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

/// <summary>Traces indexed, instanced PBR scenes with explicitly maintained DXR shaders.</summary>
internal sealed unsafe class DxrSceneBackend : IDisposable
{
    private const int MaxTextures = 1024, OutputCount = 16;
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
    internal bool CausticsEnabled { get; set; } = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public uint VertexOffset, IndexOffset, Material, CausticCaster;
        public Matrix4x4 World, Normal;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Emitter { public Vector4 P0, E1, E2, Meta; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Origin, Forward, Right, Up, Size, Control, Environment, Limits, SunDirection, SunRadiance;
        public Vector4 PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, PreviousSize;
        public Vector4 Lighting, CausticBounds, CausticSettings;
    }
    private sealed class SceneGpu : IDisposable
    {
        internal readonly List<IDisposable> Owned = [];
        internal SceneAsset Asset = null!;
        internal ID3D12Resource Vertices = null!, Indices = null!, Instances = null!, Materials = null!, Lights = null!, Emitters = null!, Tlas = null!;
        internal ID3D12DescriptorHeap Heap = null!, Samplers = null!;
        internal ID3D12DescriptorHeap[] DescriptorHeaps = [];
        internal uint EmitterCount;
        internal float EmitterArea;
        internal bool HasTransmission, HasRefractiveVolumes;
        internal Vector4 CausticBounds;
        internal T Keep<T>(T value) where T : IDisposable { Owned.Add(value); return value; }
        public void Dispose() { for (int i = Owned.Count - 1; i >= 0; i--) Owned[i].Dispose(); Owned.Clear(); }
    }

    internal void Initialize(GraphicsDevice device)
    {
        if (_initialized) return;
        Guid iid = typeof(ID3D12Device5).GUID; nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        _gpu.Initialize(new ID3D12Device5(pointer));
        var parameters = new List<RootParameter1>
        { new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) };
        for (uint i = 0; i < 7; i++) parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(i, 0), ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 9, 0, 0),
            new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 7, 13, 0),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaxTextures, 0, 1),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, 0, 2)), ShaderVisibility.All));
        parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler, MaxTextures, 0, 1)), ShaderVisibility.All));
        for (uint i = 9; i < 13; i++) parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(i, 0), ShaderVisibility.All));
        _root = _gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray())); _owned.Add(_root);
        using var compiler = new DxrShaderCompiler();
        compiler.CompileLibrary(Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "SceneTrace.hlsl"));
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
                var geometry = new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(
                    new GpuVirtualAddressAndStride(scene.Vertices.GPUVirtualAddress + (ulong)offsets[i].Vertex * 80, 80),
                    Format.R32G32B32_Float, (uint)mesh.Vertices.Length, transform3x4: 0,
                    indexBuffer: scene.Indices.GPUVirtualAddress + (ulong)offsets[i].Index * 4,
                    indexFormat: Format.R32_UInt, indexCount: (uint)mesh.Indices.Length), RaytracingGeometryFlags.None);
                var inputs = new BuildRaytracingAccelerationStructureInputs { Type = RaytracingAccelerationStructureType.BottomLevel,
                    Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace, DescriptorsCount = 1, Layout = ElementsLayout.Array, GeometryDescriptions = [geometry] };
                blas[i] = BuildAcceleration(scene, inputs);
            }
            var instanceData = new InstanceData[asset.Instances.Length]; var emitters = new List<Emitter>();
            var descriptions = new byte[checked(asset.Instances.Length * 64)]; float areaSum = 0;
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
                    Material = (uint)mesh.Material, CausticCaster = causticCaster ? 1u : 0u, World = instance.Transform, Normal = Matrix4x4.Transpose(inverse) };
                Span<byte> desc = descriptions.AsSpan(i * 64, 64); Span<float> transform = MemoryMarshal.Cast<byte, float>(desc[..48]);
                Matrix4x4 m = Matrix4x4.Transpose(instance.Transform);
                MemoryMarshal.CreateReadOnlySpan(ref m.M11, 12).CopyTo(transform);
                MemoryMarshal.Write(desc[48..], (uint)i | 0xff000000u);
                MemoryMarshal.Write(desc[56..], blas[instance.Mesh].GPUVirtualAddress);
                if (asset.Materials[mesh.Material].Emissive.X + asset.Materials[mesh.Material].Emissive.Y + asset.Materials[mesh.Material].Emissive.Z > 0)
                    for (int t = 0; t < mesh.Indices.Length; t += 3)
                    {
                        Vector3 Point(uint index) { Vector4 p = mesh.Vertices[index].Position; return Vector3.Transform(new(p.X, p.Y, p.Z), instance.Transform); }
                        Vector3 p0 = Point(mesh.Indices[t]), e1 = Point(mesh.Indices[t + 1]) - p0, e2 = Point(mesh.Indices[t + 2]) - p0;
                        float area = Vector3.Cross(e1, e2).Length() * .5f;
                        if (area <= 1e-12f) continue;
                        areaSum += area;
                        emitters.Add(new() { P0 = new(p0, 0), E1 = new(e1, 0), E2 = new(e2, 0), Meta = new(area, areaSum, i, t / 3) });
                    }
            }
            for (int i = 0; i < emitters.Count; i++) { Emitter e = emitters[i]; e.Meta.Y /= areaSum; emitters[i] = e; }
            scene.EmitterCount = (uint)emitters.Count;
            scene.EmitterArea = areaSum;
            if (scene.HasRefractiveVolumes) scene.CausticBounds = new((glassMin + glassMax) * .5f, MathF.Max(Vector3.Distance(glassMin, glassMax) * .501f, 1e-4f));
            if (emitters.Count == 0) emitters.Add(default);
            scene.Emitters = Upload(scene, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(emitters)));
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
        var desc = ResourceDescription.Texture2D(source.Srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
            (uint)source.Width, (uint)source.Height, 1, (ushort)source.Mips.Length);
        var texture = scene.Keep(_gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, desc, ResourceStates.CopyDest));
        // Explicit D3D12 row/slice alignment; no temporary allocations in frame rendering.
        ulong total = 0; int w = source.Width, h = source.Height;
        for (int i = 0; i < source.Mips.Length; i++) { total = (total + 511) & ~511ul; total += (ulong)((w * 4 + 255) & ~255) * (uint)h; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        using var staging = DxrResources.CreateUploadBuffer(_gpu.Device, total);
        void* p; staging.Map(0, null, &p).CheckError(); w = source.Width; h = source.Height; ulong offset = 0;
        Begin();
        for (int i = 0; i < source.Mips.Length; i++)
        {
            offset = (offset + 511) & ~511ul; uint pitch = (uint)((w * 4 + 255) & ~255);
            for (int y = 0; y < h; y++) source.Mips[i].AsSpan(y * w * 4, w * 4).CopyTo(new Span<byte>((byte*)p + offset + (ulong)y * pitch, w * 4));
            var footprint = new PlacedSubresourceFootPrint { Offset = offset, Footprint = new SubresourceFootPrint { Format = desc.Format, Width = (uint)w, Height = (uint)h, Depth = 1, RowPitch = pitch } };
            _gpu.CommandList.CopyTextureRegion(new TextureCopyLocation(texture, (uint)i), 0, 0, 0, new TextureCopyLocation(staging, footprint));
            offset += pitch * (uint)h; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        staging.Unmap(0); _gpu.CommandList.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource); End();
        _gpu.Device.CreateShaderResourceView(texture, null, scene.Heap.GetCPUDescriptorHandleForHeapStart() + (index + OutputCount) * _stride);
    }

    private static ID3D12Resource Resource<T>(ReadWriteTexture2D<T> texture) where T : unmanaged
    { Guid iid = typeof(ID3D12Resource).GUID; nint pointer; InteropServices.GetID3D12Resource(texture, &iid, (void**)&pointer); return new(pointer); }
    private static ID3D12Resource Resource(ReadWriteTexture2D<Rgba32, Float4> texture)
    { Guid iid = typeof(ID3D12Resource).GUID; nint pointer; InteropServices.GetID3D12Resource(texture, &iid, (void**)&pointer); return new(pointer); }

    internal void Trace(in CameraFrame camera, int frame, int samples, int bounces, Float2 jitter, float environment,
        ReadWriteTexture2D<Float4> raw, ReadWriteTexture2D<Rgba32, Float4> normal, ReadWriteTexture2D<Float4> surface,
        ReadWriteTexture2D<Float4> guide, PbrSignals signals,
        SunLightSettings sun = default, bool rayReconstruction = false, bool resetHistory = false, bool opaqueForNrd = false)
    {
        var scene = _scene ?? throw new InvalidOperationException("Upload scene before tracing.");
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
            foreach (var old in _outputs) old?.Dispose();
            _outputs[0] = Resource(raw); _outputs[1] = Resource(normal); _outputs[2] = Resource(surface); _outputs[3] = Resource(guide);
            _outputs[4] = Resource(signals.Diffuse); _outputs[5] = Resource(signals.Specular); _outputs[6] = Resource(signals.Albedo); _outputs[7] = Resource(signals.Unfiltered);
            _outputs[8] = Resource(signals.SpecularGuide);
            _outputs[9] = Resource(signals.DiffuseSh); _outputs[10] = Resource(signals.SpecularSh); _outputs[11] = Resource(signals.GlassSurface);
            _outputs[12] = Resource(signals.DiffuseFactor); _outputs[13] = Resource(signals.SpecularFactor);
            _outputs[14] = Resource(signals.GlassFallback); _outputs[15] = Resource(signals.GlassNormal);
            for (int i = 0; i < OutputCount; i++) _gpu.Device.CreateUnorderedAccessView(_outputs[i], null, null, scene.Heap.GetCPUDescriptorHandleForHeapStart() + i * _stride);
            _boundOutput = raw;
        }
        Constants data = new() { Origin = new(camera.Origin, camera.Near), Forward = new(camera.Forward, MathF.Tan(camera.VerticalFov * .5f)),
            Right = new(camera.Right, camera.Far), Up = new(camera.Up, 0), Size = new(raw.Width, raw.Height, jitter.X, jitter.Y),
            Control = new(samples, bounces, frame, scene.Asset.Lights.Length), Environment = new(environment, scene.EmitterCount, scene.EmitterArea, 0),
            Limits = new(MathF.Max(camera.Near * .01f, 1e-6f), Vector3.Distance(scene.Asset.Minimum, scene.Asset.Maximum), rayReconstruction ? 1 : 0, scene.HasTransmission ? 1 : 0),
            SunDirection = new(sun.Direction, 0), SunRadiance = new(sun.Radiance, 0),
            PreviousOrigin = _previous.Origin, PreviousForward = _previous.Forward, PreviousRight = _previous.Right, PreviousUp = _previous.Up, PreviousSize = _previous.Size,
            Lighting = new(RestirEnabled ? 1 : 0, 0, 8, 32), CausticBounds = scene.CausticBounds,
            CausticSettings = new(CausticsEnabled && scene.HasRefractiveVolumes && (scene.Asset.Lights.Length > 0 || sun.Radiance.LengthSquared() > 0) ? PhotonCount : 0,
                MathF.Max(scene.CausticBounds.W * .015f, camera.Near * 8), PhotonGridSize, 12) };
        data.Up.W = opaqueForNrd ? 1 : 0;
        bool compatible = _historyValid && !resetHistory && frame > 0 && frame == _previousFrame + 1 && data.Up.W == _previous.Up.W &&
            data.Environment == _previous.Environment && data.SunDirection == _previous.SunDirection && data.SunRadiance == _previous.SunRadiance &&
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
        cmd.DispatchRays(new(new(_table.Buffer.GPUVirtualAddress, 64), new(_table.Buffer.GPUVirtualAddress + 64, 64, 64),
            new(_table.Buffer.GPUVirtualAddress + 128, 64, 64), default, (uint)raw.Width, (uint)raw.Height, 1));
        foreach (var output in _outputs) cmd.ResourceBarrierUnorderedAccessView(output!);
        cmd.ResourceBarrierUnorderedAccessView(_reservoirs[_reservoirWrite]!);
        End();
        if (rebuildPhotons) { _photonFrame = data; _photonsValid = true; PhotonBuildCount++; }
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
        foreach (var output in _outputs) output?.Dispose(); _scene?.Dispose(); _table.Dispose();
        foreach (var reservoir in _reservoirs) reservoir?.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose(); _gpu.Dispose();
    }
}
