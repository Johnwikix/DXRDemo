using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;

/// <summary>以实际捕获/查询验证 Card 覆盖、图集隔离、版本失效和有界页池。</summary>
internal static unsafe class RasterCardsProbe
{
    internal static void Run(DxrDevice device,RasterBackend backend)
    {
        RasterProbe.Require(Marshal.SizeOf<RasterCardData>()==80&&Marshal.SizeOf<RasterCardBounds>()==32,"Card CPU/HLSL metadata layout mismatch");
        var material=SceneMaterial.Default;material.BaseColor=new(.8f,.1f,.05f,1);material.Factors=new(0,1,1,1);
        var camera=CameraFrame.Look(new(0,0,3),-Vector3.UnitZ,.6f,.01f,100);
        SceneAsset Environment(SceneAsset a)=>a with {EnvironmentMap=new(8,4,Enumerable.Repeat(new Vector4(.5f,.5f,.5f,0),32).ToArray())};
        var asset=Environment(RasterProbe.Fixture(material));
        void Warm(SceneAsset a,int frames=32)
        {
            backend.SetScene(a);
            for(int i=0;i<frames;i++)backend.Render(camera,camera,default,default,64,48,1.0/60,i==0,1,default);
        }
        Warm(asset);
        var cards=backend.CardAtlas!;
        Console.WriteLine($"Card warmup: resident={cards.ResidentPages}, captures={cards.Captures}, total hits={cards.TotalHits}, last hits/misses/rejects={cards.Hits}/{cards.Misses}/{cards.Rejected}");
        RasterProbe.Require(cards.Active&&cards.ResidentPages>0&&cards.TotalHits>0,"Cards did not capture and serve actual world-probe hits");
        var metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,RasterCardAtlas.Capacity,ResourceStates.GenericRead);
        int slot=Array.FindIndex(metadata,c=>c.Generation>0&&c.Axis==4);
        RasterProbe.Require(slot>=0,"Front-facing Card was not allocated");
        var positions=ReconstructionProbe.ReadTexture(device.Device,cards.Positions,false);
        var colors=ReconstructionProbe.ReadTexture(device.Device,cards.MaterialBase,true);
        var normals=ReconstructionProbe.ReadTexture(device.Device,cards.Normals,true);
        int sx=slot%8*24,sy=slot/8*24;double error=0;
        for(int y=0;y<24;y++)for(int x=0;x<24;x++)
        {
            int k=((sy+y)*192+sx+x)*4;
            float expectedX=-1+(Math.Clamp(x-4,0,15)+.5f)/8,expectedY=1-(Math.Clamp(y-4,0,15)+.5f)/8;
            error=Math.Max(error,Math.Abs(positions[k]-expectedX)+Math.Abs(positions[k+1]-expectedY)+Math.Abs(positions[k+2]));
            RasterProbe.Require(positions[k+3]>0&&normals[k+2]>.999&&Math.Abs(colors[k]-.8f)<.001,"Card capture/padding lost material, front-face, or geometry normal");
        }
        RasterProbe.Require(error<1e-5,"Card orthographic position/padding mismatch");
        for(uint mip=0;mip<3;mip++)
        {
            var rgba=ReadMip(device,cards.Radiance,mip);int w=192>>(int)mip,pitch=24>>(int)mip;
            for(int y=0;y<w;y++)for(int x=0;x<w;x++)
            {
                int page=y/pitch*8+x/pitch;float alpha=rgba[(y*w+x)*4+3];
                RasterProbe.Require(float.IsFinite(rgba[(y*w+x)*4])&&alpha==(metadata[page].Generation>0?1:0),"Card mip crossed a tile boundary or retained invalid coverage");
            }
        }
        uint captures=cards.Captures,generation=metadata[slot].Generation;
        backend.Render(camera,camera,default,default,65,49,1.0/60,true,1,default);
        RasterProbe.Require(cards.Captures==captures&&ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead)[slot].Generation==generation,"Resize/cut discarded stable Card geometry");
        var dark=backend.Render(camera,camera,default,default,64,48,1.0/60,true,0,default);
        var darkPixels=ReconstructionProbe.ReadTexture(device.Device,dark.Color,true);
        RasterProbe.Require(cards.Captures==captures&&ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead)[slot].Generation!=generation&&darkPixels.Where((_,i)=>i%4!=3).Max()<1e-5,"Lighting invalidation reused stale Card radiance or recaptured immutable geometry");
        Console.WriteLine($"PASS: actual Card hits, orthographic capture/padding max position error={error:R}, isolated mip0/1/2, cut/resize retention and light invalidation.");

        var hdr=material;hdr.Emissive=new(500000,100000,50000,0);
        Warm(Environment(RasterProbe.Fixture(hdr)));
        metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);slot=Array.FindIndex(metadata,c=>c.Generation>0&&c.Axis==4);
        var capturedEmission=ReconstructionProbe.ReadTexture(device.Device,cards.Emission,true);
        int hdrCenter=((slot/8*24+12)*192+slot%8*24+12)*4;
        var hdrLight=ReadMip(device,cards.Radiance,0);
        RasterProbe.Require(slot>=0&&float.IsFinite(capturedEmission[hdrCenter])&&capturedEmission[hdrCenter]>64000&&hdrLight[hdrCenter]<10,"HDR capture overflowed half precision or emission was stored as reflected diffuse lighting");
        Console.WriteLine($"PASS: HDR captured emission={capturedEmission[hdrCenter]:R}, diffuse radiance={hdrLight[hdrCenter]:R}; emission kept separate.");

        var blue=material;blue.BaseColor=new(.02f,.1f,.8f,1);
        var transformed=Environment(RasterProbe.Fixture(blue)) with {Instances=[new("negative nonuniform",0,Matrix4x4.CreateScale(-1.4f,.7f,2)*Matrix4x4.CreateTranslation(.2f,-.1f,.1f))]};
        Warm(transformed);
        metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);
        positions=ReconstructionProbe.ReadTexture(device.Device,cards.Positions,false);colors=ReconstructionProbe.ReadTexture(device.Device,cards.MaterialBase,true);
        slot=Array.FindIndex(metadata,c=>c.Generation>0&&c.Axis==4);RasterProbe.Require(slot>=0&&cards.TotalHits>0,"Negative-scale Card front-face/query contract failed");
        int center=((slot/8*24+12)*192+slot%8*24+12)*4;
        RasterProbe.Require(Math.Abs(positions[center+2]-.1f)<1e-5&&Math.Abs(colors[center+2]-.8f)<.001&&colors[center]<.03,"Scene identity or transformed Card material retained another scene");

        var bytes=new byte[16*16*4];
        for(int y=0;y<16;y++)for(int x=0;x<16;x++) {int k=(y*16+x)*4;bytes[k]=bytes[k+1]=bytes[k+2]=255;bytes[k+3]=(byte)((x&1)==0?255:0);}
        var masked=material;masked.Flags.X=1;masked.BaseColorMap=new() {Info=new(0,0,10497,10497),Transform=new(0,0,1,1),Extra=new(0,1,0,16)};
        var holes=Environment(RasterProbe.Fixture(masked)) with {Textures=[new("mask",16,16,[bytes],false,MinFilter:9728,MagFilter:9728)]};
        Warm(holes);
        metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);slot=Array.FindIndex(metadata,c=>c.Generation>0&&c.Axis==4);
        RasterProbe.Require(slot>=0,"MASK Card missing");positions=ReconstructionProbe.ReadTexture(device.Device,cards.Positions,false);
        int coverage=0;for(int y=4;y<20;y++)for(int x=4;x<20;x++)if(positions[((slot/8*24+y)*192+slot%8*24+x)*4+3]!=0)coverage++;
        RasterProbe.Require(coverage==128,"MASK capture filled holes or lost authored alpha");
        var coarse=ReadMip(device,cards.Radiance,1);
        for(int y=2;y<10;y++)for(int x=2;x<10;x++)RasterProbe.Require(coarse[((slot/8*12+y)*96+slot%8*12+x)*4+3]==0,"Mip filtering averaged valid surfaces over MASK holes");
        Console.WriteLine($"PASS: negative/nonuniform transform, scene identity, MASK coverage={coverage}/256 and invalid-hole mips.");

        var quad=asset.Meshes[0];var vertices=new List<SceneVertex>();var indices=new List<uint>();
        foreach(float depth in new[]{-1.5f,-.5f,.5f,1.5f})
        {
            uint offset=(uint)vertices.Count;
            foreach(var source in quad.Vertices) {var v=source;v.Position=new(source.Position.X*4,source.Position.Y*4,depth,1);vertices.Add(v);}
            foreach(uint index in quad.Indices)indices.Add(offset+index);
        }
        var layered=asset with {Meshes=[quad with {Vertices=vertices.ToArray(),Indices=indices.ToArray()}],Minimum=new(-4,-4,-1.5f),Maximum=new(4,4,1.5f)};
        Warm(layered,48);metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);
        var z=metadata.Where(c=>c.Generation>0&&c.Axis==4).Select(c=>c.Center.Z).Order().ToArray();
        RasterProbe.Require(z.Length==4&&z.Zip(new[]{-1.125f,-.375f,.375f,1.125f}).All(p=>Math.Abs(p.First-p.Second)<1e-5),"Layered/concave surface Card depth regions were not captured independently");
        Console.WriteLine("PASS: four hidden depth regions captured independently in one mesh.");
        Lru(device,backend,asset,camera);
    }

    private static void Lru(DxrDevice device,RasterBackend backend,SceneAsset asset,CameraFrame camera)
    {
        var instances=Enumerable.Range(0,68).Select(i=>new SceneInstance($"card {i}",0,Matrix4x4.CreateScale(.2f)*Matrix4x4.CreateTranslation((i%10-5)*.5f,(i/10-3)*.5f,0))).ToArray();
        backend.SetScene(asset with {Instances=instances,Minimum=new(-3,-2,0),Maximum=new(3,2,0)});
        backend.Render(camera,camera,default,default,64,48,1.0/60,true,1,default);
        var cards=backend.CardAtlas!;var queue=new uint[RasterCardAtlas.QueueWords];queue[0]=2;
        for(int i=0;i<34;i++)
        {
            queue[1]=(uint)(i*2*24+16);queue[2]=(uint)((i*2+1)*24+16);cards.AcceptFeedback(queue);
            uint before=cards.Captures;
            backend.Render(camera,camera,default,default,64,48,1.0/60,false,1,default);
            RasterProbe.Require(cards.Captures-before<=2&&cards.ResidentPages<=64,"Card capture/residency exceeded fixed budget");
        }
        var metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);
        RasterProbe.Require(cards.ResidentPages==64&&metadata.Any(c=>c.Instance>=64),"LRU did not evict old pages for new requests");
        var coarse=ReadMip(device,cards.Radiance,1);var stamps=ReadBuffer<ulong>(device,cards.Stamps,RasterCardAtlas.Pixels,ResourceStates.NonPixelShaderResource);
        int invalidated=0;
        for(int slot=0;slot<64;slot++)if(metadata[slot].Instance>=64)
        {
            int basePixel=(slot/8*24+12)*192+slot%8*24+12;
            if((uint)stamps[basePixel]!=metadata[slot].Generation)
            {
                invalidated++;
                RasterProbe.Require(coarse[((slot/8*12+6)*96+slot%8*12+6)*4+3]==0,"Reused tile retained the previous owner's valid lighting mip");
            }
        }
        RasterProbe.Require(invalidated>0,"LRU reuse fixture did not exercise stale light rejection");
        Console.WriteLine($"PASS: compact delayed feedback, 64-page LRU, <=2 captures/frame, reused pages rejected={invalidated}; no previous-owner light mips.");
    }

    /// <summary>导出按当前 geometry/light generation 核验后的图集，原始日志和图片保持在 ignored output。</summary>
    internal static void SaveAtlas(DxrDevice device,RasterCardAtlas cards,string stem)
    {
        var metadata=ReadBuffer<RasterCardData>(device,cards.Metadata,64,ResourceStates.GenericRead);
        var stamps=ReadBuffer<ulong>(device,cards.Stamps,RasterCardAtlas.Pixels,ResourceStates.NonPixelShaderResource);
        var positions=ReconstructionProbe.ReadTexture(device.Device,cards.Positions,false);
        var colors=ReconstructionProbe.ReadTexture(device.Device,cards.MaterialBase,true);
        var lighting=ReadMip(device,cards.Radiance,0);
        var rgba=new byte[192*192*4];var light=new byte[rgba.Length];
        static byte Encode(float v) {v=Math.Clamp(v,0,1);return (byte)Math.Clamp((int)MathF.Round(255*(v<=.0031308f?12.92f*v:1.055f*MathF.Pow(v,1/2.4f)-.055f)),0,255);}
        for(int y=0;y<192;y++)for(int x=0;x<192;x++)
        {
            int pixel=y*192+x,k=pixel*4,slot=y/24*8+x/24;
            bool geometry=metadata[slot].Generation>0&&positions[k+3]!=0;
            bool validLight=geometry&&(uint)stamps[pixel]==metadata[slot].Generation&&lighting[k+3]>0;
            for(int c=0;c<3;c++) {rgba[k+c]=geometry?Encode(colors[k+c]):(byte)0;float value=lighting[k+c];light[k+c]=validLight?Encode(value/(1+value)):(byte)0;}
            rgba[k+3]=light[k+3]=255;
        }
        SceneProbe.WritePng(stem+"-card-base.png",192,192,rgba);SceneProbe.WritePng(stem+"-card-light.png",192,192,light);
        var pages=metadata.Select((c,slot)=>new {slot,c.Instance,c.Mesh,c.Axis,c.Generation,c.Center,c.U,c.V,c.N});
        File.WriteAllText(stem+"-card-pages.json",System.Text.Json.JsonSerializer.Serialize(pages,new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
    }

    private static T[] ReadBuffer<T>(DxrDevice device,ID3D12Resource source,int count,ResourceStates state) where T:unmanaged
    {
        using var result=device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer((ulong)(count*sizeof(T))),ResourceStates.CopyDest);
        device.CommandAllocator.Reset();device.CommandList.Reset(device.CommandAllocator);
        if(state!=ResourceStates.GenericRead)device.CommandList.ResourceBarrierTransition(source,state,ResourceStates.CopySource);
        device.CommandList.CopyResource(result,source);
        if(state!=ResourceStates.GenericRead)device.CommandList.ResourceBarrierTransition(source,ResourceStates.CopySource,state);
        device.CommandList.Close();device.CommandQueue.ExecuteCommandList(device.CommandList);device.SignalAndWait();
        void* pointer;result.Map(0,null,&pointer).CheckError();var data=new ReadOnlySpan<T>(pointer,count).ToArray();result.Unmap(0);return data;
    }
    private static float[] ReadMip(DxrDevice device,ID3D12Resource source,uint mip)
    {
        int width=192>>(int)mip,pitch=(width*8+255)&~255;
        using var result=device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer((ulong)(pitch*width)),ResourceStates.CopyDest);
        device.CommandAllocator.Reset();device.CommandList.Reset(device.CommandAllocator);
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.NonPixelShaderResource,ResourceStates.CopySource,mip);
        device.CommandList.CopyTextureRegion(new TextureCopyLocation(result,new PlacedSubresourceFootPrint {Footprint=new SubresourceFootPrint(Vortice.DXGI.Format.R16G16B16A16_Float,(uint)width,(uint)width,1,(uint)pitch)}),0,0,0,new TextureCopyLocation(source,mip));
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.NonPixelShaderResource,mip);
        device.CommandList.Close();device.CommandQueue.ExecuteCommandList(device.CommandList);device.SignalAndWait();
        void* pointer;result.Map(0,null,&pointer).CheckError();var data=new float[width*width*4];
        for(int y=0;y<width;y++)for(int x=0;x<width*4;x++)data[y*width*4+x]=(float)((Half*)((byte*)pointer+y*pitch))[x];
        result.Unmap(0);return data;
    }
}
