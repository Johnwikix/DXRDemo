using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;

/// <summary>独立验证世界探针的能量、遮挡、反弹、冷启动与相机/尺寸生命周期。</summary>
internal static class RasterWorldProbe
{
    internal static void Run(DxrDevice device, RasterBackend backend)
    {
        RasterProbe.Require(backend.CachedGi,"World cache test requires cached GI");
        const int width=64,height=48;
        var material=SceneMaterial.Default;material.BaseColor=new(.5f,.5f,.5f,1);material.Factors=new(0,1,1,1);
        var black=material;black.BaseColor=new(0,0,0,1);black.Flags.Z=1;
        var camera=CameraFrame.Look(new(0,0,3),-Vector3.UnitZ,.9f,.01f,100);
        var room=RasterHardwareProbe.Room(material,black) with { EnvironmentMap=new(8,4,Enumerable.Repeat(new Vector4(.5f,.5f,.5f,0),32).ToArray()) };
        Vector3 Mean(SceneAsset asset,float environment=1,int frames=32)
        {
            backend.SetScene(asset);Vector3 sum=default;int count=0;
            for(int frame=0;frame<frames;frame++) {
                var output=backend.Render(camera,camera,default,default,width,height,1.0/60,frame==0,environment,default);
                if(frame>=frames-8) {
                    var pixels=ReconstructionProbe.ReadTexture(device.Device,output.Color,true);
                    RasterProbe.Require(pixels.All(float.IsFinite),"World GI generated invalid radiance");
                    for(int y=18;y<30;y++)for(int x=24;x<40;x++) {int k=(y*width+x)*4;sum+=new Vector3(pixels[k],pixels[k+1],pixels[k+2]);count++;}
                }
            }
            return sum/count;
        }
        var open=Mean(room with {Instances=[room.Instances[0]]});
        if(backend.NrdActive)
        {
            RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device,backend.NrdDiffuseInput!,true).All(v=>v==0),"World-cache diffuse masqueraded as NRD ray radiance/hit distance");
            RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device,backend.NrdSpecularInput!,true).All(v=>v==0),"Probe reflection masqueraded as an actual reflection ray");
        }
        uint beforeTiny=backend.WorldUpdates;
        var tiny=backend.Render(camera,camera,default,default,1,1,1.0/60,true,1,default);
        float tinyRed=ReconstructionProbe.ReadTexture(device.Device,tiny.Color,true)[0];
        RasterProbe.Require(backend.GiDivisor==1&&backend.WorldUpdates==beforeTiny+1&&tinyRed is >.20f and <.30f,"Tiny input lost cache lighting or reset world history");
        Console.WriteLine($"World 1x1 receiver red={tinyRed:R}; effective sampling step={backend.GiDivisor}");
        var closed=Mean(room);
        Console.WriteLine($"World open={open}, closed={closed}");
        RasterProbe.Require(open.X is >.20f and <.30f && closed.Length()<1e-5,"World probes lost constant energy or enclosure occlusion");
        var lamp=black;lamp.Emissive=new(1,.1f,.05f,0);
        var lit=Mean(room with {Materials=[material,lamp]},0);
        Console.WriteLine($"World emissive receiver={lit}");
        RasterProbe.Require(lit.X>.15f && lit.X>lit.Y*3,"World probes lost authored mesh light contribution");
        var point=new SceneLight {PositionType=new(0,0,2,1),DirectionRange=new(0,0,-1,20),ColorIntensity=new(1,1,1,8)};
        var dark=Mean(room with {Lights=[point]},0);
        var red=material;red.BaseColor=new(.8f,.02f,.02f,1);red.Flags.Z=1;
        var bounce=Mean(room with {Materials=[material,red],Lights=[point]},0);
        Console.WriteLine($"World wall bounce dark={dark}, red={bounce}, delta={bounce-dark}");
        RasterProbe.Require(bounce.X-dark.X>.01f && bounce.X-dark.X>(bounce.Y-dark.Y)*2,"World probes failed offscreen color transfer");
        // 同一双面墙的内外表面不能共享光照；光源在封闭房间之外时室内应保持黑暗。
        var outsidePoint=point;outsidePoint.PositionType=new(0,0,6,1);outsidePoint.ColorIntensity=new(1,1,1,40);
        var externalAsset=room with {Materials=[material,red],Lights=[outsidePoint]};
        var external=Mean(externalAsset,0);
        Console.WriteLine($"World exterior light inside closed two-sided room={external}");
        RasterProbe.Require(external.Length()<1e-5,"Surface/probe cache leaked lighting across a two-sided wall");
        var settledExternal=Mean(externalAsset,0,96);
        Console.WriteLine($"World exterior light after {backend.WorldUpdates} updates={settledExternal}");
        RasterProbe.Require(backend.WorldUpdates==128&&settledExternal.Length()<1e-5,$"Coarse cache iteration accumulated exterior light through a sealed wall: {settledExternal}");
        // 恢复有光场景，随后检查移动和缩放不会清空世界缓存。
        Mean(room with {Materials=[material,red],Lights=[point]},0);
        var statistics=ReadStatistics(device,backend.WorldStatistics!);
        RasterProbe.Require(statistics[2]<=RasterWorldCache.SurfaceBudget&&statistics[0]>0,"Surface lighting cache did not reuse results or exceeded shading budget");
        Console.WriteLine($"Surface cache: hits={statistics[0]}, shading={statistics[2]}, fallback={statistics[3]}");
        uint updates=backend.WorldUpdates;
        var moved=camera with {Origin=camera.Origin+new Vector3(RasterWorldCache.Grid(backend.SharedScene!.Asset,camera).W*1.25f,0,0)};
        backend.Render(moved,camera,default,default,width,height,1.0/60,true,0,default);
        RasterProbe.Require(backend.WorldUpdates==updates+1,"Camera cut reset world cache");
        backend.Render(moved,moved,default,default,65,49,1.0/60,true,0,default);
        RasterProbe.Require(backend.WorldUpdates==updates+2,"Resize reset world cache");
        var probes=ReadProbes(device,backend.WorldProbeData!);
        int valid=0,invalid=0;
        for(int i=0;i<RasterWorldCache.Count;i++) {if(probes[i*(RasterWorldCache.Stride/16)].W>0)valid++;if(probes[i*(RasterWorldCache.Stride/16)].W<0)invalid++;}
        RasterProbe.Require(valid>0&&probes.All(v=>float.IsFinite(v.X+v.Y+v.Z+v.W)),"World cache has no valid finite probes");
        var reset=Mean(room with {Name="light removed"},0,8);
        RasterProbe.Require(reset.Length()<1e-5&&backend.WorldUpdates==8,"Lighting/scene change retained old world radiance");
        var unlit=material;unlit.BaseColor=new(.2f,.4f,.6f,1);unlit.Emissive=new(.03f,.01f,.02f,0);unlit.Flags.W=1;
        backend.SetScene(RasterProbe.Fixture(unlit));
        var unlitFrame=backend.Render(camera,camera,default,default,64,48,1.0/60,true,10,new SunLightSettings(true,315,45,30,0));
        var unlitColor=ReconstructionProbe.ReadTexture(device.Device,unlitFrame.Color,true);int center=(24*64+32)*4;
        RasterProbe.Require(Math.Abs(unlitColor[center]-.23f)<.001&&Math.Abs(unlitColor[center+1]-.41f)<.001&&Math.Abs(unlitColor[center+2]-.62f)<.001,"World/NRD relit an unlit material");
        Console.WriteLine($"PASS: world GI preserves unlit base+emission {unlitColor[center]:R}/{unlitColor[center+1]:R}/{unlitColor[center+2]:R}.");
        var glass=material;glass.Flags.X=2;glass.BaseColor.W=.5f;glass.Transmission=new(1,1.5f,0,0);
        var withGlass=room with { Meshes=[room.Meshes[0],room.Meshes[1],room.Meshes[0] with {Material=2}],
            Materials=[material,black,glass],Lights=[point],Instances=[..room.Instances,new("glass",2,Matrix4x4.CreateTranslation(0,0,.5f))] };
        backend.SetScene(withGlass);
        var transparent=backend.Render(camera,camera,default,default,64,48,1.0/60,true,1,default);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device,transparent.Color,true).All(float.IsFinite),"Transparent world-cache lookup generated invalid radiance");
        var pane=material;pane.Flags.X=2;pane.BaseColor.W=.5f;
        var emissivePane=room with {Meshes=[room.Meshes[0],room.Meshes[1],room.Meshes[0] with {Material=2}],
            Materials=[material,lamp,pane],Lights=[],Instances=[..room.Instances,new("world-lit pane",2,Matrix4x4.CreateTranslation(0,0,.5f))]};
        Mean(emissivePane,0);
        var paneColor=ReconstructionProbe.ReadTexture(device.Device,backend.SceneColor,true);
        var opaque=ReconstructionProbe.ReadTexture(device.Device,backend.OpaqueSnapshot,true);
        float paneContribution=paneColor[center]-.5f*opaque[center];
        RasterProbe.Require(paneContribution>.02f,"Transparent pane did not receive world-cache lighting with sky/sun/punctual lights disabled");
        Console.WriteLine($"PASS: transparent world-cache red contribution={paneContribution:R}, measured against the same-frame opaque snapshot.");
        var mirror=material;mirror.BaseColor=Vector4.One;mirror.Factors=new(1,.02f,1,1);
        var reflectionAsset=RasterProbe.Fixture(mirror);
        var vertices=reflectionAsset.Meshes[0].Vertices.Select(v=>{v.Normal=new(.38268343f,0,.92387953f,0);return v;}).ToArray();
        var reflectorLamp=black;reflectorLamp.Emissive=new(4,.2f,.1f,0);
        reflectionAsset=reflectionAsset with {Meshes=[reflectionAsset.Meshes[0] with {Vertices=vertices},reflectionAsset.Meshes[0] with {Material=1}],
            Materials=[mirror,reflectorLamp],Instances=[reflectionAsset.Instances[0],new("behind camera",1,Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationY(-MathF.PI/2)*Matrix4x4.CreateTranslation(8,0,8))],Minimum=new(-1,-2,0),Maximum=new(8,2,10)};
        backend.SetScene(reflectionAsset);var reflectionCamera=camera with {VerticalFov=.25f};
        var reflected=backend.Render(reflectionCamera,reflectionCamera,default,default,64,48,1.0/60,true,0,default);
        var reflection=ReconstructionProbe.ReadTexture(device.Device,reflected.Color,true);
        RasterProbe.Require(backend.VisibleInstances==1&&reflection[center]>2&&reflection[center]>reflection[center+1]*10,"Cached GI replaced the actual offscreen metallic reflection");
        if(backend.NrdActive) {
            var packed=ReconstructionProbe.ReadTexture(device.Device,backend.NrdSpecularInput!,true);
            RasterProbe.Require(packed[(6*16+8)*4+3]>1,"Actual reflection lost its physical NRD hit distance");
        }
        Console.WriteLine($"PASS: cached mode offscreen metal reflection={reflection[center]:R}; NRD={backend.NrdActive}, actual-ray signal assertions run only with the SDK.");
        RasterWorldClipmapProbe.Run(device,backend);
        Console.WriteLine($"PASS: world cache valid={valid}, invalid={invalid}; 16640 probe directions/frame independent of input resolution (shadow/reflection rays counted separately); scroll, camera cut, resize, scene/light reset.");
    }

    private static unsafe uint[] ReadStatistics(DxrDevice device,ID3D12Resource source)
    {
        using var readback=device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,ResourceDescription.Buffer(16),ResourceStates.CopyDest);
        device.CommandAllocator.Reset();device.CommandList.Reset(device.CommandAllocator);
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.UnorderedAccess,ResourceStates.CopySource);
        device.CommandList.CopyResource(readback,source);
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.UnorderedAccess);
        device.CommandList.Close();device.CommandQueue.ExecuteCommandList(device.CommandList);device.SignalAndWait();
        void* pointer;readback.Map(0,null,&pointer).CheckError();var result=new ReadOnlySpan<uint>(pointer,4).ToArray();readback.Unmap(0);return result;
    }

    internal static unsafe Vector4[] ReadProbes(DxrDevice device,ID3D12Resource source)
    {
        using var readback=device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback),HeapFlags.None,
            ResourceDescription.Buffer(RasterWorldCache.Count*RasterWorldCache.Stride),ResourceStates.CopyDest);
        device.CommandAllocator.Reset();device.CommandList.Reset(device.CommandAllocator);
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.NonPixelShaderResource,ResourceStates.CopySource);
        device.CommandList.CopyResource(readback,source);
        device.CommandList.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.NonPixelShaderResource);
        device.CommandList.Close();device.CommandQueue.ExecuteCommandList(device.CommandList);device.SignalAndWait();
        void* pointer;readback.Map(0,null,&pointer).CheckError();
        var result=new ReadOnlySpan<Vector4>(pointer,RasterWorldCache.Count*(RasterWorldCache.Stride/16)).ToArray();readback.Unmap(0);return result;
    }
}
