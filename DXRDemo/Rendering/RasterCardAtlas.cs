using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.DXR;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>固定容量的 Card 材质/光照图集；共享场景几何，反馈驱动捕获和 LRU，前后版本隔离。</summary>
internal sealed unsafe partial class RasterCardAtlas : GpuResourceOwner
{
    internal const int Size=16, Padding=4, Pitch=24, Columns=8, Capacity=64, Width=Columns*Pitch;
    internal const int Pixels=Width*Width, Mips=3, CaptureBudget=2, TriangleBudget=100000;
    internal const int QueueCapacity=256, QueueWords=517, DescriptorCount=14, MetadataStride=80;
    private readonly ID3D12Device5 _device;
    private readonly ID3D12Resource[] _material=new ID3D12Resource[5], _radiance=new ID3D12Resource[2], _stamps=new ID3D12Resource[2];
    private ID3D12Resource _metadata=null!,_queue=null!,_readback=null!,_captureConstants=null!,_neutralFrame=null!;
    private ComputePass _resetFeedback=null!,_padMaterials=null!;
    private readonly ComputePass[] _clearLight=new ComputePass[2],_padLight=new ComputePass[2];
    private readonly ComputePass[,] _mip=new ComputePass[2,Mips-1];
    private GpuInterval _captureTimer=null!,_lightTimer=null!;
    private bool _initialized;
    private readonly SceneResources _sceneResources=new();
    private sealed class SceneResources : IDisposable
    {
        internal GpuResourceOwner Current=new Owner();
        internal void Reset() {Current.Dispose();Current=new Owner();}
        public void Dispose()=>Current.Dispose();
    }
    private sealed class Owner : GpuResourceOwner { }
    private ID3D12Resource _mapping=null!,_flags=null!;
    private int[] _mappingCpu=[0];
    private readonly RasterCardData[] _pages=new RasterCardData[Capacity];
    private static readonly IComparer<uint> DescendingCounts=Comparer<uint>.Create(static(a,b)=>b.CompareTo(a));
    private readonly uint[] _weights=new uint[QueueCapacity],_importance=new uint[Capacity];
    private readonly uint[] _lastTouch=new uint[Capacity],_lastLight=new uint[Capacity],_created=new uint[Capacity];
    private readonly int[] _pageKeys=new int[Capacity],_requests=new int[QueueCapacity],_captureJobs=new int[CaptureBudget];
    private GpuScene? _scene;
    private uint _clock,_generation,_lightingEpoch;
    private int _requestCount,_current;
    private bool _dirty,_clearFlags=true,_lightInitialized,_feedbackPending,_supported=true;
    internal bool DescriptorsDirty {get;private set;}=true;
    internal bool Active {get;private set;}
    internal int CaptureCount {get;private set;}
    internal int LightSlot {get;private set;}=-1;
    internal int ReadIndex=>_current;
    internal uint Hits {get;private set;}
    internal ulong TotalHits {get;private set;}
    internal uint Misses {get;private set;}
    internal uint Rejected {get;private set;}
    internal uint QueueOverflow {get;private set;}
    internal int ResidentPages {get;private set;}
    internal uint Captures {get;private set;}
    internal double CaptureMilliseconds {get;private set;}
    internal double LightingMilliseconds {get;private set;}
    internal ID3D12Resource Positions=>_material[3];
    internal ID3D12Resource Normals=>_material[4];
    internal ID3D12Resource MaterialBase=>_material[0];
    internal ID3D12Resource Emission=>_material[2];
    internal ID3D12Resource Radiance=>_radiance[_current];
    internal ID3D12Resource Stamps=>_stamps[_current];
    internal ID3D12Resource Metadata=>_metadata;

    internal RasterCardAtlas(DxrDevice device) { _device=device.Device; }

