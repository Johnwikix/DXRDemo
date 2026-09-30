using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using DXRDemo.Assets.Importers;
using DXRDemo.SuperResolution;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.Hdr;

internal static class LightingProbe
{
    // Actual-scene A/B capture: fixed camera, seed, exposure and frame sequence; no denoiser or SR.
    internal static async Task SceneAblation(string mode)
    {
        var document = await GltfImporter.LoadAsync(Path.GetFullPath("../../Samples/RainyCorner/RainyCorner.glb"));
        var asset = document.Scenes[document.DefaultScene];
        using var device = GraphicsDevice.GetDefault();
        using var backend = new DxrSceneBackend(); backend.Initialize(device); backend.SetScene(asset);
        backend.RestirEnabled = !mode.Contains("no-restir") && mode != "neither";
        backend.CausticsEnabled = !mode.Contains("no-caustics") && mode != "neither";
        const int width=640,height=400;
        using var raw=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var normal=device.AllocateReadWriteTexture2D<Rgba32,Float4>(width,height);
        using var surface=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var guide=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var signals=new PbrSignals(device,width,height);
        var camera=new CameraController(); camera.FrameBounds(asset.Minimum,asset.Maximum,width/(float)height);
        camera.Rotate(0,.18f); camera.Zoom(3); var view=camera.Advance(0,out _,out _);
        bool nrd=mode.StartsWith("nrd"),rr=mode.StartsWith("dlssd");
        using var reconstruction=nrd||rr?new SuperResolutionRenderer(device):null;
        using var target=nrd||rr?device.AllocateReadWriteTexture2D<Rgba64,Float4>(width,height):null;
        var reconstructionMode=rr?ReconstructionMode.DlssRayReconstruction:ReconstructionMode.Off;
        reconstruction?.Configure(reconstructionMode,100,width,height,nrd,true);
        if(rr) Require(reconstruction!.Active==reconstructionMode,reconstruction.Status.Message);
        int frames=nrd||rr?48:8;
        for(int frame=0;frame<frames;frame++)
        {
            Float2 jitter=reconstruction?.Jitter()??default;
            backend.Trace(view,frame,2,10,jitter,0,raw,normal,surface,guide,signals,rayReconstruction:rr,opaqueForNrd:nrd);
            if(reconstruction!=null)
                Require(reconstruction.Execute(raw,normal,surface,target!,default,2.7f,jitter,nrd?RayTraceDenoiserMode.NrdRelax:RayTraceDenoiserMode.None,
                    HdrRenderParameters.Default,1.0/60,guide,view,signals,-2,transparentBackend:nrd?backend:null),reconstruction.Status.Message);
        }
        if(nrd) Require(reconstruction!.Status.NrdActive,"NRD capture used a fallback");
        if(rr) Require(reconstruction!.Active==reconstructionMode,"DLSSD capture used a fallback");
        Directory.CreateDirectory("output/lighting/scene");
        var pixels=raw.ToArray(); string prefix=$"output/lighting/scene/{mode}";
        if(target!=null) SceneProbe.Save(target,prefix+".png");
        using(var output=File.Create(prefix+".rgba32f"))
            output.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref pixels[0,0],pixels.Length)));
        using(var writer=new BinaryWriter(File.Create(prefix+".ppm")))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
            byte Encode(float value) { float c=Math.Max(value,0)*.25f; c=Math.Clamp(c*(2.51f*c+.03f)/(c*(2.43f*c+.59f)+.14f),0,1); return (byte)(255*MathF.Pow(c,1/2.2f)); }
            foreach(var p in pixels) { writer.Write(Encode(p.X));writer.Write(Encode(p.Y));writer.Write(Encode(p.Z)); }
        }
        Console.WriteLine($"Captured {mode}: ReSTIR={backend.RestirEnabled}, caustics={backend.CausticsEnabled}, photon builds={backend.PhotonBuildCount}, 640x400, 2 SPP, 10 bounces, frame={frames-1}, exposure=-2, NRD={nrd}, DLSSD={rr}.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static unsafe void Run()
    {
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); layer.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        Guid iid = typeof(ID3D12Device5).GUID; nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        using var native = new ID3D12Device5(pointer);
        using var debug = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend(); backend.Initialize(device);
        const int width = 192, height = 144;
        using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var surface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var guide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        var view = CameraFrame.Look(new(0, 4, 3.5f), new(0, -4, -3.5f), .8f, .001f, 100);
        SunLightSettings lightOverride=default;
        void Trace(int frame, bool reset = false) => backend.Trace(view, frame, 1, 1, default, 0, raw, normal, surface, guide,
            signals,sun:lightOverride,resetHistory: reset);
        // Flat, parallel storefront glazing must not turn continuous transmitted light into photon splats.
        // Compare raw wet-ground radiance, before any denoiser, on the actual production backend.
        var paneScene=MakeScene(false,1);
        var paneMaterials=(SceneMaterial[])paneScene.Materials.Clone(); paneMaterials[0].Factors.Y=.12f;
        var paneMesh=Box(new(-1.3f,1.1f,-1.3f),new(1.3f,1.12f,1.3f),1);
        var tessellatedPane=Box(new(-1.3f,1.1f,-1.3f),new(1.3f,1.12f,1.3f),1,true);
        foreach(var (mesh,transform) in new[] { (paneMesh,Matrix4x4.Identity),(paneMesh,Matrix4x4.CreateScale(-1.15f,1,.85f)*Matrix4x4.CreateRotationY(.37f)),
            (tessellatedPane,Matrix4x4.CreateRotationX(.18f)) })
        {
            backend.SetScene(paneScene with { Materials=paneMaterials,Meshes=[paneScene.Meshes[0],mesh],
                Instances=[paneScene.Instances[0],new("parallel glass",1,transform)] });
            backend.CausticsEnabled=false; Trace(7); var transmitted=raw.ToArray();
            backend.CausticsEnabled=true; Trace(7); var photons=raw.ToArray();
            var positions=surface.ToArray(); double maximumError=0; int groundPixels=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                if(positions[y,x].W>0 && Math.Abs(positions[y,x].Y)<.001)
                {
                    maximumError=Math.Max(maximumError,Math.Abs(photons[y,x].X-transmitted[y,x].X)); groundPixels++;
                }
            Require(groundPixels>100,"Parallel-glass regression has no wet-ground receiver");
            Require(maximumError<.0001,$"Parallel glass produces photon blotches before denoising: max error={maximumError:F6}");
            Console.WriteLine($"PASS parallel glass / wet ground: {groundPixels} receiver pixels, raw max error={maximumError:E2}");
        }
        double TimeFrames(int start)
        {
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 16; i++) Trace(start + i);
            return timer.Elapsed.TotalMilliseconds / 16;
        }
        var scene = MakeScene(false, 128); backend.SetScene(scene); backend.CausticsEnabled = false;
        backend.RestirEnabled = false; Trace(0); var reference = raw.ToArray();
        double classicMs = TimeFrames(1);
        backend.RestirEnabled = true; var mean = new double[height, width];
        double initialError = 0, finalError = 0, expected = 0, actual = 0;
        for (int frame = 0; frame < 64; frame++)
        {
            Trace(frame); var values = raw.ToArray();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Require(float.IsFinite(values[y, x].X), "Nonfinite reservoir output");
                mean[y, x] += values[y, x].X / 64;
                if (frame == 0) initialError += Math.Pow(values[y, x].X - reference[y, x].X, 2);
            }
        }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            expected += reference[y, x].X; actual += mean[y, x];
            finalError += Math.Pow(mean[y, x] - reference[y, x].X, 2);
        }
        double energyRatio = actual / expected;
        Require(Math.Abs(energyRatio - 1) < .04, $"ReSTIR energy changed: {energyRatio}");
        Require(finalError < initialError * .3, "ReSTIR average fails to converge");
        double restirMs = TimeFrames(64);
        // An explicit camera cut, a discontinuous frame and repeated seed must all ignore old reservoirs.
        view = view with { Origin = view.Origin + new Vector3(.2f, 0, 0) };
        Trace(80, true); var cut = raw.ToArray(); Trace(80); var repeated = raw.ToArray();
        Require(cut.Cast<Float4>().SequenceEqual(repeated.Cast<Float4>()), "Camera-cut reset reused history");
        var d = signals.Diffuse.ToArray(); var s = signals.Specular.ToArray(); var a = signals.DiffuseFactor.ToArray(); var e = signals.Unfiltered.ToArray();
        var specFactors = signals.SpecularFactor.ToArray();
        double lobeError = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            lobeError = Math.Max(lobeError, Math.Abs(d[y,x].X * a[y,x].X + s[y,x].X * specFactors[y,x].X + e[y,x].X - repeated[y,x].X));
        Require(lobeError < .0001, "ReSTIR lobe signals no longer reconstruct raw radiance");
        Console.WriteLine($"PASS ReSTIR: 128 lights, energy ratio={energyRatio:F5}, averaged/initial MSE={finalError/initialError:F5}, classic={classicMs:F3} ms, ReSTIR={restirMs:F3} ms, cut/reset, NRD lobe error={lobeError:E2}");

        // Area-domain samples need their area PDF (not merely a triangle-selection probability).
        SceneMaterial emissive = SceneMaterial.Default; emissive.Factors.X = 0; emissive.Flags.Z = 1; emissive.Emissive = new(4,3,2,0);
        SceneMesh emitterMesh = scene.Meshes[0] with { Name = "area lamp", Material = 2 };
        var areaScene = scene with { Lights = [], Materials = [..scene.Materials,emissive], Meshes = [..scene.Meshes,emitterMesh],
            Instances = [scene.Instances[0],new("emitter",2,Matrix4x4.CreateScale(.22f)*Matrix4x4.CreateTranslation(0,2.1f,0))] };
        backend.SetScene(areaScene); backend.RestirEnabled = false;
        double AreaEnergy(bool restir)
        {
            backend.RestirEnabled = restir; double total = 0;
            for (int frame=0;frame<64;frame++)
            {
                Trace(frame); var values=raw.ToArray(); var positions=surface.ToArray();
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    if(positions[y,x].W>0 && Math.Abs(positions[y,x].Y)<.001) total+=values[y,x].X;
            }
            return total;
        }
        double areaReference=AreaEnergy(false),areaRatio=AreaEnergy(true)/areaReference;
        Require(areaReference>0 && Math.Abs(areaRatio-1)<.05,$"Emissive area PDF/MIS energy changed: {areaRatio}");
        Console.WriteLine($"PASS emissive-area ReSTIR: energy ratio={areaRatio:F5}");

        view = CameraFrame.Look(new(0, 4, 3.5f), new(0, -4, -3.5f), .8f, .001f, 100);
        var glass = MakeScene(true, 1); backend.SetScene(glass); backend.CausticsEnabled = true;
        int buildsBefore=backend.PhotonBuildCount;
        mean = new double[height, width];
        for (int frame = 0; frame < 24; frame++)
        {
            Trace(frame); var values = raw.ToArray();
            foreach (var value in values) Require(float.IsFinite(value.X) && value.X >= 0, "Nonfinite/negative photon radiance");
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) mean[y,x] += values[y,x].X / 24;
        }
        var locations = surface.ToArray();
        Require(backend.PhotonBuildCount==buildsBefore+1,"Static photon map is randomly rebuilt each frame");
        var fixedView=view; view=view with { Origin=view.Origin+new Vector3(.01f,0,0) }; Trace(24,true); view=fixedView;
        Require(backend.PhotonBuildCount==buildsBefore+1,"Camera cut changes world-space photon samples");
        lightOverride=new(true,0,65,.25f); Trace(25);
        Require(backend.PhotonBuildCount==buildsBefore+2,"New sun does not invalidate photon map");
        lightOverride=lightOverride with { Intensity=.5f }; Trace(26);
        Require(backend.PhotonBuildCount==buildsBefore+3,"Sun intensity changes retain old photon power");
        lightOverride=default; Trace(27);
        Require(backend.PhotonBuildCount==buildsBefore+4,"Disabled sun leaves photons behind");
        Console.WriteLine("PASS photon cache: static frames/camera cut reuse the map; sun toggle/intensity rebuild it");
        var causticRaw=raw.ToArray(); d=signals.Diffuse.ToArray(); s=signals.Specular.ToArray(); a=signals.DiffuseFactor.ToArray(); e=signals.Unfiltered.ToArray(); specFactors=signals.SpecularFactor.ToArray();
        double causticLobeError=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            causticLobeError=Math.Max(causticLobeError,Math.Abs(d[y,x].X*a[y,x].X+s[y,x].X*specFactors[y,x].X+e[y,x].X-causticRaw[y,x].X));
        Require(causticLobeError<.0001,"Caustic diffuse/specular signals do not reconstruct raw radiance");
        backend.CausticsEnabled = false; Trace(0); var straight = raw.ToArray();
        backend.SetScene(glass with { Instances = [glass.Instances[0]] }); Trace(0); var unobstructed = raw.ToArray();
        double peakRatio = 0, shadowRatio = 1, meanCaustic = 0; int ground = 0, focused = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (locations[y,x].W <= 0 || Math.Abs(locations[y,x].Y) > .001 || unobstructed[y,x].X <= .01) continue;
            double ratio = mean[y,x] / unobstructed[y,x].X;
            peakRatio = Math.Max(peakRatio, ratio); shadowRatio = Math.Min(shadowRatio, ratio);
            meanCaustic += mean[y,x]; ground++;
            if (mean[y,x] > straight[y,x].X * 1.5 && ratio > 1.5) focused++;
        }
        Require(peakRatio > 1.5 && focused >= 4, $"Refraction did not focus light: peak={peakRatio}, focused pixels={focused}");
        Require(shadowRatio < .5, "Glass has no refracted shadow footprint");
        var noBending = (SceneMaterial[])glass.Materials.Clone(); noBending[1].Transmission.Y = 1;
        backend.CausticsEnabled = true; backend.SetScene(glass with { Materials = noBending }); Trace(0);
        var noBendingPixels = raw.ToArray(); double unitIorError = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if (locations[y,x].W > 0 && Math.Abs(locations[y,x].Y) < .001)
                unitIorError = Math.Max(unitIorError, Math.Abs(noBendingPixels[y,x].X - unobstructed[y,x].X));
        Require(unitIorError < .002, $"IOR=1 retains phantom caustics: {unitIorError}");
        foreach (int type in new[] { 1, 2 })
        {
            var localLight = new SceneLight { PositionType = new(0,4,0,type), DirectionRange = new(0,-1,0,20),
                ColorIntensity = new(1,1,1,30), Spot = new(.98f,.8f,0,0) };
            backend.SetScene(glass with { Lights = [localLight] }); backend.CausticsEnabled = false;
            Trace(0); var localStraight = raw.ToArray(); backend.CausticsEnabled = true;
            var focusedMean = new double[height,width];
            for(int frame=0;frame<12;frame++)
            {
                Trace(frame); var values=raw.ToArray();
                for(int y=0;y<height;y++)for(int x=0;x<width;x++) focusedMean[y,x]+=values[y,x].X/12;
            }
            int concentrated=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                if(locations[y,x].W>0 && Math.Abs(locations[y,x].Y)<.001 && focusedMean[y,x]>Math.Max(localStraight[y,x].X*1.5,.1)) concentrated++;
            Require(concentrated>=4,$"Light type {type} does not produce refractive caustics");
            Console.WriteLine($"PASS caustic light type {type}: {concentrated} concentrated receiver pixels");
        }
        backend.SetScene(glass with { Lights = [] }); Trace(1);
        Require(raw.ToArray().Cast<Float4>().All(v => v.X == 0 && v.Y == 0 && v.Z == 0), "Disabled lights retain photon energy");
        // Rebind/resizing allocates fresh history; returning to the original resources must also be valid.
        using (var smallRaw = device.AllocateReadWriteTexture2D<Float4>(32, 24))
        using (var smallNormal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(32, 24))
        using (var smallSurface = device.AllocateReadWriteTexture2D<Float4>(32, 24))
        using (var smallGuide = device.AllocateReadWriteTexture2D<Float4>(32, 24))
        using (var smallSignals = new PbrSignals(device, 32, 24))
            backend.Trace(view, 2, 1, 1, default, 0, smallRaw, smallNormal, smallSurface, smallGuide,
                smallSignals);
        Trace(3);
        for (ulong i = 0; i < debug.NumStoredMessages; i++)
        {
            var message = debug.GetMessage(i);
            Require(message.Severity != MessageSeverity.Error && message.Severity != MessageSeverity.Corruption, message.Description);
        }
        Directory.CreateDirectory("output/lighting");
        SavePpm("output/lighting/caustics.ppm", mean);
        File.WriteAllText("output/lighting/results.json", JsonSerializer.Serialize(new { classicMs, restirMs, energyRatio,
            mseRatio = finalError/initialError, lobeError, causticLobeError, areaRatio, peakRatio, shadowRatio, focused, unitIorError, ground, meanCaustic,
            validation = "D3D12 debug layer: no errors; reset, resize, scene/light change passed" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS caustics: peak/unoccluded={peakRatio:F2}, shadow={shadowRatio:F3}, focused pixels={focused}, IOR=1 error={unitIorError:E2}; no D3D12 errors, resize and light reset passed.");
    }

    private static SceneAsset MakeScene(bool glass, int lightCount)
    {
        SceneMaterial diffuse = SceneMaterial.Default; diffuse.BaseColor = new(.8f,.8f,.8f,1); diffuse.Factors = new(0,1,1,1); diffuse.Flags.Z = 1;
        SceneMaterial volume = diffuse; volume.BaseColor = Vector4.One; volume.Factors.Y = .025f; volume.Transmission = new(1,1.5f,1,0);
        SceneVertex Vertex(Vector3 p, Vector3 n) => new() { Position = new(p,1), Normal = new(n,0), Color = Vector4.One };
        SceneMesh floor = new("floor", [Vertex(new(-3,0,-3),Vector3.UnitY),Vertex(new(-3,0,3),Vector3.UnitY),Vertex(new(3,0,3),Vector3.UnitY),Vertex(new(3,0,-3),Vector3.UnitY)], [0,1,2,0,2,3], 0);
        const int rings = 48, sectors = 96;
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        for (int j = 0; j <= rings; j++) for (int i = 0; i <= sectors; i++)
        {
            float theta = MathF.PI*j/rings, phi = 2*MathF.PI*i/sectors;
            Vector3 n = new(MathF.Sin(theta)*MathF.Cos(phi),MathF.Cos(theta),MathF.Sin(theta)*MathF.Sin(phi));
            vertices.Add(Vertex(new Vector3(0,1.15f,0)+n*.65f,n));
        }
        void Triangle(uint a, uint b, uint c)
        {
            Vector3 P(uint i) { var p=vertices[(int)i].Position; return new(p.X,p.Y,p.Z); }
            if (Vector3.Dot(Vector3.Cross(P(b)-P(a),P(c)-P(a)),P(a)-new Vector3(0,1.15f,0)) < 0) (b,c)=(c,b);
            indices.AddRange([a,b,c]);
        }
        for (int j = 0; j < rings; j++) for (int i = 0; i < sectors; i++)
        { uint a=(uint)(j*(sectors+1)+i),b=a+(uint)sectors+1; Triangle(a,b,a+1); Triangle(a+1,b,b+1); }
        SceneMesh sphere = new("glass sphere", vertices.ToArray(), indices.ToArray(), 1);
        var lights = new SceneLight[lightCount];
        for (int i=0;i<lightCount;i++)
        {
            float angle=i*2*MathF.PI/lightCount;
            lights[i]=lightCount==1 ? new() { DirectionRange=new(0,-1,0,0),ColorIntensity=new(1,1,1,2) } :
                new() { PositionType=new(MathF.Cos(angle)*2,2.5f,MathF.Sin(angle)*2,1),ColorIntensity=new(1,.9f,.8f,.2f+(i%7)*.15f) };
        }
        return new("Lighting regression", [floor,sphere], glass ? [new("ground",0,Matrix4x4.Identity),new("lens",1,Matrix4x4.Identity)] : [new("ground",0,Matrix4x4.Identity)],
            [diffuse,volume], [], lights, new(-3,0,-3),new(3,2,3),[]);
    }
    private static SceneMesh Box(Vector3 minimum,Vector3 maximum,int material,bool tessellatePanels=false)
    {
        var vertices=new List<SceneVertex>(); var indices=new List<uint>();
        void Face(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 normal)
        {
            int steps=tessellatePanels && Math.Abs(normal.Y)>.9f?32:1;
            uint first=(uint)vertices.Count; bool outward=Vector3.Dot(Vector3.Cross(b-a,c-a),normal)>0;
            for(int v=0;v<=steps;v++)for(int u=0;u<=steps;u++)
                vertices.Add(new() { Position=new(a+(b-a)*(u/(float)steps)+(d-a)*(v/(float)steps),1),Normal=new(normal,0),Color=Vector4.One });
            for(int v=0;v<steps;v++)for(int u=0;u<steps;u++)
            {
                uint p=first+(uint)(v*(steps+1)+u),q=p+1,r=q+(uint)steps+1,s=r-1;
                if(outward) indices.AddRange([p,q,r,p,r,s]); else indices.AddRange([p,r,q,p,s,r]);
            }
        }
        var l=minimum; var h=maximum;
        Face(new(l.X,l.Y,l.Z),new(h.X,l.Y,l.Z),new(h.X,l.Y,h.Z),new(l.X,l.Y,h.Z),-Vector3.UnitY);
        Face(new(l.X,h.Y,l.Z),new(h.X,h.Y,l.Z),new(h.X,h.Y,h.Z),new(l.X,h.Y,h.Z),Vector3.UnitY);
        Face(new(l.X,l.Y,l.Z),new(l.X,h.Y,l.Z),new(l.X,h.Y,h.Z),new(l.X,l.Y,h.Z),-Vector3.UnitX);
        Face(new(h.X,l.Y,l.Z),new(h.X,h.Y,l.Z),new(h.X,h.Y,h.Z),new(h.X,l.Y,h.Z),Vector3.UnitX);
        Face(new(l.X,l.Y,l.Z),new(h.X,l.Y,l.Z),new(h.X,h.Y,l.Z),new(l.X,h.Y,l.Z),-Vector3.UnitZ);
        Face(new(l.X,l.Y,h.Z),new(h.X,l.Y,h.Z),new(h.X,h.Y,h.Z),new(l.X,h.Y,h.Z),Vector3.UnitZ);
        return new("parallel glass slab",vertices.ToArray(),indices.ToArray(),material);
    }
    private static void SavePpm(string path, double[,] values)
    {
        int h=values.GetLength(0),w=values.GetLength(1); using var stream=File.Create(path);
        using var writer=new BinaryWriter(stream); writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));
        for(int y=0;y<h;y++)for(int x=0;x<w;x++) { byte c=(byte)(255*Math.Pow(values[y,x]/(1+values[y,x]),1/2.2)); writer.Write(c);writer.Write(c);writer.Write(c); }
    }
}
