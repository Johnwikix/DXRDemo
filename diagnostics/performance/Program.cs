using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.DXR;
using Vortice.Direct3D12;
using Vortice.DXGI;

unsafe class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "mesh") { MeshBenchmark.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "compute") { RunCompute(args); return; }
        string shader = Path.GetFullPath(args.Length > 0 ? args[0] : "../../DXRDemo/Shaders/DXR/RayTrace.hlsl");
        uint width = args.Length > 1 ? uint.Parse(args[1]) : 1280;
        uint height = args.Length > 2 ? uint.Parse(args[2]) : 720;
        using var gpu = new DxrDevice();
        gpu.Initialize();
        long luid = gpu.Device.AdapterLuid;
        using var adapter = gpu.DxgiFactory.EnumAdapterByLuid<IDXGIAdapter1>(*(Vortice.Luid*)&luid);
        Console.WriteLine($"GPU={adapter.Description1.Description}; shader={Path.GetFileName(shader)}; size={width}x{height}");
        using var root = new DxrRootSignature(); root.Build(gpu.Device);
        bool compute = shader.EndsWith(".cs.hlsl");
        using var compiler = new DxrShaderCompiler();
        byte[] bytecode;
        if (compute)
        {
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DXR_DXC_PATH")!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-T", "cs_6_5", "-E", "ComputeMain", "-Fo", shader + ".dxil", "-I", Path.GetDirectoryName(shader)!, shader }) info.ArgumentList.Add(arg);
            using var proc = Process.Start(info)!; proc.WaitForExit();
            if (proc.ExitCode != 0) throw new Exception("DXC failed");
            bytecode = File.ReadAllBytes(shader + ".dxil");
        }
        else { compiler.CompileLibrary(shader, gpu.SupportsShaderModel69, shader.Contains("SceneTrace", StringComparison.OrdinalIgnoreCase)); bytecode = compiler.DxilBytes; }
        using var scene = new DxrAccelerationStructure();
        gpu.CommandAllocator.Reset(); gpu.CommandList.Reset(gpu.CommandAllocator);
        scene.Build(gpu.Device, gpu.CommandList);
        gpu.CommandList.Close(); gpu.CommandQueue.ExecuteCommandList(gpu.CommandList); gpu.SignalAndWait();
        using var pso = new DxrPipelineState();
        if (!compute) pso.Build(gpu.Device, root.RootSignature, bytecode, ["RayGen", "ClosestHit", "Miss", "IntersectionSphere"]);
        using var computePso = compute ? gpu.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = root.RootSignature, ComputeShader = bytecode }) : null;
        using var table = new DxrShaderTable(); if (!compute) table.Build(gpu.Device, pso.Properties);
        using var output = DxrResources.CreateUnorderedAccessTexture2D(gpu.Device, width, height, Format.R8G8B8A8_UNorm, ResourceStates.UnorderedAccess);
        using var previous = DxrResources.CreateUnorderedAccessTexture2D(gpu.Device, width, height, Format.R8G8B8A8_UNorm, ResourceStates.UnorderedAccess);
        using var cb = DxrResources.CreateUploadBuffer(gpu.Device, 256);
        void* constants; cb.Map(0, null, &constants).CheckError();
        *(SceneConstants*)constants = new() { Resolution = new(width, height), Distance = 6.5f, Time = 1, Frame = 0 };
        cb.Unmap(0);
        using var heap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        var handle = heap.GetCPUDescriptorHandleForHeapStart();
        gpu.Device.CreateUnorderedAccessView(output, null, null, handle);
        gpu.Device.CreateUnorderedAccessView(previous, null, null, handle + (int)gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
        using var queries = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 2));
        using var readback = gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer(16), ResourceStates.CopyDest);
        gpu.CommandQueue.GetTimestampFrequency(out ulong frequency).CheckError();
        var dispatch = compute ? default : new DispatchRaysDescription(
            new(table.Buffer.GPUVirtualAddress, DxrShaderTable.RecordSize),
            new(table.Buffer.GPUVirtualAddress + DxrShaderTable.MissOffset, DxrShaderTable.RecordSize, DxrShaderTable.RecordSize),
            new(table.Buffer.GPUVirtualAddress + DxrShaderTable.HitGroupOffset, DxrShaderTable.RecordSize, DxrShaderTable.RecordSize),
            default, width, height, 1);
        var times = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            gpu.CommandAllocator.Reset(); gpu.CommandList.Reset(gpu.CommandAllocator);
            var cmd = gpu.CommandList;
            cmd.SetDescriptorHeaps(heap);
            if (compute) cmd.SetPipelineState(computePso!); else cmd.SetPipelineState1(pso.StateObject);
            cmd.SetComputeRootSignature(root.RootSignature);
            cmd.SetComputeRootShaderResourceView(0, scene.Tlas.GPUVirtualAddress);
            cmd.SetComputeRootDescriptorTable(1, heap.GetGPUDescriptorHandleForHeapStart());
            cmd.SetComputeRootConstantBufferView(2, cb.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(3, scene.SphereBuffer.GPUVirtualAddress);
            cmd.ResourceBarrierUnorderedAccessView(output);
            cmd.EndQuery(queries, QueryType.Timestamp, 0);
            if (compute) cmd.Dispatch((width + 7) / 8, (height + 7) / 8, 1); else cmd.DispatchRays(dispatch);
            cmd.EndQuery(queries, QueryType.Timestamp, 1);
            cmd.ResolveQueryData(queries, QueryType.Timestamp, 0, 2, readback, 0);
            cmd.Close(); gpu.CommandQueue.ExecuteCommandList(cmd); gpu.SignalAndWait();
            void* ptr; readback.Map(0, null, &ptr).CheckError();
            double ms = (((ulong*)ptr)[1] - ((ulong*)ptr)[0]) * 1000.0 / frequency;
            readback.Unmap(0);
            if (i >= 100) times.Add(ms);
        }
        times.Sort();
        Console.WriteLine($"GPU timestamp: median={times[times.Count / 2]:F4} ms; mean={times.Average():F4} ms; p10={times[times.Count / 10]:F4}; p90={times[times.Count * 9 / 10]:F4}; n={times.Count}");
        if (Environment.GetEnvironmentVariable("PERF_DUMP") == "1") DumpPixels(gpu, output, width, height, shader + ".rgba");
    }

    static void DumpPixels(DxrDevice gpu, ID3D12Resource output, uint width, uint height, string path)
    {
        uint pitch = (width * 4 + 255) & ~255u;
        using var buffer = gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer((ulong)pitch * height), ResourceStates.CopyDest);
        gpu.CommandAllocator.Reset(); gpu.CommandList.Reset(gpu.CommandAllocator);
        gpu.CommandList.ResourceBarrierTransition(output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        var footprint = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint { Format = Format.R8G8B8A8_UNorm, Width = width, Height = height, Depth = 1, RowPitch = pitch } };
        gpu.CommandList.CopyTextureRegion(new TextureCopyLocation(buffer, footprint), 0, 0, 0, new TextureCopyLocation(output, 0));
        gpu.CommandList.Close(); gpu.CommandQueue.ExecuteCommandList(gpu.CommandList); gpu.SignalAndWait();
        void* ptr; buffer.Map(0, null, &ptr).CheckError();
        using var file = File.Create(path);
        for (uint row = 0; row < height; row++) file.Write(new ReadOnlySpan<byte>((byte*)ptr + row * pitch, (int)width * 4));
        buffer.Unmap(0);
    }

    static void RunCompute(string[] args)
    {
        int width = args.Length > 1 ? int.Parse(args[1]) : 1280;
        int height = args.Length > 2 ? int.Parse(args[2]) : 720;
        int samples = args.Length > 3 ? int.Parse(args[3]) : 4;
        var device = ComputeSharp.GraphicsDevice.GetDefault();
        using var target = ComputeSharp.GraphicsDeviceExtensions.AllocateReadWriteTexture2D<ComputeSharp.Rgba64, ComputeSharp.Float4>(device, width, height);
        using var normal = ComputeSharp.GraphicsDeviceExtensions.AllocateReadWriteTexture2D<ComputeSharp.Rgba32, ComputeSharp.Float4>(device, width, height);
        var shader = new ComputeSharpDemo.Shaders.RayTrace.RayTraceShader(1, default, new(width, height), 0, 6.5f, false, 200, 1000, 10, samples, normal);
        var times = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            var watch = Stopwatch.StartNew();
            ComputeSharp.GraphicsDeviceExtensions.ForEach(device, target, shader);
            watch.Stop();
            if (i >= 100) times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"ComputeSharp raw path tracer, {width}x{height}, {samples} spp, 10 bounces, CPU wall time including submit/wait: median={times[times.Count / 2]:F4} ms; mean={times.Average():F4}; p10={times[times.Count / 10]:F4}; p90={times[times.Count * 9 / 10]:F4}; n={times.Count}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SceneConstants { public Vector2 Resolution, Mouse; public float Time, Distance; public int Frame, Pad; }
}