    /// <summary>显式创建 GPU 图集和捕获管线；构造函数只保存设备引用。</summary>
    internal void Initialize(DxrDevice device)
    {
        if(_initialized)return;
        try
        {
            Keep(_sceneResources);
            _captureTimer=Keep(new GpuInterval(device));_lightTimer=Keep(new GpuInterval(device));
            for(int i=0;i<5;i++)_material[i]=Keep(Texture(i==3?Format.R32G32B32A32_Float:Format.R16G16B16A16_Float,true,1));
            _metadata=Keep(DxrResources.CreateUploadBuffer(_device,Capacity*MetadataStride));
            _captureConstants=Keep(DxrResources.CreateUploadBuffer(_device,CaptureBudget*256));
            _neutralFrame=Keep(DxrResources.CreateUploadBuffer(_device,2048));
            ClearUpload(_metadata,Capacity*MetadataStride);ClearUpload(_neutralFrame,2048);
            _queue=Keep(DxrResources.CreateDefaultBuffer(_device,QueueWords*4,ResourceFlags.AllowUnorderedAccess,ResourceStates.UnorderedAccess));
            _readback=Keep(_device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer(QueueWords*4),ResourceStates.CopyDest));
            _resetFeedback=Keep(new ComputePass(_device,"RasterCardsMaintain.hlsl","ResetFeedback",1,4));
            _resetFeedback.BufferUav(0,_queue,4,QueueWords);
            _padMaterials=Keep(new ComputePass(_device,"RasterCardsFilter.hlsl","PadMaterials",3,5));
            for(int i=0;i<5;i++)_padMaterials.Uav(i,_material[i]);
            for(int version=0;version<2;version++)
            {
                _radiance[version]=Keep(Texture(Format.R16G16B16A16_Float,false,Mips));
                _stamps[version]=Keep(DxrResources.CreateDefaultBuffer(_device,Pixels*8,ResourceFlags.AllowUnorderedAccess,ResourceStates.NonPixelShaderResource));
                _clearLight[version]=Keep(new ComputePass(_device,"RasterCardsMaintain.hlsl","ClearLight",1,4));
                _clearLight[version].UavMip(2,_radiance[version],0);_clearLight[version].BufferUav(3,_stamps[version],8,Pixels);
                _padLight[version]=Keep(new ComputePass(_device,"RasterCardsMaintain.hlsl","PadLight",1,4));
                _padLight[version].UavMip(2,_radiance[version],0);_padLight[version].BufferUav(3,_stamps[version],8,Pixels);
                for(int level=1;level<Mips;level++)
                {
                    var pass=_mip[version,level-1]=Keep(new ComputePass(_device,"RasterCardsFilter.hlsl","Mip",3,5));
                    pass.SrvMip(0,_radiance[version],(uint)level-1);pass.BufferSrv(1,_stamps[version],8,Pixels);
                    pass.BufferSrv(2,_metadata,MetadataStride,Capacity);pass.UavMip(0,_radiance[version],(uint)level);
                }
            }
            AllocateSceneBuffers(1);
            InitializeCapture();
            _initialized=true;
        }
        catch {Dispose();throw;}
    }

    private ID3D12Resource Texture(Format format,bool renderTarget,int mips)
    {
        var description=ResourceDescription.Texture2D(format,Width,Width,1,(ushort)mips);
        description.Flags=ResourceFlags.AllowUnorderedAccess|(renderTarget?ResourceFlags.AllowRenderTarget:ResourceFlags.None);
        return _device.CreateCommittedResource(new HeapProperties(HeapType.Default),HeapFlags.None,description,ResourceStates.NonPixelShaderResource);
    }
    private static void ClearUpload(ID3D12Resource resource,int bytes)
    {
        void* pointer;resource.Map(0,null,&pointer).CheckError();NativeMemory.Clear(pointer,(nuint)bytes);resource.Unmap(0);
    }
    private void AllocateSceneBuffers(int count)
    {
        _sceneResources.Reset();
        _mapping=_sceneResources.Current.Keep(DxrResources.CreateUploadBuffer(_device,(ulong)count*4));
        _flags=_sceneResources.Current.Keep(DxrResources.CreateDefaultBuffer(_device,(ulong)count*8,ResourceFlags.AllowUnorderedAccess,ResourceStates.UnorderedAccess));
        ClearUpload(_mapping,count*4);_mappingCpu=new int[count];_clearFlags=true;
        _resetFeedback.BufferUav(1,_flags,8,(uint)count);DescriptorsDirty=true;
    }
    private void SetScene(GpuScene scene)
    {
        if(ReferenceEquals(_scene,scene))return;
        _scene=scene;_supported=scene.Asset.Instances.Length<=16777216/24;
        AllocateSceneBuffers(_supported?Math.Max(scene.Asset.Instances.Length*24,1):1);
        Array.Clear(_pages);Array.Clear(_lastTouch);Array.Clear(_lastLight);Array.Clear(_created);Array.Clear(_importance);
        _requestCount=0;_feedbackPending=false;ResidentPages=0;CaptureCount=0;TotalHits=0;_dirty=true;
    }
    /// <summary>只在既有帧 fence 后读取紧凑反馈；不增加队列或独立等待。</summary>
    internal void ReadFeedback()
    {
        if(!_feedbackPending)return;
        void* pointer;_readback.Map(0,null,&pointer).CheckError();
        var values=new ReadOnlySpan<uint>(pointer,QueueWords);
        AcceptFeedback(values);
        _readback.Unmap(0);_feedbackPending=false;
        CaptureMilliseconds=_captureTimer.ReadMilliseconds();LightingMilliseconds=_lightTimer.ReadMilliseconds();
    }
    /// <summary>消费已完成帧的紧凑反馈；入口与 GPU 映射分离，便于验证丢页、延迟和 LRU 顺序。</summary>
    internal void AcceptFeedback(ReadOnlySpan<uint> values)
    {
        if(values.Length<QueueWords)throw new ArgumentException("Card feedback buffer is incomplete.",nameof(values));
        _requestCount=(int)Math.Min(values[0],QueueCapacity);QueueOverflow=values[0]>(uint)QueueCapacity?values[0]-QueueCapacity:0;
        for(int i=0;i<_requestCount;i++) {_requests[i]=(int)values[i+1];_weights[i]=Math.Max(values[1+QueueCapacity+i],1);}
        Array.Sort(_weights,_requests,0,_requestCount,DescendingCounts);
        Hits=values[1+QueueCapacity*2];Misses=values[2+QueueCapacity*2];Rejected=values[3+QueueCapacity*2];TotalHits+=Hits;
    }
    /// <summary>以固定数组处理反馈、分配和更新，稳态不创建每帧集合或描述符。</summary>
    internal void Frame(Span<Vector4> frame,GpuScene scene,bool enabled)
    {
        CaptureCount=0;LightSlot=-1;Active=false;
        if(!enabled) {frame[18]=default;return;}
        SetScene(scene);
        if(!_supported) {frame[18]=default;return;}
        Active=true;
        _clock=_clock%16777214+1;
        uint epoch=(uint)frame[15].Y;
        if(epoch!=_lightingEpoch||_clock==1)
        {
            _lightingEpoch=epoch;
            for(int slot=0;slot<Capacity;slot++)if(_pages[slot].Generation!=0) {_pages[slot].Generation=NextGeneration();_lastLight[slot]=0;}
            _dirty=true;if(_clock==1)_clearFlags=true;
        }
        for(int slot=0;slot<Capacity;slot++)_importance[slot]=_importance[slot]*7/8;
        for(int i=0;i<_requestCount;i++)
        {
            int key=_requests[i];if((uint)key>=(uint)_mappingCpu.Length)continue;
            int mapped=_mappingCpu[key];if(mapped>0) {_lastTouch[mapped-1]=_clock;_importance[mapped-1]=Math.Max(_importance[mapped-1],_weights[i]);}
        }
        int triangles=0;
        for(int i=0;i<_requestCount&&CaptureCount<CaptureBudget;i++)
        {
            int key=_requests[i];if((uint)key>=(uint)_mappingCpu.Length||_mappingCpu[key]!=0)continue;
            int instance=key/24,axis=key%24/4,layer=key%4;
            var mesh=scene.Asset.Meshes[scene.Asset.Instances[instance].Mesh];int count=mesh.Indices.Length/3;
            if(count>TriangleBudget-triangles||scene.CardBounds[scene.Asset.Instances[instance].Mesh].Minimum.W==0)continue;
            int candidate=-1;uint oldest=uint.MaxValue,leastImportance=uint.MaxValue;
            for(int slot=0;slot<Capacity;slot++)
            {
                if(_pages[slot].Generation==0) {candidate=slot;break;}
                bool eligible=_clock-_created[slot]>=8&&(_clock-_lastTouch[slot]>=8||_weights[i]>_importance[slot]*2+1);
                if(eligible&&(_importance[slot]<leastImportance||_importance[slot]==leastImportance&&_lastTouch[slot]<oldest))
                {candidate=slot;oldest=_lastTouch[slot];leastImportance=_importance[slot];}
            }
            if(candidate<0)continue;
            if(_pages[candidate].Generation!=0)_mappingCpu[_pageKeys[candidate]]=0;else ResidentPages++;
            _pages[candidate]=BuildPage(scene,instance,axis,layer);_pageKeys[candidate]=key;_mappingCpu[key]=candidate+1;
            _created[candidate]=_lastTouch[candidate]=_clock;_lastLight[candidate]=0;_importance[candidate]=_weights[i];
            _captureJobs[CaptureCount++]=candidate;triangles+=count;_dirty=true;Captures++;
        }
        _requestCount=0;
        uint best=0;
        for(int slot=0;slot<Capacity;slot++)if(_pages[slot].Generation!=0)
        {
            uint age=_lastLight[slot]==0?32+_clock-_created[slot]:_clock-_lastLight[slot];
            uint priority=age*Math.Min(1+(uint)BitOperations.Log2(_importance[slot]+1),8);
            if(LightSlot<0||priority>best) {LightSlot=slot;best=priority;}
        }
        if(LightSlot>=0)_lastLight[LightSlot]=_clock;
        if(_dirty)
        {
            void* pointer;_mapping.Map(0,null,&pointer).CheckError();_mappingCpu.AsSpan().CopyTo(new Span<int>(pointer,_mappingCpu.Length));_mapping.Unmap(0);
            _metadata.Map(0,null,&pointer).CheckError();MemoryMarshal.AsBytes(_pages.AsSpan()).CopyTo(new Span<byte>(pointer,Capacity*MetadataStride));_metadata.Unmap(0);_dirty=false;
        }
        if(CaptureCount>0)
        {
            void* pointer;_captureConstants.Map(0,null,&pointer).CheckError();
            for(int job=0;job<CaptureCount;job++)MemoryMarshal.AsBytes(_pages.AsSpan(_captureJobs[job],1)).CopyTo(new Span<byte>((byte*)pointer+job*256,MetadataStride));
            _captureConstants.Unmap(0);
        }
        frame[16]=new(_mappingCpu.Length,Capacity,Size,_clock);
        frame[17]=new(0,LightSlot,_clearFlags?1:0,0);
        frame[18]=new(CaptureCount>0?_captureJobs[0]:0,CaptureCount>1?_captureJobs[1]:0,CaptureCount,1);
    }
    private uint NextGeneration() {if(++_generation==0)_generation=1;return _generation;}
    private RasterCardData BuildPage(GpuScene scene,int instance,int axis,int layer)
    {
        int mesh=scene.Asset.Instances[instance].Mesh;var bounds=scene.CardBounds[mesh];
        Vector3 minimum=new(bounds.Minimum.X,bounds.Minimum.Y,bounds.Minimum.Z),maximum=new(bounds.Maximum.X,bounds.Maximum.Y,bounds.Maximum.Z);
        Vector3 center=(minimum+maximum)*.5f,extent=(maximum-minimum)*.5f;
        float longest=MathF.Max(extent.X,MathF.Max(extent.Y,extent.Z));int a=axis/2;
        int layers=extent[a]>longest*1e-4f?4:1;
        center[a]=minimum[a]+(layer+.5f)*(maximum[a]-minimum[a])/layers;extent[a]/=layers;
        Vector3 n=a==0?Vector3.UnitX:a==1?Vector3.UnitY:Vector3.UnitZ;n*=axis%2==0?1:-1;
        Vector3 u=a==0?new(0,0,axis%2==0?-1:1):a==1?Vector3.UnitX:new(axis%2==0?1:-1,0,0);
        Vector3 v=Vector3.Cross(n,u);float tiny=MathF.Max(longest*1e-5f,1e-6f);
        float hu=MathF.Max(Vector3.Dot(Vector3.Abs(u),extent),tiny),hv=MathF.Max(Vector3.Dot(Vector3.Abs(v),extent),tiny),hn=MathF.Max(Vector3.Dot(Vector3.Abs(n),extent),tiny);
        Matrix4x4 transform=scene.Asset.Instances[instance].Transform;
        float texel=2*MathF.Max(Vector3.TransformNormal(u*hu,transform).Length(),Vector3.TransformNormal(v*hv,transform).Length())/Size;
        return new() {Instance=(uint)instance,Mesh=(uint)mesh,Axis=(uint)axis,Generation=NextGeneration(),Center=new(center,texel),U=new(u,hu),V=new(v,hv),N=new(n,hn)};
    }

    internal void WriteDescriptors(CpuDescriptorHandle start,int stride,int version,GpuScene? scene)
    {
        void Buffer(int index,ID3D12Resource? resource,uint size,uint count)=>_device.CreateShaderResourceView(resource,
            new ShaderResourceViewDescription {Format=Format.Unknown,ViewDimension=ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping=ShaderComponentMapping.Default,Buffer=new() {NumElements=count,StructureByteStride=size}},start+index*stride);
        Buffer(0,_mapping,4,(uint)_mappingCpu.Length);Buffer(1,_metadata,MetadataStride,Capacity);
        for(int i=0;i<5;i++)_device.CreateShaderResourceView(_material[i],null,start+(i+2)*stride);
        _device.CreateShaderResourceView(_radiance[version],null,start+7*stride);
        Buffer(8,_stamps[version],8,Pixels);Buffer(9,scene?.CardInstanceBounds,32,(uint)Math.Max(scene?.Asset.Instances.Length??1,1));
        void UavBuffer(int index,ID3D12Resource resource,uint size,uint count)=>_device.CreateUnorderedAccessView(resource,null,
            new UnorderedAccessViewDescription {Format=Format.Unknown,ViewDimension=UnorderedAccessViewDimension.Buffer,Buffer=new() {NumElements=count,StructureByteStride=size}},start+(10+index)*stride);
        UavBuffer(0,_queue,4,QueueWords);UavBuffer(1,_flags,8,(uint)_mappingCpu.Length);
        _device.CreateUnorderedAccessView(_radiance[1-version],null,null,start+12*stride);UavBuffer(3,_stamps[1-version],8,Pixels);
    }
    internal void DescriptorsWritten()=>DescriptorsDirty=false;
    internal void Begin(ID3D12GraphicsCommandList4 commands,ID3D12Resource constants,ID3D12DescriptorHeap textures,GpuScene scene)
    {
        if(!_lightInitialized)
        {
            for(int version=0;version<2;version++)
            {
                commands.ResourceBarrierTransition(_radiance[version],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess,0);
                commands.ResourceBarrierTransition(_stamps[version],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess);
                _clearLight[version].Dispatch(commands,constants,Width,Width);
                commands.ResourceBarrierTransition(_radiance[version],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource,0);
                commands.ResourceBarrierTransition(_stamps[version],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
            }
            _lightInitialized=true;
        }
        int resetCount=_clearFlags?Math.Max(_mappingCpu.Length,QueueWords):QueueWords;
        _resetFeedback.Dispatch(commands,constants,Math.Min(resetCount,4096),(resetCount+4095)/4096);
        commands.ResourceBarrierUnorderedAccessView(_queue);commands.ResourceBarrierUnorderedAccessView(_flags);_clearFlags=false;
        _captureTimer.Begin(commands);RecordCapture(commands,constants,textures,scene);_captureTimer.End(commands);
        for(int i=0;i<2;i++)
        {
            var source=i==0?_radiance[_current]:_stamps[_current];var destination=i==0?_radiance[1-_current]:_stamps[1-_current];
            commands.ResourceBarrierTransition(source,ResourceStates.NonPixelShaderResource,ResourceStates.CopySource);
            commands.ResourceBarrierTransition(destination,ResourceStates.NonPixelShaderResource,ResourceStates.CopyDest);
            commands.CopyResource(destination,source);
            commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.NonPixelShaderResource);
            commands.ResourceBarrierTransition(destination,ResourceStates.CopyDest,ResourceStates.NonPixelShaderResource);
        }
        commands.ResourceBarrierTransition(_radiance[1-_current],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess,0);
        commands.ResourceBarrierTransition(_stamps[1-_current],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess);
        _lightTimer.Begin(commands);
    }
    internal void EndLighting(ID3D12GraphicsCommandList4 commands,ID3D12Resource constants)
    {
        int next=1-_current;
        commands.ResourceBarrierUnorderedAccessView(_radiance[next]);commands.ResourceBarrierUnorderedAccessView(_stamps[next]);
        if(LightSlot>=0)_padLight[next].Dispatch(commands,constants,Pitch,Pitch);
        commands.ResourceBarrierTransition(_radiance[next],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource,0);
        commands.ResourceBarrierTransition(_stamps[next],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
        for(uint level=1;level<Mips;level++)
        {
            commands.ResourceBarrierTransition(_radiance[next],ResourceStates.NonPixelShaderResource,ResourceStates.UnorderedAccess,level);
            _mip[next,level-1].Dispatch(commands,constants,Width>>((int)level),Width>>((int)level));
            commands.ResourceBarrierTransition(_radiance[next],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource,level);
        }
        _lightTimer.End(commands);
    }
    internal void PublishFeedback(ID3D12GraphicsCommandList4 commands)
    {
        commands.ResourceBarrierTransition(_queue,ResourceStates.UnorderedAccess,ResourceStates.CopySource);commands.CopyResource(_readback,_queue);
        commands.ResourceBarrierTransition(_queue,ResourceStates.CopySource,ResourceStates.UnorderedAccess);
        _feedbackPending=true;_current=1-_current;
    }
}
