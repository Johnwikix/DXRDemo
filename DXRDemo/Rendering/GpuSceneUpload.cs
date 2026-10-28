using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

public sealed unsafe partial class GpuSceneCache
{
    private void Begin()
    {
        _gpu.CommandAllocator.Reset();
        _gpu.CommandList.Reset(_gpu.CommandAllocator);
    }
    private void End()
    {
        _gpu.CommandList.Close();
        _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList);
        _gpu.SignalAndWait();
    }
    private ID3D12Resource Upload(GpuResourceOwner scene, ReadOnlySpan<byte> bytes, ResourceStates state = ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource)
    {
        using var staging = DxrResources.CreateUploadBuffer(_gpu.Device, (ulong)Math.Max(bytes.Length, 16));
        void* pointer;
        staging.Map(0, null, &pointer).CheckError();
        NativeMemory.Clear(pointer, (nuint)Math.Max(bytes.Length, 16));
        bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
        staging.Unmap(0);
        var output = scene.Keep(DxrResources.CreateDefaultBuffer(_gpu.Device, (ulong)Math.Max(bytes.Length, 16), ResourceFlags.None, ResourceStates.CopyDest));
        Begin();
        _gpu.CommandList.CopyBufferRegion(output, 0, staging, 0, (ulong)Math.Max(bytes.Length, 16));
        _gpu.CommandList.ResourceBarrierTransition(output, ResourceStates.CopyDest, state);
        End();
        return output;
    }

    private GpuScene BuildScene(SceneAsset asset, CancellationToken cancellation)
    {
        if (asset.Textures.Length > MaxTextures)
            throw new NotSupportedException($"场景贴图数超过 {MaxTextures}。");
        if (asset.Instances.Length == 0)
            throw new InvalidDataException("所选场景没有可渲染网格。");
        var scene = new GpuScene { Asset = asset };
        try
        {
            int vertexCount = 0, indexCount = 0;
            var offsets = new (int Vertex, int Index)[asset.Meshes.Length];
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = (vertexCount, indexCount);
                vertexCount = checked(vertexCount + asset.Meshes[i].Vertices.Length);
                indexCount = checked(indexCount + asset.Meshes[i].Indices.Length);
            }
            var vertices = new SceneVertex[vertexCount];
            var indices = new uint[indexCount];
            for (int i = 0; i < offsets.Length; i++)
            {
                asset.Meshes[i].Vertices.CopyTo(vertices, offsets[i].Vertex);
                asset.Meshes[i].Indices.CopyTo(indices, offsets[i].Index);
            }
            scene.MeshOffsets = offsets;
            scene.Vertices = Upload(scene, MemoryMarshal.AsBytes(vertices.AsSpan()), ResourceStates.VertexAndConstantBuffer | ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
            scene.Indices = Upload(scene, MemoryMarshal.AsBytes(indices.AsSpan()), ResourceStates.IndexBuffer | ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
            scene.Materials = Upload(scene, MemoryMarshal.AsBytes(asset.Materials.AsSpan()));
            SceneLight[] lights = asset.Lights.Length == 0 ? new SceneLight[1] : asset.Lights;
            scene.Lights = Upload(scene, MemoryMarshal.AsBytes(lights.AsSpan()));
            var distribution = new SceneLightDistribution(asset, cancellation);
            var instanceData = new GpuSceneInstance[asset.Instances.Length];
            Vector3 glassMin = new(float.PositiveInfinity), glassMax = new(float.NegativeInfinity);
            for (int i = 0; i < instanceData.Length; i++)
            {
                SceneInstance instance = asset.Instances[i];
                var mesh = asset.Meshes[instance.Mesh];
                SceneMaterial material = asset.Materials[mesh.Material];
                scene.HasTransmission |= material.Transmission.X > 0;
                bool causticCaster = CausticGeometry.IsCaster(mesh, material, instance.Transform);
                if (causticCaster)
                {
                    scene.HasRefractiveVolumes = true;
                    foreach (ref readonly SceneVertex vertex in mesh.Vertices.AsSpan())
                    {
                        Vector3 point = Vector3.Transform(new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), instance.Transform);
                        glassMin = Vector3.Min(glassMin, point);
                        glassMax = Vector3.Max(glassMax, point);
                    }
                }
                Matrix4x4.Invert(instance.Transform, out Matrix4x4 inverse);
                instanceData[i] = new()
                {
                    VertexOffset = (uint)offsets[instance.Mesh].Vertex,
                    IndexOffset = (uint)offsets[instance.Mesh].Index,
                    Material = (uint)mesh.Material,
                    CausticCaster = causticCaster ? 1u : 0u,
                    World = instance.Transform,
                    Normal = Matrix4x4.Transpose(inverse),
                    EmissionSampling = new(distribution.InstanceAreaPdf[i], distribution.InstanceEmitterStart[i] + 1, 0, 0)
                };
            }
            scene.EmitterCount = distribution.TotalPower > 0 ? (uint)distribution.Emitters.Length : 0;
            scene.EmitterPower = distribution.TotalPower;
            if (scene.HasRefractiveVolumes)
                scene.CausticBounds = new((glassMin + glassMax) * .5f, MathF.Max(Vector3.Distance(glassMin, glassMax) * .501f, 1e-4f));
            scene.Emitters = Upload(scene, MemoryMarshal.AsBytes(distribution.Emitters.AsSpan()));
            EnvironmentDistribution.Texel[] environmentTexels = asset.EnvironmentMap is { } map ? new EnvironmentDistribution(map).Texels : [default];
            scene.EnvironmentTexels = Upload(scene, MemoryMarshal.AsBytes(environmentTexels.AsSpan()));
            scene.Instances = Upload(scene, MemoryMarshal.AsBytes(instanceData.AsSpan()));
            scene.TextureViews = scene.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, MaxTextures, DescriptorHeapFlags.None)));
            scene.Samplers = scene.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.Sampler, MaxTextures, DescriptorHeapFlags.ShaderVisible)));
            var nullView = new ShaderResourceViewDescription
            {
                Format = Format.R8G8B8A8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new() { MipLevels = 1 }
            };
            int samplerStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
            for (int i = 0; i < MaxTextures; i++)
            {
                _gpu.Device.CreateShaderResourceView(null, nullView, scene.TextureViews.GetCPUDescriptorHandleForHeapStart() + i * _stride);
                var texture = i < asset.Textures.Length ? asset.Textures[i] : null;
                int min = texture?.MinFilter ?? 9987, mag = texture?.MagFilter ?? 9729;
                bool minLinear = min is 9729 or 9985 or 9987, mipLinear = min is 9986 or 9987;
                var samplerDescription = new SamplerDescription
                {
                    Filter = (Filter)((minLinear ? 16 : 0) | (mag == 9729 ? 4 : 0) | (mipLinear ? 1 : 0)),
                    AddressU = Address(texture?.WrapS ?? 10497),
                    AddressV = Address(texture?.WrapT ?? 10497),
                    AddressW = TextureAddressMode.Wrap,
                    MinLOD = 0,
                    MaxLOD = min is 9728 or 9729 ? 0 : float.MaxValue,
                    MaxAnisotropy = 1,
                    ComparisonFunction = ComparisonFunction.Always
                };
                _gpu.Device.CreateSampler(ref samplerDescription, scene.Samplers.GetCPUDescriptorHandleForHeapStart() + i * samplerStride);
            }
            for (int i = 0; i < asset.Textures.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                UploadTexture(scene, asset.Textures[i], i);
            }
            cancellation.ThrowIfCancellationRequested();
            return scene;
        }
        catch { try { _gpu.SignalAndWait(); } finally { scene.Dispose(); } throw; }
    }

    private RayTracingScene BuildRayTracingScene(GpuScene scene, CancellationToken cancellation)
    {
        SceneAsset asset = scene.Asset;
        if (asset.Instances.Length > 1 << 24)
            throw new NotSupportedException("DXR instance IDs are limited to 24 bits.");
        var offsets = scene.MeshOffsets;
        var result = new RayTracingScene();
        try
        {
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
                var inputs = new BuildRaytracingAccelerationStructureInputs
                {
                    Type = RaytracingAccelerationStructureType.BottomLevel,
                    Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                    DescriptorsCount = 1,
                    Layout = ElementsLayout.Array,
                    GeometryDescriptions = [geometry]
                };
                blas[i] = BuildAcceleration(result, inputs);
            }
            var descriptions = new byte[checked(asset.Instances.Length * 64)];
            for (int i = 0; i < asset.Instances.Length; i++)
            {
                SceneInstance instance = asset.Instances[i];
                SceneMaterial material = asset.Materials[asset.Meshes[instance.Mesh].Material];
                Span<byte> desc = descriptions.AsSpan(i * 64, 64);
                Span<float> transform = MemoryMarshal.Cast<byte, float>(desc[..48]);
                Matrix4x4 m = Matrix4x4.Transpose(instance.Transform);
                MemoryMarshal.CreateReadOnlySpan(ref m.M11, 12).CopyTo(transform);
                // bit 0 是普通射线，bit 1 是仅透射阴影，bit 2 是不透明阴影。
                // TLAS 提前过滤不透明物体，避免每次玻璃查询都为墙面触发 any-hit。
                uint visibilityMask = material.Transmission.X > 0 ? 3u : 5u;
                MemoryMarshal.Write(desc[48..], (uint)i | (visibilityMask << 24));
                // DXR 在实例变换前判定绕序，与 glTF 物体空间法线一致；
                // 镜像实例继续沿用同一逆转置法线和剔除语义。
                uint instanceFlags = 0;
                if (material.Flags.Z != 0 || material.Transmission.X > 0)
                    instanceFlags |= (uint)RaytracingInstanceFlags.TriangleCullDisable;
                MemoryMarshal.Write(desc[52..], instanceFlags << 24);
                MemoryMarshal.Write(desc[56..], blas[instance.Mesh].GPUVirtualAddress);
            }
            var instanceBuffer = Upload(result, descriptions);
            result.Tlas = BuildAcceleration(result, new BuildRaytracingAccelerationStructureInputs
            {
                Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = (uint)asset.Instances.Length,
                Layout = ElementsLayout.Array,
                InstanceDescriptions = instanceBuffer.GPUVirtualAddress
            });
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch { try { _gpu.SignalAndWait(); } finally { result.Dispose(); } throw; }
    }

    private static TextureAddressMode Address(int wrap) => wrap == 33071 ? TextureAddressMode.Clamp : wrap == 33648 ? TextureAddressMode.Mirror : TextureAddressMode.Wrap;

    private ID3D12Resource BuildAcceleration(GpuResourceOwner scene, BuildRaytracingAccelerationStructureInputs inputs)
    {
        var info = _gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        var result = scene.Keep(DxrResources.CreateDefaultBuffer(_gpu.Device, info.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure));
        using var scratch = DxrResources.CreateDefaultBuffer(_gpu.Device, info.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        Begin();
        _gpu.CommandList.BuildRaytracingAccelerationStructure(new(result.GPUVirtualAddress, inputs, 0, scratch.GPUVirtualAddress));
        _gpu.CommandList.ResourceBarrierUnorderedAccessView(result);
        End();
        return result;
    }

    private void UploadTexture(GpuScene scene, SceneTexture source, int index)
    {
        bool compressed = source.Bc7Mips != null;
        byte[][] mips = source.Bc7Mips ?? source.Mips;
        if (source.Width <= 0 || source.Height <= 0 || mips.Length == 0 ||
            mips.Length > 1 + BitOperations.Log2((uint)Math.Max(source.Width, source.Height)))
            throw new InvalidDataException("贴图尺寸或 mip 数量无效。");
        // 在 Begin 前检查每级数据；无效候选不能留下仍在录制的命令列表。
        int RowBytes(int width) => compressed ? checked(((width + 3) / 4) * 16) : checked(width * 4);
        int Rows(int height) => compressed ? (height + 3) / 4 : height;
        int checkWidth = source.Width, checkHeight = source.Height;
        foreach (byte[] mip in mips)
        {
            if (mip.Length < checked(RowBytes(checkWidth) * Rows(checkHeight)))
                throw new InvalidDataException("贴图 mip 数据不完整。");
            checkWidth = Math.Max(1, checkWidth / 2);
            checkHeight = Math.Max(1, checkHeight / 2);
        }
        Format format = compressed ? (source.Srgb ? Format.BC7_UNorm_SRgb : Format.BC7_UNorm) :
            (source.Srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm);
        var desc = ResourceDescription.Texture2D(format,
            (uint)source.Width, (uint)source.Height, 1, (ushort)mips.Length);
        var texture = scene.Keep(_gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, desc, ResourceStates.CopyDest));
        // 上传阶段遵守 D3D12 的行与切片对齐；帧渲染不再创建这些暂存资源。
        ulong total = 0;
        int w = source.Width, h = source.Height;
        for (int i = 0; i < mips.Length; i++)
        {
            total = (total + 511) & ~511ul;
            total += (ulong)((RowBytes(w) + 255) & ~255) * (uint)Rows(h);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        using var staging = DxrResources.CreateUploadBuffer(_gpu.Device, total);
        void* p;
        staging.Map(0, null, &p).CheckError();
        w = source.Width;
        h = source.Height;
        ulong offset = 0;
        Begin();
        for (int i = 0; i < mips.Length; i++)
        {
            int rowBytes = RowBytes(w), rows = Rows(h);
            offset = (offset + 511) & ~511ul;
            uint pitch = (uint)((rowBytes + 255) & ~255);
            for (int y = 0; y < rows; y++)
                mips[i].AsSpan(y * rowBytes, rowBytes).CopyTo(new Span<byte>((byte*)p + offset + (ulong)y * pitch, rowBytes));
            var footprint = new PlacedSubresourceFootPrint { Offset = offset, Footprint = new SubresourceFootPrint { Format = desc.Format, Width = (uint)w, Height = (uint)h, Depth = 1, RowPitch = pitch } };
            _gpu.CommandList.CopyTextureRegion(new TextureCopyLocation(texture, (uint)i), 0, 0, 0, new TextureCopyLocation(staging, footprint));
            offset += pitch * (uint)rows;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        staging.Unmap(0);
        _gpu.CommandList.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
        End();
        _gpu.Device.CreateShaderResourceView(texture, null, scene.TextureViews.GetCPUDescriptorHandleForHeapStart() + index * _stride);
    }

}
