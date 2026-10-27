using System.Numerics;
using System.Text.Json;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Scene;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Float2 = System.Numerics.Vector2;
using Float4 = System.Numerics.Vector4;

// Actual glass BSDF + actual SR: stationary shimmer, fine background detail and optical motion.
internal static unsafe class GlassTemporalProbe
{
    internal static void Run(string[] args)
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native=device.Device.QueryInterface<ID3D12Device5>();
        using var messages=native.QueryInterface<ID3D12InfoQueue>();
        using var backend=new DxrSceneBackend { CausticsEnabled=false,FireflyThreshold=0 };
        backend.Initialize(device);
        const int width=384,height=240,frames=48,measured=16;
        var view=CameraFrame.Look(new(0,0,3),-Vector3.UnitZ,.7f,.01f,100);
        var original=GlassReconstructionProbe.Fixture();
        bool volume=args.Contains("--volume"),partial=args.Contains("--partial"),reflective=args.Contains("--reflective"),rough=args.Contains("--rough"),layered=args.Contains("--layered"),hdrDetail=args.Contains("--hdr-detail");
        float radianceGain=hdrDetail?101:1;
        float transmission=partial?.6f:1,ior=reflective?9:1.5f;
        string variant=volume?"-volume":partial?"-partial":reflective?"-reflective":rough?"-rough":layered?"-layered":"";
        if(hdrDetail)variant+="-hdr";
        int scaleIndex=Array.IndexOf(args,"--scale");int scale=scaleIndex>=0?int.Parse(args[scaleIndex+1]):50;
        Require(scale is >=33 and <=100,"Glass quality scale must be 33..100");
        if(scale!=50)variant+=$"-{scale}";
        string output="output/glass-temporal"+variant;
        var materials=(SceneMaterial[])original.Materials.Clone();
        if(hdrDetail)
        {
            for(int i=0;i<2;i++)materials[i].Emissive=new(materials[i].BaseColor.X*100,materials[i].BaseColor.Y*100,materials[i].BaseColor.Z*100,0);
            backend.FireflyThreshold=64;
        }
        materials[2].Factors.Y=rough?.02f:0;materials[2].Transmission=new(transmission,ior,volume?.01f:0,0);materials[2].Flags.Z=1;
        var scene=original with { Materials=materials,Instances=volume || layered?original.Instances:original.Instances[..^1] };
        if(volume)
        {
            // Entry and exit must share an instance/medium identity. Two separate
            // sheet instances would test an invalid nested-medium fixture instead.
            var corners=new[] { new Vector3(-1.4f,-.9f,1),new Vector3(1.4f,-.9f,1),new Vector3(1.4f,.9f,1),new Vector3(-1.4f,.9f,1),
                new Vector3(-1.4f,-.9f,.99f),new Vector3(1.4f,-.9f,.99f),new Vector3(1.4f,.9f,.99f),new Vector3(-1.4f,.9f,.99f) };
            int[][] faces=[[0,1,2,3],[4,7,6,5],[0,3,7,4],[1,5,6,2],[0,4,5,1],[3,2,6,7]];
            Vector3[] normals=[Vector3.UnitZ,-Vector3.UnitZ,-Vector3.UnitX,Vector3.UnitX,-Vector3.UnitY,Vector3.UnitY];
            var vertices=new SceneVertex[24];var indices=new uint[36];
            for(int face=0;face<6;face++)
            {
                for(int i=0;i<4;i++)vertices[face*4+i]=new() { Position=new(corners[faces[face][i]],1),Normal=new(normals[face],0),Color=Vector4.One };
                uint offset=(uint)face*4;uint[] local=[0,1,2,0,2,3];for(int i=0;i<6;i++)indices[face*6+i]=offset+local[i];
            }
            var meshes=(SceneMesh[])scene.Meshes.Clone();meshes[48]=new("closed glass slab",vertices,indices,2);
            scene=scene with { Meshes=meshes,Instances=scene.Instances.Where(i=>i.Mesh!=49).ToArray() };
        }
        if(partial || reflective)
        {
            var card=SceneMaterial.Default;card.Factors.X=0;card.BaseColor=new(.15f,.15f,.15f,1);card.Flags.W=card.Flags.Z=1;
            var points=new[] { new Vector3(-10,-10,5),new Vector3(10,-10,5),new Vector3(10,10,5),new Vector3(-10,10,5) };
            var vertices=points.Select(p=>new SceneVertex { Position=new(p,1),Normal=new(0,0,-1,0),Color=Vector4.One }).ToArray();
            scene=scene with { Materials=[..materials,card],Meshes=[..scene.Meshes,new("reflection card",vertices,[0,2,1,0,3,2],3)],
                Instances=[..scene.Instances,new("reflection card",scene.Meshes.Length,Matrix4x4.Identity)] };
        }
        // An exact, noise-free reference isolates glass filtering from Monte Carlo error.
        using var referenceRaw=device.CreateTexture2D<Float4>(width,height);
        using var referenceNormal=device.CreateTexture2D<Rgba32>(width,height);
        using var referenceSurface=device.CreateTexture2D<Float4>(width,height);
        using var referenceGuide=device.CreateTexture2D<Float4>(width,height);
        using var referenceSignals=new PbrSignals(device,width,height);
        backend.SetScene(scene with { Instances=scene.Instances.Where(i=>i.Mesh is not (48 or 49)).ToArray() });
        backend.Trace(view,0,1,4,default,0,referenceRaw,referenceNormal,referenceSurface,referenceGuide,referenceSignals);
        var reference=referenceRaw.ToArray();backend.SetScene(scene);
        using var sr=new SuperResolutionRenderer(device);
        using var target=device.CreateTexture2D<Rgba64>(width,height);
        var results=new List<Result>();Directory.CreateDirectory(output);
        foreach(var mode in new[] { ReconstructionMode.Fsr,ReconstructionMode.XeSS,ReconstructionMode.Dlss })
        {
            if(!sr.Status.Supports(mode) || args.Contains("--fsr-only") && mode!=ReconstructionMode.Fsr)continue;
            foreach(var denoiser in new[] { RayTraceDenoiserMode.TemporalOnly,RayTraceDenoiserMode.Optix })
            {
                if(denoiser==RayTraceDenoiserMode.Optix && !sr.Status.OptixAvailable)continue;
                sr.Configure(mode,scale,width,height,false,true,denoiser==RayTraceDenoiserMode.Optix);sr.Reset();
                Require(sr.Active==mode,sr.Status.Message);
                int w=sr.InputWidth,h=sr.InputHeight;
                using var raw=device.CreateTexture2D<Float4>(w,h);
                using var normal=device.CreateTexture2D<Rgba32>(w,h);
                using var surface=device.CreateTexture2D<Float4>(w,h);
                using var guide=device.CreateTexture2D<Float4>(w,h);
                using var signals=new PbrSignals(device,w,h);
                void Frame(int frame,CameraFrame camera)
                {
                    Float2 jitter=sr.Jitter();
                    backend.Trace(camera,frame,1,6,jitter,0,raw,normal,surface,guide,signals,
                        optixGuides:denoiser==RayTraceDenoiserMode.Optix,textureLodBias:sr.TextureLodBias,stabilizeGlass:true);
                    Require(sr.Execute(raw,normal,surface,target,default,3,jitter,denoiser,
                        HdrRenderParameters.Default,1.0/60,guide,camera,signals),sr.Status.Message);
                }
                var sum=new double[width*height];var square=new double[width*height];
                for(int frame=0;frame<frames;frame++)
                {
                    Frame(frame,view);
                    if(frame<frames-measured)continue;
                    float[] pixels=ReconstructionProbe.ReadTexture(native,sr.LinearOutput,true,ResourceStates.UnorderedAccess);
                    for(int p=0;p<sum.Length;p++) { double a=pixels[p*4];Require(double.IsFinite(a),"Nonfinite glass output");sum[p]+=a;square[p]+=a*a; }
                }
                Require(denoiser!=RayTraceDenoiserMode.Optix || sr.Status.OptixActive && sr.OptixDispatchCount>=frames,"OptiX silently fell back");
                double bright=0,dark=0,variance=0;int brightN=0,darkN=0;
                for(int y=height/3;y<height*2/3;y++)for(int x=width/4;x<width*3/4;x++)
                {
                    float truth=reference[y,x].X;
                    if(Math.Abs(reference[y,x-1].X-truth)>.001 || Math.Abs(reference[y,x+1].X-truth)>.001)continue;
                    int p=y*width+x;double mean=sum[p]/measured;
                    variance+=Math.Max(0,square[p]/measured-mean*mean);
                    if(truth/radianceGain>.8f) { bright+=mean;brightN++; } else { dark+=mean;darkN++; }
                }
                Require(brightN>100 && darkN>100,"The glass detail fixture is empty");
                double f=Math.Pow((ior-1)/(ior+1),2);
                double expectedTrans=(volume || layered?(1-f)/(1+f):1-f)*transmission;
                double contrast=(bright/brightN-dark/darkN)/(.35*expectedTrans*radianceGain);
                double rms=Math.Sqrt(variance/(brightN+darkN))/radianceGain;
                SceneProbe.Save(target,$"{output}/{mode}-{denoiser}.png");
                var optical=surface.ToArray();float guideZ=optical[h/2,w/2].Z;
                Frame(frames,view with { Origin=view.Origin+new Vector3(.01f,0,0) });
                float[] motion=ReconstructionProbe.ReadTexture(native,sr.Motion,true);
                // A thin pane has no lateral refraction: the visible plane is at Z=0, not Z=1.
                double expectedZ=reflective?-3:volume?.01*(1-1/ior):0;
                double expectedMotion=.01*h/(2*(3-expectedZ)*Math.Tan(view.VerticalFov*.5));
                double motionError=Math.Abs(motion[(h/2*w+w/2)*2]-expectedMotion);
                double Reference(int x,int y,float offset)
                {
                    var ray=Vector3.Normalize(new Vector3((2*(x+.5f)/width-1)*(width/(float)height)*MathF.Tan(view.VerticalFov*.5f),
                        (1-2*(y+.5f)/height)*MathF.Tan(view.VerticalFov*.5f),-1));
                    double cosine=-ray.Z,eta=1/ior,ct=Math.Sqrt(1-eta*eta*(1-cosine*cosine));
                    double rs=(eta*cosine-ct)/(eta*cosine+ct),rp=(cosine-eta*ct)/(cosine+eta*ct);
                    double fresnel=(rs*rs+rp*rp)*.5;
                    double bx=offset+ray.X*3/(-ray.Z);
                    if(volume)bx=offset+ray.X*2.99/(-ray.Z)+eta*ray.X*.01/ct;
                    int stripe=(int)Math.Floor((bx+1.5)*16);double pattern=(stripe&1)==0?1:.65;
                    return (volume || layered?(1-fresnel)/(1+fresnel):1-fresnel)*transmission*pattern*radianceGain+
                        (partial || reflective?fresnel*.15:0);
                }
                double movingError=0;
                double expectedEnergy=0;int energyPixels=0;
                for(int y=height/3;y<height*2/3;y++)for(int x=width/4;x<width*3/4;x++)
                {
                    float truth=reference[y,x].X;
                    if(Math.Abs(reference[y,x-1].X-truth)>.001 || Math.Abs(reference[y,x+1].X-truth)>.001)continue;
                    expectedEnergy+=Reference(x,y,0);energyPixels++;
                }
                double energyRatio=(bright+dark)/expectedEnergy;
                Require(energyPixels==brightN+darkN,"Energy/reference masks differ");
                for(int step=0;step<8;step++)
                {
                    float offset=(step+1)*.01f;
                    if(step>0)Frame(frames+step,view with { Origin=view.Origin+new Vector3(offset,0,0) });
                    float[] pixels=ReconstructionProbe.ReadTexture(native,sr.LinearOutput,true,ResourceStates.UnorderedAccess);
                    double error=0,error2=0;int count=0;
                    for(int y=height/3;y<height*2/3;y++)for(int x=width/4;x<width*3/4;x++)
                    {
                        double truth=Reference(x,y,offset);
                        if(Math.Abs(Reference(x-2,y,offset)-truth)>.001 || Math.Abs(Reference(x+2,y,offset)-truth)>.001)continue;
                        double delta=pixels[(y*width+x)*4]-truth;error+=delta;error2+=delta*delta;count++;
                    }
                    Require(count>100,"Moving optical reference has no stripe interiors");
                    movingError=Math.Max(movingError,Math.Sqrt(Math.Max(0,error2/count-Math.Pow(error/count,2)))/radianceGain);
                }
                SceneProbe.Save(target,$"{output}/{mode}-{denoiser}-moving.png");
                var result=new Result(mode.ToString(),denoiser.ToString(),contrast,rms,guideZ,motionError,Math.Abs(guideZ-expectedZ),movingError,energyRatio);
                results.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
            }
        }
        File.WriteAllText($"{output}/metrics.json",JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        for(ulong i=0;i<messages.NumStoredMessagesAllowedByRetrievalFilter;i++)
        { var message=messages.GetMessage(i);Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption),message.Description); }
        Require(results.Count>=2,"No glass/SR combinations executed");
        foreach(var r in results)
        {
            Require(r.Contrast is >.8 and <1.15,$"{r.Provider}+{r.Denoiser} blurred glass detail: {r.Contrast}");
            Require(r.FrameRms<.025,$"{r.Provider}+{r.Denoiser} shimmers through stationary glass: {r.FrameRms}");
            Require(r.GuideError<.001 && r.MotionError<.02,$"{r.Provider}+{r.Denoiser} tracks the pane instead of the visible background");
            Require(r.MovingError<.06,$"{r.Provider}+{r.Denoiser} loses detail while moving behind glass: {r.MovingError}");
            Require(r.EnergyRatio>.9 && r.EnergyRatio<(partial?1.25:1.1),$"{r.Provider}+{r.Denoiser} changed glass energy: {r.EnergyRatio}");
        }
        Console.WriteLine("PASS glass/SR detail, stationary stability and visible-surface motion.");
    }
    private sealed record Result(string Provider,string Denoiser,double Contrast,double FrameRms,double GuideZ,double MotionError,double GuideError,double MovingError,double EnergyRatio);
    private static void Require(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
}
