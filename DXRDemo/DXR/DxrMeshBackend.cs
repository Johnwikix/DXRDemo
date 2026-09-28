using ComputeSharp;
using ComputeSharp.Descriptors;
using ComputeSharp.Interop;
using DXRDemo.Shaders.RayTrace;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

/// <summary>Hardware triangle tracing on the device that owns the HDR / denoiser textures.</summary>
public sealed unsafe class DxrMeshBackend : IMeshTraceBackend
{
    private readonly DxrDevice _gpu = new();
    private readonly List<IDisposable> _owned = [];
    private readonly List<IDisposable> _scene = [];
    private readonly DxrShaderTable _table = new();
    private GraphicsDevice _compute = null!;
    private ID3D12RootSignature _root = null!;
    private ID3D12StateObject _pipeline = null!;
    private ID3D12Resource _triangles = null!, _tlas = null!, _constants = null!;
    private ID3D12DescriptorHeap _heap = null!;
    private MeshData? _mesh;
    private bool _initialized;
    public string Name => "DXR / HARDWARE TRIANGLES";
    public void Initialize(GraphicsDevice device) => _compute = device;
    private T Keep<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    private T Scene<T>(T value) where T : IDisposable { _scene.Add(value); return value; }

    private void InitializePipeline()
    {
        Guid iid = typeof(ID3D12Device5).GUID;
        nint pointer;
        InteropServices.GetID3D12Device(_compute, &iid, (void**)&pointer);
        _gpu.Initialize(new ID3D12Device5(pointer));
        var parameters = new RootParameter1[]
        {
            new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 0)), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All)
        };
        string error = D3D12.D3D12SerializeVersionedRootSignature(new VersionedRootSignatureDescription(new RootSignatureDescription1(RootSignatureFlags.None, parameters)), out Blob blob);
        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
        using (blob) _root = Keep(_gpu.Device.CreateRootSignature<ID3D12RootSignature>(blob.BufferPointer, blob.BufferSize));
        byte[] bytecode = CompileHardwareLibrary();
        _pipeline = Keep(_gpu.Device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(StateObjectType.RaytracingPipeline, new StateSubObject[]
        {
            new(new DxilLibraryDescription(bytecode, new[] { new ExportDescription("RayGen"), new ExportDescription("ClosestHit"), new ExportDescription("Miss") })),
            new(new HitGroupDescription("HitGroup_Sphere", HitGroupType.Triangles, closestHitShaderImport: "ClosestHit")),
            new(new GlobalRootSignature(_root)), new(new RaytracingPipelineConfig(1)), new(new RaytracingShaderConfig(8, 8))
        })));
        using (var props = _pipeline.QueryInterface<ID3D12StateObjectProperties>()) _table.Build(_gpu.Device, props);
        _constants = Keep(DxrResources.CreateUploadBuffer(_gpu.Device, 256));
        _heap = Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible)));
        _initialized = true;
    }

    private static string Source<T>() where T : struct, IComputeShader, IComputeShaderDescriptor<T> => T.HlslSource;
    private static byte[] CompileHardwareLibrary()
    {
        // Keep lighting, camera, random numbers and guides identical to the software tracer.
        // The only replacement is BVH traversal with TraceRay and the dispatch entry point.
        string source = Source<MeshPathTraceShader>();
        int begin = source.IndexOf("bool HitMesh(", source.IndexOf("bool HitMesh(", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        int end = source.IndexOf("static float3 Sky(float3 rd)\n{", begin, StringComparison.Ordinal);
        if (begin < 0 || end < 0) throw new InvalidOperationException("Mesh shader generator contract changed: HitMesh/Sky markers missing.");
        const string trace = """
            struct HitPayload { float t; int id; };
            RaytracingAccelerationStructure SceneBVH : register(t2);
            bool HitMesh(float3 ro, float3 rd, float tMax, bool anyHit, out float bestT, out int bestId) {
                RayDesc ray; ray.Origin=ro; ray.Direction=rd; ray.TMin=0.0001; ray.TMax=tMax;
                HitPayload hit; hit.t=tMax; hit.id=-1;
                uint flags=anyHit ? RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH : RAY_FLAG_NONE;
                TraceRay(SceneBVH,flags,0xFF,0,0,0,ray,hit);
                bestT=hit.t; bestId=hit.id; return hit.id>=0;
            }
            [shader("closesthit")]
            void ClosestHit(inout HitPayload hit, BuiltInTriangleIntersectionAttributes attr) { hit.t=RayTCurrent(); hit.id=PrimitiveIndex(); }
            [shader("miss")]
            void Miss(inout HitPayload hit) { hit.id=-1; }

            """;
        source = source[..begin] + trace + source[end..];
        var entry = new Regex(@"\[NumThreads\([^\r\n]*\)\]\s*void Execute\(uint3 ThreadIds : SV_DispatchThreadID\)\s*\{");
        if (entry.Matches(source).Count != 1) throw new InvalidOperationException("Mesh shader entry point changed.");
        source = entry.Replace(source, "[shader(\"raygeneration\")]\nvoid RayGen() { uint3 ThreadIds=DispatchRaysIndex();");
        string path = Path.Combine(Path.GetTempPath(), "dxr_mesh_" + Guid.NewGuid().ToString("N") + ".hlsl");
        try
        {
            File.WriteAllText(path, source);
            using var compiler = new DxrShaderCompiler();
            compiler.CompileLibrary(path);
            return compiler.DxilBytes;
        }
        finally { File.Delete(path); }
    }

    private ID3D12Resource Upload(ReadOnlySpan<byte> data)
    {
        using var stage = DxrResources.CreateUploadBuffer(_gpu.Device, (ulong)data.Length);
        void* p; stage.Map(0, null, &p).CheckError(); data.CopyTo(new Span<byte>(p, data.Length)); stage.Unmap(0);
        var dest = Scene(DxrResources.CreateDefaultBuffer(_gpu.Device, (ulong)data.Length, ResourceFlags.None, ResourceStates.CopyDest));
        Begin(); _gpu.CommandList.CopyBufferRegion(dest, 0, stage, 0, (ulong)data.Length);
        _gpu.CommandList.ResourceBarrierTransition(dest, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource); End();
        return dest;
    }
    private void BuildScene(MeshData mesh)
    {
        _gpu.SignalAndWait();
        for (int i = _scene.Count - 1; i >= 0; i--) _scene[i].Dispose();
        _scene.Clear();
        _triangles = Upload(MemoryMarshal.AsBytes(mesh.Triangles.AsSpan()));
        var vertices = new System.Numerics.Vector3[mesh.Triangles.Length];
        for (int i = 0; i < vertices.Length; i += 3)
        {
            Float4 p = mesh.Triangles[i], e1 = mesh.Triangles[i + 1], e2 = mesh.Triangles[i + 2];
            vertices[i] = new(p.X, p.Y, p.Z);
            vertices[i + 1] = vertices[i] + new System.Numerics.Vector3(e1.X, e1.Y, e1.Z);
            vertices[i + 2] = vertices[i] + new System.Numerics.Vector3(e2.X, e2.Y, e2.Z);
        }
        var vertexBuffer = Upload(MemoryMarshal.AsBytes(vertices.AsSpan()));
        var geometry = new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(new GpuVirtualAddressAndStride(vertexBuffer.GPUVirtualAddress, 12), Format.R32G32B32_Float, (uint)vertices.Length, 0, 0, Format.Unknown, 0), RaytracingGeometryFlags.Opaque);
        var inputs = new BuildRaytracingAccelerationStructureInputs { Type = RaytracingAccelerationStructureType.BottomLevel, Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace, DescriptorsCount = 1, Layout = ElementsLayout.Array, GeometryDescriptions = [geometry] };
        var info = _gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        var blas = Scene(DxrResources.CreateDefaultBuffer(_gpu.Device, info.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure));
        using var scratch = DxrResources.CreateDefaultBuffer(_gpu.Device, info.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        Begin(); _gpu.CommandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(blas.GPUVirtualAddress, inputs, 0, scratch.GPUVirtualAddress));
        _gpu.CommandList.ResourceBarrierUnorderedAccessView(blas); End();
        using var instance = DxrResources.CreateUploadBuffer(_gpu.Device, 64);
        void* ptr; instance.Map(0, null, &ptr).CheckError(); NativeMemory.Clear(ptr, 64);
        ((float*)ptr)[0] = ((float*)ptr)[5] = ((float*)ptr)[10] = 1;
        ((byte*)ptr)[51] = 255; *(ulong*)((byte*)ptr + 56) = blas.GPUVirtualAddress; instance.Unmap(0);
        var topInputs = new BuildRaytracingAccelerationStructureInputs { Type = RaytracingAccelerationStructureType.TopLevel, Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace, DescriptorsCount = 1, Layout = ElementsLayout.Array, InstanceDescriptions = instance.GPUVirtualAddress };
        var topInfo = _gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(topInputs);
        _tlas = Scene(DxrResources.CreateDefaultBuffer(_gpu.Device, topInfo.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure));
        using var topScratch = DxrResources.CreateDefaultBuffer(_gpu.Device, topInfo.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        Begin(); _gpu.CommandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(_tlas.GPUVirtualAddress, topInputs, 0, topScratch.GPUVirtualAddress));
        _gpu.CommandList.ResourceBarrierUnorderedAccessView(_tlas); End();
        _mesh = mesh;
    }
    private void Begin() { _gpu.CommandAllocator.Reset(); _gpu.CommandList.Reset(_gpu.CommandAllocator); }
    private void End() { _gpu.CommandList.Close(); _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList); _gpu.SignalAndWait(); }
    private readonly struct ConstantLoader(nint pointer) : IConstantBufferLoader
    {
        public void LoadConstantBuffer(ReadOnlySpan<byte> data)
        {
            if (data.Length > 256) throw new InvalidOperationException("Mesh constant buffer exceeds its allocation.");
            data.CopyTo(new Span<byte>((void*)pointer, 256));
        }
    }
    private static void WriteConstants<T>(in T shader, ref ConstantLoader loader, int width, int height)
        where T : struct, IComputeShader, IComputeShaderDescriptor<T> => T.LoadConstantBuffer(in shader, ref loader, width, height, 1);
    public void Trace(MeshData mesh, int width, int height, int samples, int bounces, int frame, Float2 orbit, float distance,
        ReadWriteTexture2D<Float4> output, ReadWriteTexture2D<Rgba32, Float4> normals)
    {
        if (!_initialized) InitializePipeline();
        if (!ReferenceEquals(mesh, _mesh)) BuildScene(mesh);
        Guid iid = typeof(ID3D12Resource).GUID;
        nint op, np;
        InteropServices.GetID3D12Resource(output, &iid, (void**)&op);
        InteropServices.GetID3D12Resource(normals, &iid, (void**)&np);
        using var outputResource = new ID3D12Resource(op);
        using var normalResource = new ID3D12Resource(np);
        var cpu = _heap.GetCPUDescriptorHandleForHeapStart();
        _gpu.Device.CreateUnorderedAccessView(outputResource, null, null, cpu);
        _gpu.Device.CreateUnorderedAccessView(normalResource, null, null, cpu + (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
        // Ask ComputeSharp's generated descriptor to pack constants; no hand-maintained offsets.
        var shader = new MeshPathTraceShader(width, height, samples, bounces, frame, orbit, distance, null!, null!, output, normals);
        void* pointer; _constants.Map(0, null, &pointer).CheckError();
        var loader = new ConstantLoader((nint)pointer); WriteConstants(in shader, ref loader, width, height); _constants.Unmap(0);
        Begin(); var cmd = _gpu.CommandList;
        cmd.SetDescriptorHeaps(_heap); cmd.SetComputeRootSignature(_root);
        cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(1, _triangles.GPUVirtualAddress);
        cmd.SetComputeRootShaderResourceView(2, _triangles.GPUVirtualAddress); // Software nodes are unused by the DXR library.
        cmd.SetComputeRootDescriptorTable(3, _heap.GetGPUDescriptorHandleForHeapStart());
        cmd.SetComputeRootShaderResourceView(4, _tlas.GPUVirtualAddress);
        cmd.SetPipelineState1(_pipeline);
        cmd.DispatchRays(new DispatchRaysDescription(new(_table.Buffer.GPUVirtualAddress, 64), new(_table.Buffer.GPUVirtualAddress + 64, 64, 64), new(_table.Buffer.GPUVirtualAddress + 128, 64, 64), default, (uint)width, (uint)height, 1));
        cmd.ResourceBarrierUnorderedAccessView(outputResource); cmd.ResourceBarrierUnorderedAccessView(normalResource);
        // Complete this queue before ComputeSharp consumes the textures on its own queue.
        End();
    }
    public void Dispose()
    {
        if (_initialized) _gpu.SignalAndWait();
        for (int i = _scene.Count - 1; i >= 0; i--) _scene[i].Dispose();
        _table.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _gpu.Dispose();
    }
}
