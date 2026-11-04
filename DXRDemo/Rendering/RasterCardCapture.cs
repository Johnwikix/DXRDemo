using DXRDemo.DXR;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DXRDemo.Rendering;

internal sealed unsafe partial class RasterCardAtlas
{
    private ID3D12RootSignature _captureRoot=null!;
    private ID3D12PipelineState _capturePipeline=null!;
    private ID3D12DescriptorHeap _rtv=null!,_dsv=null!;
    private ID3D12Resource _depth=null!;
    private readonly ID3D12DescriptorHeap[] _captureHeaps=new ID3D12DescriptorHeap[2];
    private int _rtvStride;
    private readonly RawRect[] _clearRectangle=new RawRect[1];

    private void InitializeCapture()
    {
        _captureRoot=Keep(_device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
        [new(RootParameterType.ConstantBufferView,new RootDescriptor1(0,0),ShaderVisibility.All),
         new(new RootConstants(1,0,2),ShaderVisibility.All),
         new(RootParameterType.ConstantBufferView,new RootDescriptor1(2,0),ShaderVisibility.All),
         new(RootParameterType.ShaderResourceView,new RootDescriptor1(0,0),ShaderVisibility.All),
         new(RootParameterType.ShaderResourceView,new RootDescriptor1(1,0),ShaderVisibility.All),
         new(RootParameterType.ShaderResourceView,new RootDescriptor1(2,0),ShaderVisibility.All),
         new(RootParameterType.ShaderResourceView,new RootDescriptor1(11,0),ShaderVisibility.All),
         new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView,GpuSceneCache.MaxTextures,0,1)),ShaderVisibility.All),
         new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler,GpuSceneCache.MaxTextures,0,1)),ShaderVisibility.All)])));
        using var vertex=new DxrShaderCompiler();using var pixel=new DxrShaderCompiler();
        string file=Path.Combine(AppContext.BaseDirectory,"Shaders","DXR","RasterCardsCapture.hlsl");
        vertex.CompileGraphics(file,"CardVertex");pixel.CompileGraphics(file,"CardPixel",true);
        _capturePipeline=Keep(_device.CreateGraphicsPipelineState(new()
        {
            RootSignature=_captureRoot,VertexShader=vertex.DxilBytes,PixelShader=pixel.DxilBytes,
            PrimitiveTopologyType=PrimitiveTopologyType.Triangle,
            RenderTargetFormats=[Format.R16G16B16A16_Float,Format.R16G16B16A16_Float,Format.R16G16B16A16_Float,Format.R32G32B32A32_Float,Format.R16G16B16A16_Float],
            DepthStencilFormat=Format.D32_Float,SampleDescription=new(1,0),SampleMask=uint.MaxValue,BlendState=BlendDescription.Opaque,
            RasterizerState=new RasterizerDescription {CullMode=CullMode.None,FillMode=FillMode.Solid,FrontCounterClockwise=true,DepthClipEnable=true},
            DepthStencilState=new DepthStencilDescription {DepthEnable=true,DepthWriteMask=DepthWriteMask.All,DepthFunc=ComparisonFunction.GreaterEqual,StencilEnable=false}
        }));
        _rtv=Keep(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.RenderTargetView,5)));
        _dsv=Keep(_device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.DepthStencilView,1)));
        _rtvStride=(int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        for(int i=0;i<5;i++)_device.CreateRenderTargetView(_material[i],null,_rtv.GetCPUDescriptorHandleForHeapStart()+i*_rtvStride);
        var depth=ResourceDescription.Texture2D(Format.R32_Typeless,Width,Width,1,1);depth.Flags=ResourceFlags.AllowDepthStencil;
        _depth=Keep(_device.CreateCommittedResource(new HeapProperties(HeapType.Default),HeapFlags.None,depth,ResourceStates.DepthWrite,new ClearValue(Format.D32_Float,0,0)));
        _device.CreateDepthStencilView(_depth,new DepthStencilViewDescription {Format=Format.D32_Float,ViewDimension=DepthStencilViewDimension.Texture2D},_dsv.GetCPUDescriptorHandleForHeapStart());
    }

    private void RecordCapture(ID3D12GraphicsCommandList4 commands,ID3D12Resource constants,ID3D12DescriptorHeap textures,GpuScene scene)
    {
        if(CaptureCount==0)return;
        for(int i=0;i<5;i++)commands.ResourceBarrierTransition(_material[i],ResourceStates.NonPixelShaderResource,ResourceStates.RenderTarget);
        _captureHeaps[0]=textures;_captureHeaps[1]=scene.Samplers;commands.SetDescriptorHeaps(2,_captureHeaps);
        commands.SetGraphicsRootSignature(_captureRoot);commands.SetPipelineState(_capturePipeline);
        commands.SetGraphicsRootConstantBufferView(0,_neutralFrame.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(3,scene.Vertices.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(4,scene.Instances.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(5,scene.Materials.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(6,scene.Indices.GPUVirtualAddress);
        commands.SetGraphicsRootDescriptorTable(7,textures.GetGPUDescriptorHandleForHeapStart());
        commands.SetGraphicsRootDescriptorTable(8,scene.Samplers.GetGPUDescriptorHandleForHeapStart());
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetIndexBuffer(new IndexBufferView(scene.Indices.GPUVirtualAddress,(uint)scene.Indices.Description.Width,Format.R32_UInt));
        Span<CpuDescriptorHandle> targets=stackalloc CpuDescriptorHandle[5];
        for(int i=0;i<5;i++)targets[i]=_rtv.GetCPUDescriptorHandleForHeapStart()+i*_rtvStride;
        commands.OMSetRenderTargets(targets,_dsv.GetCPUDescriptorHandleForHeapStart());
        for(int job=0;job<CaptureCount;job++)
        {
            int slot=_captureJobs[job],x=slot%Columns*Pitch,y=slot/Columns*Pitch;
            _clearRectangle[0]=new RawRect(x,y,x+Pitch,y+Pitch);
            for(int i=0;i<5;i++)commands.ClearRenderTargetView(targets[i],new Color4(0),1,_clearRectangle);
            commands.ClearDepthStencilView(_dsv.GetCPUDescriptorHandleForHeapStart(),ClearFlags.Depth,0,0,1,_clearRectangle);
            commands.RSSetViewport(x+Padding,y+Padding,Size,Size);
            commands.RSSetScissorRect(new RawRect(x+Padding,y+Padding,x+Padding+Size,y+Padding+Size));
            commands.SetGraphicsRootConstantBufferView(2,_captureConstants.GPUVirtualAddress+(ulong)job*256);
            uint instance=_pages[slot].Instance;
            commands.SetGraphicsRoot32BitConstant(1,instance,0);commands.SetGraphicsRoot32BitConstant(1,0,1);
            var mesh=scene.Asset.Meshes[scene.Asset.Instances[instance].Mesh];var offset=scene.MeshOffsets[scene.Asset.Instances[instance].Mesh];
            commands.DrawIndexedInstanced((uint)mesh.Indices.Length,1,(uint)offset.Index,0,0);
        }
        commands.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty,null);
        for(int i=0;i<5;i++)commands.ResourceBarrierTransition(_material[i],ResourceStates.RenderTarget,ResourceStates.UnorderedAccess);
        _padMaterials.Dispatch(commands,constants,CaptureCount*Pitch,Pitch);
        for(int i=0;i<5;i++)commands.ResourceBarrierTransition(_material[i],ResourceStates.UnorderedAccess,ResourceStates.NonPixelShaderResource);
    }
}
