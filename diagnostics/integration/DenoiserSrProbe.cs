using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Scene;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

// Analytic textured plane: varying Halton footprints must reach the real SR SDK intact.
internal static unsafe class DenoiserSrProbe
{
    internal static void Run(string[] args)
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        Guid iid = typeof(ID3D12Device5).GUID; nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        using var native = new ID3D12Device5(pointer);
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var sr = new SuperResolutionRenderer(device);
        if(args.Contains("--sr-mips")) { VerifyMipFootprint(device,sr); VerifyDebug(messages); return; }
        if(args.Contains("--sr-bistro")) { VerifyBistro(device,sr,args); VerifyDebug(messages); return; }
        const int width = 512, height = 288, frames = 48;
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width,height);
        var results = new List<Result>();
        bool noisy=args.Contains("--noisy"),emissive=args.Contains("--emissive"),sparse=args.Contains("--sparse"),impulse=args.Contains("--impulse");
        Require(!sparse || !emissive,"Sparse transport must enter the noisy lighting history");
        string output = "output/sr-detail"+(noisy?"-noisy":"")+(emissive?"-emissive":"")+(sparse?"-sparse":"")+(impulse?"-impulse":"");
        Directory.CreateDirectory(output);
        foreach (var mode in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS, ReconstructionMode.Dlss })
        {
            if (!sr.Status.Supports(mode) || args.Contains("--fsr-only") && mode != ReconstructionMode.Fsr) continue;
            foreach (var denoiser in new[] { RayTraceDenoiserMode.None, RayTraceDenoiserMode.TemporalOnly, RayTraceDenoiserMode.Optix })
            {
                if (denoiser == RayTraceDenoiserMode.Optix && !sr.Status.OptixAvailable) continue;
                sr.Configure(mode,50,width,height,false,true,denoiser == RayTraceDenoiserMode.Optix); sr.Reset();
                Require(sr.Active == mode,sr.Status.Message);
                int iw=sr.InputWidth,ih=sr.InputHeight;
                using var raw=device.AllocateReadWriteTexture2D<Float4>(iw,ih);
                using var normal=device.AllocateReadWriteTexture2D<Rgba32,Float4>(iw,ih);
                using var surfaces=device.AllocateReadWriteTexture2D<Float4>(iw,ih);
                using var guide=device.AllocateReadWriteTexture2D<Float4>(iw,ih);
                using var signals=new PbrSignals(device,iw,ih);
                var color=new Float4[ih,iw];var reflectance=new Float4[ih,iw];var geometry=new Float4[ih,iw];var normals=new Float4[ih,iw];
                var random=new Random(58431);
                var camera=CameraFrame.Look(new(0,0,3),-Vector3.UnitZ,2*MathF.Atan(.5f),.01f,1000);
                double inputGain=0;
                for(int frame=0;frame<frames;frame++)
                {
                    Float2 jitter=sr.Jitter();
                    for(int y=0;y<ih;y++)for(int x=0;x<iw;x++)
                    {
                        float u=(x+.5f+jitter.X)/iw,v=(y+.5f+jitter.Y)/ih;
                        float a=Pattern(u,v,width);
                        float sample=a*(sparse?(frame%16==0?16:0):noisy?(float)-Math.Log(1-random.NextDouble()):1);
                        if(impulse)sample=frame==0 && x==iw/2 && y==ih/2?a*256:0;
                        color[y,x]=new(sample,sample,sample,0);
                        reflectance[y,x]=emissive?new(.35f,.35f,.35f,0):new(a,a,a,0);
                        float wx=(u*2-1)*iw/(float)ih*1.5f,wy=(1-v*2)*1.5f;
                        geometry[y,x]=new(wx,wy,0,MathF.Sqrt(wx*wx+wy*wy+9));
                        normals[y,x]=new(0,0,1,.8f);
                    }
                    raw.CopyFrom(color);signals.Albedo.CopyFrom(reflectance);surfaces.CopyFrom(geometry);guide.CopyFrom(normals);
                    if(emissive)signals.Unfiltered.CopyFrom(color);
                    Require(sr.Execute(raw,normal,surfaces,target,default,3,jitter,denoiser,HdrRenderParameters.Default,1.0/60,guide,camera,signals),sr.Status.Message);
                    if(frame==frames-1)
                    {
                        float[] input=ReconstructionProbe.ReadTexture(native,sr.DenoisedColor,true);
                        inputGain=Gain(input,iw,ih,(x,y)=>Pattern((x+.5f+jitter.X)/iw,(y+.5f+jitter.Y)/ih,width));
                    }
                }
                Require(denoiser!=RayTraceDenoiserMode.Optix || sr.Status.OptixActive && sr.OptixDispatchCount>=frames,"OptiX silently fell back");
                if(sparse && denoiser!=RayTraceDenoiserMode.None)
                {
                    var history=ReconstructionProbe.ReadTexture(native,sr.DenoiserHistory,false);
                    uint[] counts=TemporalConvergenceProbe.ReadSampleCounts(native,sr.TemporalHistorySampleCounts);
                    Require(counts.Where((_,i)=>i%2==0).All(n=>n==frames),"Stationary SR clipped or capped the lighting mean");
                    Require(history.Where((_,i)=>i%4!=3).All(v=>Math.Abs(v-1)<1e-5),"Stationary SR lost rare bright samples or fed AI output into lighting history");
                }
                if(impulse && denoiser!=RayTraceDenoiserMode.None)
                {
                    var history=ReconstructionProbe.ReadTexture(native,sr.DenoiserHistory,false);
                    double leak=0,error=0;
                    for(int y=0;y<ih;y++)for(int x=0;x<iw;x++)for(int c=0;c<3;c++)
                    {
                        float value=history[(y*iw+x)*4+c];
                        if(x==iw/2 && y==ih/2)error=Math.Max(error,Math.Abs(value-256f/frames));
                        else leak=Math.Max(leak,Math.Abs(value));
                    }
                    Console.WriteLine($"SR lighting impulse {mode}+{denoiser}: neighbor leakage={leak:E3}, center error={error:E3}");
                    Require(leak<1e-7 && error<1e-5,"SR repeatedly resampled a rare bright path into spreading light spots");
                }
                float[] image=ReconstructionProbe.ReadTexture(native,sr.LinearOutput,true,ResourceStates.UnorderedAccess);
                double gain=Gain(image,width,height,(x,y)=>Pattern((x+.5f)/width,(y+.5f)/height,width));
                double mse=0;int pixels=0;
                for(int y=height/3;y<height*2/3;y++)for(int x=24;x<width-24;x++)
                { double delta=image[(y*width+x)*4]-Pattern((x+.5f)/width,(y+.5f)/height,width);mse+=delta*delta;pixels++; }
                var result=new Result(mode.ToString(),denoiser.ToString(),inputGain,gain,Math.Sqrt(mse/pixels));
                Console.WriteLine(JsonSerializer.Serialize(result));
                SceneProbe.Save(target,$"{output}/{mode}-{denoiser}.png");
                if(!sparse && !impulse)
                {
                    float worldWidth=3*iw/(float)ih;
                    CameraFrame moving=camera;
                    for(int step=0;step<8;step++)
                    {
                        moving=camera with { Origin=camera.Origin+new Vector3((step+1)*.5f*worldWidth/width,0,0) };
                        Float2 jitter=sr.Jitter();
                        for(int y=0;y<ih;y++)for(int x=0;x<iw;x++)
                        {
                            float u=(x+.5f+jitter.X)/iw,v=(y+.5f+jitter.Y)/ih;
                            float a=Pattern(u+moving.Origin.X/worldWidth,v,width);
                            float sample=a*(noisy?(float)-Math.Log(1-random.NextDouble()):1);
                            color[y,x]=new(sample,sample,sample,0);reflectance[y,x]=emissive?new(.35f,.35f,.35f,0):new(a,a,a,0);
                            float wx=(u*2-1)*iw/(float)ih*1.5f,wy=(1-v*2)*1.5f;
                            geometry[y,x]=new(wx+moving.Origin.X,wy,0,MathF.Sqrt(wx*wx+wy*wy+9));
                        }
                        raw.CopyFrom(color);signals.Albedo.CopyFrom(reflectance);surfaces.CopyFrom(geometry);
                        if(emissive)signals.Unfiltered.CopyFrom(color);
                        Require(sr.Execute(raw,normal,surfaces,target,default,3,jitter,denoiser,HdrRenderParameters.Default,1.0/60,guide,moving,signals),sr.Status.Message);
                    }
                    float[] motion=ReconstructionProbe.ReadTexture(native,sr.Motion,true);
                    Require(motion.Where((_,i)=>i%2==0).All(v=>v is >.24f and <.26f),"SR motion was lost or contains jitter instead of camera motion");
                    var movingOutput=ReconstructionProbe.ReadTexture(native,sr.LinearOutput,true,ResourceStates.UnorderedAccess);
                    double movingDetail=Gain(movingOutput,width,height,(x,y)=>Pattern((x+.5f)/width+moving.Origin.X/worldWidth,(y+.5f)/height,width));
                    Require(movingDetail>result.OutputDetail*.7,$"{mode}+{denoiser} lost detail during camera motion: {movingDetail}");
                    result=result with { MovingDetail=movingDetail };
                    Console.WriteLine($"PASS camera motion {mode}+{denoiser}: 0.25 input pixels/frame, moving detail={movingDetail:F5}");
                    SceneProbe.Save(target,$"{output}/{mode}-{denoiser}-moving.png");
                }
                results.Add(result);
            }
        }
        File.WriteAllText($"{output}/metrics.json",JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        VerifyDebug(messages);
        foreach(var group in results.GroupBy(r=>r.Provider).Where(_=>!impulse))
        {
            var baseline=group.Single(r=>r.Denoiser==nameof(RayTraceDenoiserMode.None));
            foreach(var item in group.Where(r=>r.Denoiser!=nameof(RayTraceDenoiserMode.None)))
                Require(item.InputDetail>.8 && item.OutputDetail>baseline.OutputDetail*.8,
                    $"{item.Provider}+{item.Denoiser} erased jittered texture detail: input={item.InputDetail:F3}, output={item.OutputDetail:F3}, SDK baseline={baseline.OutputDetail:F3}");
        }
        Require(results.Count>=3,"No actual SR/denoiser combinations were exercised");
        Console.WriteLine("PASS jittered texture detail through actual temporal SR/denoiser combinations.");
    }
    private static void VerifyMipFootprint(GraphicsDevice device,SuperResolutionRenderer sr)
    {
        const int width=512,height=288,edge=1024;
        var levels=new List<byte[]>();var mip=new byte[edge*edge*4];
        for(int y=0;y<edge;y++)for(int x=0;x<edge;x++)
        {
            byte a=(byte)(100+60*Math.Sin(2*Math.PI*x/8));int p=(y*edge+x)*4;
            mip[p]=mip[p+1]=mip[p+2]=a;mip[p+3]=255;
        }
        levels.Add(mip);
        for(int size=edge;size>1;size/=2)
        {
            int half=size/2;var next=new byte[half*half*4];
            for(int y=0;y<half;y++)for(int x=0;x<half;x++)for(int c=0;c<4;c++)
                next[(y*half+x)*4+c]=(byte)((mip[((y*2)*size+x*2)*4+c]+mip[((y*2)*size+x*2+1)*4+c]+
                    mip[((y*2+1)*size+x*2)*4+c]+mip[((y*2+1)*size+x*2+1)*4+c])/4);
            levels.Add(next);mip=next;
        }
        var material=SceneMaterial.Default;material.Flags.W=1;material.Factors.X=0;
        material.BaseColorMap=new() { Info=new(0,0,10497,10497),Transform=new(0,0,1,1),Extra=new(0,0,levels.Count-1,edge) };
        float halfWidth=width/(float)height*1.5f;
        var vertices=new SceneVertex[4];
        Vector3[] positions=[new(-halfWidth,-1.5f,0),new(halfWidth,-1.5f,0),new(halfWidth,1.5f,0),new(-halfWidth,1.5f,0)];
        Vector2[] uv=[new(0,0),new(1,0),new(1,1),new(0,1)];
        for(int i=0;i<4;i++)vertices[i]=new() { Position=new(positions[i],1),Normal=new(0,0,1,0),Tangent=new(1,0,0,1),TexCoord=new(uv[i],0,0),Color=Vector4.One };
        var mesh=new SceneMesh("textured plane",vertices,[0,1,2,0,2,3],0);
        var scene=new SceneAsset("SR mip footprint",[mesh],[new("plane",0,Matrix4x4.Identity)],[material],
            [new("fine texture",edge,edge,levels.ToArray(),false)],[],new(-halfWidth,-1.5f,0),new(halfWidth,1.5f,0),[]);
        using var backend=new DxrSceneBackend { CausticsEnabled=false,FireflyThreshold=0 };backend.Initialize(device);backend.SetScene(scene);
        var camera=CameraFrame.Look(new(0,0,3),-Vector3.UnitZ,2*MathF.Atan(.5f),.01f,1000);
        Float4[,] Render(int w,int h,Float2 jitter,float bias)
        {
            using var raw=device.AllocateReadWriteTexture2D<Float4>(w,h);
            using var normal=device.AllocateReadWriteTexture2D<Rgba32,Float4>(w,h);
            using var surface=device.AllocateReadWriteTexture2D<Float4>(w,h);
            using var guide=device.AllocateReadWriteTexture2D<Float4>(w,h);
            using var signals=new PbrSignals(device,w,h);
            backend.Trace(camera,0,1,1,jitter,0,raw,normal,surface,guide,signals,textureLodBias:bias);
            return raw.ToArray();
        }
        var reference=Render(width,height,new(.5f,.5f),0);
        var mode=new[] { ReconstructionMode.Fsr,ReconstructionMode.XeSS,ReconstructionMode.Dlss }.First(sr.Status.Supports);
        sr.Configure(mode,50,width,height,false,true);
        Require(sr.Active==mode && Math.Abs(sr.TextureLodBias+1)<1e-6,sr.Status.Message);
        var old=Render(width/2,height/2,default,0);
        var corrected=Render(width/2,height/2,default,sr.TextureLodBias);
        double before=0,after=0;int count=0;
        for(int y=4;y<height/2-4;y++)for(int x=4;x<width/2-4;x++)
        { double truth=reference[y*2,x*2].X;before+=Math.Pow(old[y,x].X-truth,2);after+=Math.Pow(corrected[y,x].X-truth,2);count++; }
        before=Math.Sqrt(before/count);after=Math.Sqrt(after/count);
        Console.WriteLine($"Production mip footprint, 50% input vs native matching rays: before RMSE={before:F6}, corrected={after:E3}, bias={sr.TextureLodBias:F3}");
        Require(before>.01 && after<1e-4,"SR texture LOD did not preserve the output-resolution footprint");
        Console.WriteLine("PASS production SR mip footprint matches the native-resolution texture reference.");
    }
    private static void VerifyBistro(GraphicsDevice device,SuperResolutionRenderer capabilities,string[] args)
    {
        var mode=new[] { ReconstructionMode.Dlss,ReconstructionMode.XeSS,ReconstructionMode.Fsr }.First(capabilities.Status.Supports);
        var denoiser=args.Contains("--temporal")?RayTraceDenoiserMode.TemporalOnly:RayTraceDenoiserMode.Optix;
        using var pass=new RayTracePass(new DxrMeshBackend());pass.Initialize(device,default);
        pass.SceneIndex=RayTracePass.RtxptBistroSceneIndex;
        pass.ReconstructionMode=mode;pass.RenderScalePercent=67;pass.DenoiserMode=denoiser;pass.Samples=1;pass.MaxBounces=8;
        using var target=device.AllocateReadWriteTexture2D<Rgba64,Float4>(1280,720);
        var watch=System.Diagnostics.Stopwatch.StartNew();int rendered=0;
        while(rendered<64)
        {
            Require(watch.Elapsed.TotalSeconds<240,"Bistro SR smoke test timed out");
            bool ready=pass.TryExecute(target,1280,720,TimeSpan.Zero,HdrRenderParameters.Default);
            if(ready && pass.ReconstructionStatus?.Active==mode)
            {
                rendered++;if(rendered%16==0)Console.WriteLine($"Bistro {denoiser}+{mode}, frame {rendered}/64");
            }
            else Thread.Sleep(10);
        }
        Require(denoiser!=RayTraceDenoiserMode.Optix || pass.ReconstructionStatus?.OptixActive==true && pass.OptixDispatchCount>=32,"Actual Bistro OptiX execution was unavailable");
        Directory.CreateDirectory("output/sr-bistro");SceneProbe.Save(target,$"output/sr-bistro/{mode}-{denoiser}.png");
        Console.WriteLine($"PASS actual Bistro: {denoiser}+{mode}, {pass.ReconstructionStatus!.InputWidth}x{pass.ReconstructionStatus.InputHeight} -> 1280x720, 1 SPP, 8 bounces, 64 frames.");
    }
    private static void VerifyDebug(ID3D12InfoQueue messages)
    {
        for(ulong i=0;i<messages.NumStoredMessagesAllowedByRetrievalFilter;i++)
        { var message=messages.GetMessage(i);Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption),message.Description); }
    }
    private static float Pattern(float u,float v,int width)
    {
        // Central band is a 4-input-pixel period at 50% rendering; broad outer bands aid visual QA.
        float period=v is > 1f/3 and < 2f/3?8:24;
        return .35f+.22f*MathF.Sin(2*MathF.PI*(u*width/period+v*.7f));
    }
    private static double Gain(float[] values,int width,int height,Func<int,int,float> reference)
    {
        double signal=0,response=0;
        for(int y=height/3+3;y<height*2/3-3;y++)for(int x=16;x<width-16;x++)
        { double r=reference(x,y)-.35;signal+=r*r;response+=r*(values[(y*width+x)*4]-.35); }
        return response/signal;
    }
    private sealed record Result(string Provider,string Denoiser,double InputDetail,double OutputDetail,double Rmse,double? MovingDetail=null);
    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
