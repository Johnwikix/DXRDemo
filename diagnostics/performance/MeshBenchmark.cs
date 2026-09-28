using ComputeSharp;
using ComputeSharp.Descriptors;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DXRDemo.DXR;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

static unsafe class MeshBenchmark
{
    static string Source<T>() where T : struct, IComputeShader, IComputeShaderDescriptor<T> => T.HlslSource;
    static ReadOnlyMemory<byte> Bytecode<T>() where T : struct, IComputeShader, IComputeShaderDescriptor<T> => T.HlslBytecode;
    public static void Run(string[] args)
    {
        Directory.CreateDirectory("mesh-output");
        string source = Source<MeshShader>();
        File.WriteAllText("mesh-output/generated.hlsl", source);
        if (args.Length == 0) { Console.WriteLine($"Exported ComputeSharp HLSL, embedded DXIL {Bytecode<MeshShader>().Length} bytes"); return; }
        string model = args[0];
        int width = args.Length > 1 ? int.Parse(args[1]) : 1280;
        int height = args.Length > 2 ? int.Parse(args[2]) : 720;
        int samples = args.Length > 3 ? int.Parse(args[3]) : 4;
        int bounces = args.Length > 4 ? int.Parse(args[4]) : 10;
        using var reader = new BinaryReader(File.OpenRead($"models/{model}.mesh"));
        int triangleCount = reader.ReadInt32(), nodeCount = reader.ReadInt32();
        var triangles = MemoryMarshal.Cast<byte,Float4>(reader.ReadBytes(triangleCount * 48)).ToArray();
        var nodes = MemoryMarshal.Cast<byte,Float4>(reader.ReadBytes(nodeCount * 48)).ToArray();
        string prefix = $"mesh-output/{model}-{width}x{height}-{samples}spp";
        Console.WriteLine($"Model={model}, triangles={triangleCount}, nodes={nodeCount}, {width}x{height}, spp={samples}, maxBounces={bounces}");

        // The material, PRNG, camera, path integration and output are literally the
        // ComputeSharp-generated source. Only HitMesh and entry-point plumbing change.
        int bodyStart = source.IndexOf("bool HitMesh(", source.IndexOf("bool HitMesh(") + 1);
        int bodyEnd = source.IndexOf("static float3 Sky(float3 rd)\n{",bodyStart);
        string hardwareHit = """
        struct HitPayload { float t; int id; };
        RaytracingAccelerationStructure SceneBVH : register(t2);
        bool HitMesh(float3 ro,float3 rd,float tMax,bool anyHit,out float bestT,out int bestId) {
            RayDesc ray; ray.Origin=ro; ray.Direction=rd; ray.TMin=0.0001; ray.TMax=tMax;
            HitPayload hit; hit.t=tMax; hit.id=-1;
            uint flags = anyHit ? RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH : RAY_FLAG_NONE;
            TraceRay(SceneBVH,flags,0xFF,0,0,0,ray,hit);
            bestT=hit.t; bestId=hit.id; return hit.id>=0;
        }
        [shader("closesthit")]
        void ClosestHit(inout HitPayload hit, BuiltInTriangleIntersectionAttributes attr) { hit.t=RayTCurrent(); hit.id=PrimitiveIndex(); }
        [shader("miss")]
        void Miss(inout HitPayload hit) { hit.id=-1; }

        """;
        string rtSource = source[..bodyStart] + hardwareHit + source[bodyEnd..];
        rtSource = Regex.Replace(rtSource, @"\[NumThreads\([^\r\n]*\)\]\s*void Execute\(uint3 ThreadIds : SV_DispatchThreadID\)\s*\{", "[shader(\"raygeneration\")]\nvoid RayGen() { uint3 ThreadIds=DispatchRaysIndex();");
        File.WriteAllText("mesh-output/hardware.hlsl", rtSource);
        byte[] rtCode = Compile("mesh-output/hardware.hlsl", "lib_6_5", null);
        byte[] smCode = Bytecode<MeshShader>().ToArray();
        if (smCode.Length == 0) smCode = Compile("mesh-output/generated.hlsl", "cs_6_0", "Execute");

        var all = new List<object>();
        using (var renderer = new MeshGpu(triangles, nodes, width, height, samples, bounces, rtCode, smCode))
        {
            // Bracket the software pass with repeat hardware measurements.
            all.Add(renderer.Measure(true, prefix, "dxr-a"));
            all.Add(renderer.Measure(false, prefix, "sm-bytecode"));
            all.Add(renderer.Measure(true, prefix, "dxr-b"));
            renderer.Render(true, diagnostic: 1); renderer.Save(prefix + "-dxr-primary.f32");
            renderer.Render(false, diagnostic: 1); renderer.Save(prefix + "-sm-primary.f32");
        }
        var device = GraphicsDevice.GetDefault();
        using var triBuffer = device.AllocateReadOnlyBuffer(triangles);
        using var nodeBuffer = device.AllocateReadOnlyBuffer(nodes);
        using var target = device.AllocateReadWriteTexture2D<Float4>(width,height);
        var shader = new MeshShader(width,height,samples,bounces,0,0,triBuffer,nodeBuffer,target);
        var walls = new List<double>();
        var warmup=Stopwatch.StartNew();
        do { device.For(width,height,shader); } while(warmup.Elapsed.TotalMilliseconds < 750);
        for (int i=0;i<60;i++)
        {
            long t = Stopwatch.GetTimestamp();
            device.For(width,height,shader);
            walls.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        var actual = new { backend="ComputeSharp.For", wall=Stats(walls) };
        all.Add(actual); Console.WriteLine(JsonSerializer.Serialize(actual));
        var pixels=target.ToArray();
        File.WriteAllBytes(prefix + "-computesharp.f32",MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref pixels[0,0],pixels.Length)).ToArray());
        File.WriteAllText(prefix+"-results.json", JsonSerializer.Serialize(new {model,triangleCount,nodeCount,width,height,samples,bounces, results=all},new JsonSerializerOptions{WriteIndented=true}));
    }

    static object Stats(List<double> data) { data.Sort(); return new { median_ms=data[data.Count/2], mean_ms=data.Average(), p10_ms=data[data.Count/10], p90_ms=data[data.Count*9/10], n=data.Count }; }

    static byte[] Compile(string path,string profile,string? entry)
    {
        string dxc=Environment.GetEnvironmentVariable("DXR_DXC_PATH") ?? Path.GetFullPath("../../DXRDemo/bin/x64/Release/net10.0-windows10.0.22621.0/dxc.exe");
        var info=new ProcessStartInfo(dxc){UseShellExecute=false,CreateNoWindow=true};
        foreach(string arg in new[]{"-T",profile,"-O3","-Fo",path+".dxil",path})info.ArgumentList.Add(arg);
        if(entry!=null){info.ArgumentList.Add("-E");info.ArgumentList.Add(entry);}
        using var proc=Process.Start(info)!;proc.WaitForExit();
        if(proc.ExitCode!=0)throw new Exception("DXC compilation failed: "+path);
        return File.ReadAllBytes(path+".dxil");
    }

    sealed class MeshGpu : IDisposable
    {
        readonly DxrDevice gpu=new();
        readonly List<IDisposable> owned=[];
        readonly ID3D12RootSignature root;
        readonly ID3D12PipelineState sm;
        readonly ID3D12StateObject rt;
        readonly DxrShaderTable table=new();
        readonly ID3D12Resource triangleBuffer,nodeBuffer,tlas,output,constants,readback;
        readonly ID3D12DescriptorHeap heap;
        readonly ID3D12QueryHeap queries;
        readonly int width,height,samples,bounces;
        readonly ulong frequency;
        readonly DispatchRaysDescription dispatch;
        T Keep<T>(T value) where T:IDisposable {owned.Add(value);return value;}
        public MeshGpu(Float4[] triangles,Float4[] nodes,int width,int height,int samples,int bounces,byte[] rtCode,byte[] smCode)
        {
            this.width=width;this.height=height;this.samples=samples;this.bounces=bounces;
            gpu.Initialize();
            long luid=gpu.Device.AdapterLuid;
            using(var adapter=gpu.DxgiFactory.EnumAdapterByLuid<IDXGIAdapter1>(*(Vortice.Luid*)&luid))Console.WriteLine("GPU="+adapter.Description1.Description);
            triangleBuffer=Upload(MemoryMarshal.AsBytes(triangles.AsSpan()));
            nodeBuffer=Upload(MemoryMarshal.AsBytes(nodes.AsSpan()));
            var vertices=new System.Numerics.Vector3[triangles.Length];
            for(int i=0;i<triangles.Length;i+=3)
            {
                var p=new System.Numerics.Vector3(triangles[i].X,triangles[i].Y,triangles[i].Z);
                vertices[i]=p;
                vertices[i+1]=p+new System.Numerics.Vector3(triangles[i+1].X,triangles[i+1].Y,triangles[i+1].Z);
                vertices[i+2]=p+new System.Numerics.Vector3(triangles[i+2].X,triangles[i+2].Y,triangles[i+2].Z);
            }
            var vertexBuffer=Upload(MemoryMarshal.AsBytes(vertices.AsSpan()));
            var geoms=new[]{new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(new GpuVirtualAddressAndStride(vertexBuffer.GPUVirtualAddress,12),Format.R32G32B32_Float,(uint)vertices.Length,0,0,Format.Unknown,0),RaytracingGeometryFlags.Opaque)};
            var blasInputs=new BuildRaytracingAccelerationStructureInputs {Type=RaytracingAccelerationStructureType.BottomLevel,Flags=RaytracingAccelerationStructureBuildFlags.PreferFastTrace,DescriptorsCount=1,Layout=ElementsLayout.Array,GeometryDescriptions=geoms};
            var blasInfo=gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(blasInputs);
            var blas=Keep(DxrResources.CreateDefaultBuffer(gpu.Device,blasInfo.ResultDataMaxSizeInBytes,ResourceFlags.AllowUnorderedAccess,ResourceStates.RaytracingAccelerationStructure));
            var scratch=Keep(DxrResources.CreateDefaultBuffer(gpu.Device,blasInfo.ScratchDataSizeInBytes,ResourceFlags.AllowUnorderedAccess,ResourceStates.UnorderedAccess));
            Begin(); gpu.CommandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(blas.GPUVirtualAddress,blasInputs,0,scratch.GPUVirtualAddress));gpu.CommandList.ResourceBarrierUnorderedAccessView(blas);End();
            var instances=Keep(DxrResources.CreateUploadBuffer(gpu.Device,64));
            void* ptr; instances.Map(0,null,&ptr).CheckError();NativeMemory.Clear(ptr,64);
            ((float*)ptr)[0]=((float*)ptr)[5]=((float*)ptr)[10]=1;((byte*)ptr)[51]=255;*(ulong*)((byte*)ptr+56)=blas.GPUVirtualAddress;instances.Unmap(0);
            var tlasInputs=new BuildRaytracingAccelerationStructureInputs {Type=RaytracingAccelerationStructureType.TopLevel,Flags=RaytracingAccelerationStructureBuildFlags.PreferFastTrace,DescriptorsCount=1,Layout=ElementsLayout.Array,InstanceDescriptions=instances.GPUVirtualAddress};
            var tlasInfo=gpu.Device.GetRaytracingAccelerationStructurePrebuildInfo(tlasInputs);
            tlas=Keep(DxrResources.CreateDefaultBuffer(gpu.Device,tlasInfo.ResultDataMaxSizeInBytes,ResourceFlags.AllowUnorderedAccess,ResourceStates.RaytracingAccelerationStructure));
            var tScratch=Keep(DxrResources.CreateDefaultBuffer(gpu.Device,tlasInfo.ScratchDataSizeInBytes,ResourceFlags.AllowUnorderedAccess,ResourceStates.UnorderedAccess));
            Begin();gpu.CommandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(tlas.GPUVirtualAddress,tlasInputs,0,tScratch.GPUVirtualAddress));gpu.CommandList.ResourceBarrierUnorderedAccessView(tlas);End();
            var parameters=new RootParameter1[]{
                new(RootParameterType.ConstantBufferView,new RootDescriptor1(0,0),ShaderVisibility.All),
                new(RootParameterType.ShaderResourceView,new RootDescriptor1(0,0),ShaderVisibility.All),
                new(RootParameterType.ShaderResourceView,new RootDescriptor1(1,0),ShaderVisibility.All),
                new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView,1,0,0)),ShaderVisibility.All),
                new(RootParameterType.ShaderResourceView,new RootDescriptor1(2,0),ShaderVisibility.All)};
            string err=D3D12.D3D12SerializeVersionedRootSignature(new VersionedRootSignatureDescription(new RootSignatureDescription1(RootSignatureFlags.None,parameters)),out Blob blob);
            if(!string.IsNullOrEmpty(err))throw new Exception(err);
            using(blob)root=Keep(gpu.Device.CreateRootSignature<ID3D12RootSignature>(blob.BufferPointer,blob.BufferSize));
            sm=Keep(gpu.Device.CreateComputePipelineState(new ComputePipelineStateDescription{RootSignature=root,ComputeShader=smCode}));
            rt=Keep(gpu.Device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(StateObjectType.RaytracingPipeline,new StateSubObject[]{
                new(new DxilLibraryDescription(rtCode,new[]{new ExportDescription("RayGen"),new ExportDescription("ClosestHit"),new ExportDescription("Miss")})),
                new(new HitGroupDescription("HitGroup_Sphere",HitGroupType.Triangles,closestHitShaderImport:"ClosestHit")),
                new(new GlobalRootSignature(root)),new(new RaytracingPipelineConfig(1)),new(new RaytracingShaderConfig(8,8))})));
            using(var props=rt.QueryInterface<ID3D12StateObjectProperties>())table.Build(gpu.Device,props);
            output=Keep(DxrResources.CreateUnorderedAccessTexture2D(gpu.Device,(uint)width,(uint)height,Format.R32G32B32A32_Float,ResourceStates.UnorderedAccess));
            constants=Keep(DxrResources.CreateUploadBuffer(gpu.Device,256));
            heap=Keep(gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,1,DescriptorHeapFlags.ShaderVisible)));
            gpu.Device.CreateUnorderedAccessView(output,null,null,heap.GetCPUDescriptorHandleForHeapStart());
            queries=Keep(gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp,2)));
            readback=Keep(gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer(16),ResourceStates.CopyDest));
            gpu.CommandQueue.GetTimestampFrequency(out frequency).CheckError();
            dispatch=new(new(table.Buffer.GPUVirtualAddress,64),new(table.Buffer.GPUVirtualAddress+64,64,64),new(table.Buffer.GPUVirtualAddress+128,64,64),default,(uint)width,(uint)height,1);
        }
        ID3D12Resource Upload(ReadOnlySpan<byte> data)
        {
            using var stage=DxrResources.CreateUploadBuffer(gpu.Device,(ulong)data.Length);
            void* p;stage.Map(0,null,&p).CheckError();data.CopyTo(new Span<byte>(p,data.Length));stage.Unmap(0);
            var dest=Keep(DxrResources.CreateDefaultBuffer(gpu.Device,(ulong)data.Length,ResourceFlags.None,ResourceStates.CopyDest));
            Begin();gpu.CommandList.CopyBufferRegion(dest,0,stage,0,(ulong)data.Length);gpu.CommandList.ResourceBarrierTransition(dest,ResourceStates.CopyDest,ResourceStates.NonPixelShaderResource);End();return dest;
        }
        void Begin(){gpu.CommandAllocator.Reset();gpu.CommandList.Reset(gpu.CommandAllocator);}
        void End(){gpu.CommandList.Close();gpu.CommandQueue.ExecuteCommandList(gpu.CommandList);gpu.SignalAndWait();}
        public double Render(bool hardware,int diagnostic=0)
        {
            void* ptr;constants.Map(0,null,&ptr).CheckError();
            var c=new Span<int>(ptr,9);c[0]=width;c[1]=height;c[2]=1;c[3]=width;c[4]=height;c[5]=samples;c[6]=bounces;c[7]=0;c[8]=diagnostic;constants.Unmap(0);
            Begin();var cmd=gpu.CommandList;
            cmd.SetDescriptorHeaps(heap);cmd.SetComputeRootSignature(root);
            cmd.SetComputeRootConstantBufferView(0,constants.GPUVirtualAddress);cmd.SetComputeRootShaderResourceView(1,triangleBuffer.GPUVirtualAddress);cmd.SetComputeRootShaderResourceView(2,nodeBuffer.GPUVirtualAddress);cmd.SetComputeRootDescriptorTable(3,heap.GetGPUDescriptorHandleForHeapStart());cmd.SetComputeRootShaderResourceView(4,tlas.GPUVirtualAddress);
            if(hardware)cmd.SetPipelineState1(rt);else cmd.SetPipelineState(sm);
            cmd.ResourceBarrierUnorderedAccessView(output);cmd.EndQuery(queries,QueryType.Timestamp,0);
            if(hardware)cmd.DispatchRays(dispatch);else cmd.Dispatch((uint)(width+7)/8,(uint)(height+7)/8,1);
            cmd.EndQuery(queries,QueryType.Timestamp,1);cmd.ResolveQueryData(queries,QueryType.Timestamp,0,2,readback,0);End();
            readback.Map(0,null,&ptr).CheckError();double time=(((ulong*)ptr)[1]-((ulong*)ptr)[0])*1000.0/frequency;readback.Unmap(0);return time;
        }
        public object Measure(bool hardware,string prefix,string label)
        {
            var gpuTimes=new List<double>();var wallTimes=new List<double>();
            var warmup=Stopwatch.StartNew();
            do {Render(hardware);} while(warmup.Elapsed.TotalMilliseconds<750);
            for(int i=0;i<60;i++) {long begin=Stopwatch.GetTimestamp();double t=Render(hardware);double wall=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;gpuTimes.Add(t);wallTimes.Add(wall);}
            var result=new{backend=label,gpu=Stats(gpuTimes),wall=Stats(wallTimes)};
            Console.WriteLine(JsonSerializer.Serialize(result));Save(prefix+"-"+label+".f32");return result;
        }
        public void Save(string path)
        {
            uint pitch=((uint)width*16+255)&~255u;
            using var buffer=gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer((ulong)pitch*(uint)height),ResourceStates.CopyDest);
            Begin();gpu.CommandList.ResourceBarrierTransition(output,ResourceStates.UnorderedAccess,ResourceStates.CopySource);
            var footprint=new PlacedSubresourceFootPrint{Offset=0,Footprint=new SubresourceFootPrint{Format=Format.R32G32B32A32_Float,Width=(uint)width,Height=(uint)height,Depth=1,RowPitch=pitch}};
            gpu.CommandList.CopyTextureRegion(new TextureCopyLocation(buffer,footprint),0,0,0,new TextureCopyLocation(output,0));gpu.CommandList.ResourceBarrierTransition(output,ResourceStates.CopySource,ResourceStates.UnorderedAccess);End();
            void* p;buffer.Map(0,null,&p).CheckError();using(var file=File.Create(path)){for(int row=0;row<height;row++)file.Write(new ReadOnlySpan<byte>((byte*)p+row*pitch,width*16));}buffer.Unmap(0);
        }
        public void Dispose(){gpu.SignalAndWait();table.Dispose();for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();gpu.Dispose();}
    }
}
