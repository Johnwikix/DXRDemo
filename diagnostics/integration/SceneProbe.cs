using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ComputeSharp;
using DXRDemo.Assets.Importers;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Scene;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using ComputeSharp.Interop;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

internal static class SceneProbe
{
    internal static async Task Glass(string path)
    {
        using var debugLayer=D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debugLayer.EnableDebugLayer();
        var document=await GltfImporter.LoadAsync(Path.GetFullPath(path)); var asset=document.Scenes[document.DefaultScene];
        Require(Marshal.SizeOf<SceneMaterial>()==432,"Material GPU ABI changed");
        Require(asset.Materials.Any(m=>m.Transmission.X>0 && m.Transmission.Z>0),"Demo has no refractive volume");
        using var device=GraphicsDevice.GetDefault(); using var native=NativeDevice(device);
        using var debug=native.QueryInterface<ID3D12InfoQueue>();
        using var backend=new DxrSceneBackend();backend.Initialize(device);backend.SetScene(asset);
        const int width=320,height=240;
        using var raw=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var normals=device.AllocateReadWriteTexture2D<Rgba32,Float4>(width,height);
        using var surface=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var guide=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var signals=new PbrSignals(device,width,height);
        using var target=device.AllocateReadWriteTexture2D<Rgba64,Float4>(width,height);
        var camera=new CameraController();camera.FrameBounds(asset.Minimum,asset.Maximum,width/(float)height);camera.Rotate(.50f,.23f);camera.Zoom(3);
        var view=camera.Advance(0,out _,out _);
        void Trace(int frame,int spp,bool opaque=false)=>backend.Trace(view,frame,spp,10,default,0,raw,normals,surface,guide,signals,opaqueForNrd:opaque);
        Trace(0,2); var first=surface.ToArray();Trace(1,2);var second=surface.ToArray();
        Require(first.Cast<Float4>().SequenceEqual(second.Cast<Float4>()),"Refractive primary geometry flickers between frames");
        var n=normals.ToArray();var mask=new bool[width*height];int pixels=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int material=n[y,x].A;
            if(first[y,x].W>0 && material<asset.Materials.Length && asset.Materials[material].Transmission.X>0){mask[y*width+x]=true;pixels++;}
        }
        Require(pixels>100,"No visible glass pixels");
        double[] reference=new double[width*height*3];
        for(int frame=0;frame<32;frame++)
        {
            Trace(1000+frame,16);var values=raw.ToArray();
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){int p=(y*width+x)*3;var v=values[y,x];reference[p]+=v.X/32;reference[p+1]+=v.Y/32;reference[p+2]+=v.Z/32;}
        }
        Trace(47,2); var noisy=raw.ToArray();
        using var sr=new SuperResolutionRenderer(device);sr.Configure(ReconstructionMode.Off,100,width,height,true,true);
        for(int frame=0;frame<48;frame++)
        {
            Trace(frame,2,true);Require(sr.Execute(raw,normals,surface,target,default,2.7f,default,RayTraceDenoiserMode.NrdRelax,
                HdrRenderParameters.Default,1.0/60,guide,view,signals,-2,transparentBackend:backend),"Glass NRD execution failed");
        }
        Require(sr.Status.NrdActive,"Glass validation ran a fallback");
        var denoised=ReconstructionProbe.ReadTexture(native,sr.DenoisedColor,true);
        double rawError=0,filteredError=0;double Compress(double value)=>Math.Max(value,0)/(1+Math.Max(value,0));
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(mask[y*width+x])
        {
            int p=y*width+x;Float4 v=noisy[y,x];
            for(int c=0;c<3;c++){double expected=Compress(reference[p*3+c]);double actual=Compress(c==0?v.X:c==1?v.Y:v.Z);
                rawError+=Math.Pow(actual-expected,2);filteredError+=Math.Pow(Compress(denoised[p*4+c])-expected,2);}
        }
        rawError=Math.Sqrt(rawError/(pixels*3));filteredError=Math.Sqrt(filteredError/(pixels*3));
        Directory.CreateDirectory("output/scenes");Save(target,"output/scenes/glass-nrd.png");
        Console.WriteLine($"Glass: {pixels} pixels, 512-SPP reference, 2-SPP raw RMSE={rawError:F6}, NRD RMSE={filteredError:F6}");
        Require(filteredError<rawError*.9,"Glass denoising does not reduce noise");
        sr.Configure(ReconstructionMode.Off,100,width,height,false,true);
        Trace(47,2);
        sr.Execute(raw,normals,surface,target,default,2.7f,default,RayTraceDenoiserMode.None,
            HdrRenderParameters.Default,1.0/60,guide,view,signals,-2);
        Save(target,"output/scenes/glass-raw-2spp.png");
        var mean=new Float4[height,width];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){int p=(y*width+x)*3;mean[y,x]=new((float)reference[p],(float)reference[p+1],(float)reference[p+2],1);}
        raw.CopyFrom(mean);
        sr.Execute(raw,normals,surface,target,default,2.7f,default,RayTraceDenoiserMode.None,
            HdrRenderParameters.Default,1.0/60,guide,view,signals,-2);
        Save(target,"output/scenes/glass-reference-512spp.png");
        var noRefraction=(SceneMaterial[])asset.Materials.Clone();
        for(int i=0;i<noRefraction.Length;i++)if(noRefraction[i].Transmission.X>0)noRefraction[i].Transmission.Y=1;
        backend.SetScene(asset with { Materials=noRefraction });Trace(1047,16);var straight=raw.ToArray();
        backend.SetScene(asset);Trace(1047,16);var refracted=raw.ToArray();
        double iorDifference=0;for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(mask[y*width+x])iorDifference+=Math.Abs(straight[y,x].X-refracted[y,x].X);
        Require(iorDifference/pixels>.001,"IOR has no effect on transmission");
        VerifyDebug(debug);
        Console.WriteLine($"PASS: stable glass guides, transmission/volume import, NRD noise reduction, IOR refraction (mean difference {iorDifference/pixels:F6}). DLSSD NOT executed.");
    }
    internal static async Task Preview(string path)
    {
        using var device=GraphicsDevice.GetDefault();
        using var pass=new RayTracePass(new DxrMeshBackend());pass.Initialize(device,default);
        using var target=device.AllocateReadWriteTexture2D<Rgba64,Float4>(800,600);
        Task<SceneDocument> load=pass.ImportAsync(Path.GetFullPath(path));
        while(!load.IsCompleted){pass.TryExecute(target,800,600,TimeSpan.Zero,HdrRenderParameters.Default);await Task.Delay(1);}
        var document=await load;var asset=document.Scenes[document.DefaultScene];
        Console.WriteLine($"Preview: {asset.Instances.Length} instances, {asset.TriangleCount} triangles, {asset.Materials.Length} materials, {asset.Lights.Length} embedded lights.");
        pass.ExternalLightingEnabled=false;pass.MaxBounces=10;pass.Samples=2;pass.Exposure=-2;
        pass.Camera.Rotate(.50f,.23f);pass.Camera.Zoom(3);
        for(int i=0;i<48;i++){pass.TryExecute(target,800,600,TimeSpan.Zero,HdrRenderParameters.Default);if(i%8==7)Console.WriteLine($"Preview frame {i+1}/48");}
        Require(pass.ReconstructionStatus?.NrdActive==true,"Preview NRD not active");
        Directory.CreateDirectory("output/scenes");Save(target,"output/scenes/RainyCorner-dxr.png");
        Console.WriteLine("PASS: only-model-lighting preview, actual NRD, output/scenes/RainyCorner-dxr.png");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task Run(string? external = null)
    {
        Directory.CreateDirectory("output/scenes");
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        CameraTests();
        string fixture = MakeFixture();
        SceneDocument document = await GltfImporter.LoadAsync(fixture);
        SceneDocument glb = await GltfImporter.LoadAsync(Path.ChangeExtension(fixture, ".glb"));
        Require(document.Scenes.Length == 2 && document.DefaultScene == 0, "Document scene selection");
        SceneAsset asset = document.Scenes[0];
        Require(asset.Instances.Length == 2 && asset.Meshes.Length == 1 && asset.TriangleCount == 4, "Instancing was flattened or lost");
        Require(glb.Scenes[0].TriangleCount == asset.TriangleCount, "GLB and glTF disagree");
        Require(asset.Textures.Length == 1 && asset.Textures[0].Mips.Length == 2 && asset.Textures[0].Mips[1].Length == 4, "Odd texture mip chain");
        Require(asset.Lights.Length == 1 && asset.Lights[0].PositionType.W == 1 && asset.Lights[0].ColorIntensity.W == 40, "Punctual light contract");
        Require(asset.Materials[0].Factors.X == 0 && Math.Abs(asset.Materials[0].Factors.Y - .35f) < .001, "PBR material factors");
        Require(asset.Minimum.X < -2 && asset.Maximum.X > 2, "World-space bounds");
        var invalid = JsonNode.Parse(File.ReadAllText(fixture))!; invalid["extensionsRequired"] = new JsonArray("KHR_materials_anisotropy");
        invalid["extensionsUsed"] = new JsonArray("KHR_lights_punctual", "KHR_materials_anisotropy");
        File.WriteAllText("output/scenes/required.gltf", invalid.ToJsonString());
        bool rejected = false;
        try { await GltfImporter.LoadAsync(Path.GetFullPath("output/scenes/required.gltf")); } catch { rejected = true; }
        Require(rejected, "Unsupported required extension was silently ignored");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); rejected = false;
            try { await GltfImporter.LoadAsync(fixture, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "Canceled import continued");
        }
        Console.WriteLine("PASS: camera integration, glTF/GLB import, shared geometry, texture mips, lights, extension rejection, cancellation.");

        using var device = GraphicsDevice.GetDefault();
        using var native = NativeDevice(device); using var debugQueue = native.QueryInterface<ID3D12InfoQueue>();
        debugQueue.SetBreakOnSeverity(MessageSeverity.Error, false); debugQueue.SetBreakOnSeverity(MessageSeverity.Corruption, false);
        using var backend = new DxrSceneBackend(); backend.Initialize(device);
        try { backend.SetScene(asset); } catch { VerifyDebug(debugQueue); throw; }
        const int width = 192, height = 128;
        using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var surface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var guide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        var camera = new CameraController(); camera.FrameBounds(asset.Minimum, asset.Maximum, width / (float)height);
        var view = camera.Advance(0, out _, out _);
        void Trace(int frame, float env = 0, SunLightSettings sun = default, int bounces = 3) => backend.Trace(view, frame, 4, bounces, default, env, raw, normals, surface, guide, signals, sun);
        Trace(0);
        VerifyDebug(debugQueue);
        float energy = Energy(raw.ToArray());
        Console.WriteLine($"Raw energy={energy}, surface hits={surface.ToArray().Cast<Float4>().Count(v=>v.W>0)}, light={asset.Lights[0]}, material={asset.Materials[0].BaseColor}, factors={asset.Materials[0].Factors}");
        Require(energy > .005f, "Punctual light produced black output");
        Require(Energy(signals.Diffuse.ToArray()) < 1e-6 && Energy(signals.Specular.ToArray()) < 1e-6,
            "Deterministic primary direct light leaks into NRD inputs");
        Trace(0, .3f);
        Require(Energy(signals.Specular.ToArray()) > 0, "Specular lobe missing");
        Require(Energy(signals.Diffuse.ToArray()) > 0, "Diffuse lobe missing");
        var rawPixels = raw.ToArray(); var dPixels = signals.Diffuse.ToArray(); var sPixels = signals.Specular.ToArray(); var aPixels = signals.DiffuseFactor.ToArray(); var ePixels = signals.Unfiltered.ToArray();
        var specFactors = signals.SpecularFactor.ToArray();
        double difference = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var d = dPixels[y,x]; var s = sPixels[y,x]; var a = aPixels[y,x]; var e = ePixels[y,x]; var r = rawPixels[y,x];
            difference = Math.Max(difference, Math.Abs(d.X * a.X + s.X * specFactors[y,x].X + e.X - r.X));
        }
        Require(difference < .0001, "Lobe decomposition does not reconstruct raw radiance");
        backend.SetScene(asset with { Lights = [] }); Trace(0);
        Require(Energy(raw.ToArray()) < .000001f, "Hidden default light contaminates imported scene");
        var metal = (SceneMaterial[])asset.Materials.Clone(); metal[0].Factors.X = 1;
        backend.SetScene(asset with { Materials = metal }); Trace(1, 1);
        Require(Energy(signals.Diffuse.ToArray()) < .000001f && Energy(signals.Specular.ToArray()) > .000001f, "Metal is not purely specular");
        var alpha = (SceneMaterial[])asset.Materials.Clone(); alpha[0].BaseColor.W = 0; alpha[0].Flags.X = 1;
        backend.SetScene(asset with { Materials = alpha }); Trace(2);
        Require(surface.ToArray().Cast<Float4>().All(v => v.W < 0), "Alpha mask does not reject intersections");
        alpha = (SceneMaterial[])alpha.Clone(); alpha[0].Flags.X = 2;
        backend.SetScene(asset with { Materials = alpha }); Trace(2);
        Require(Energy(raw.ToArray()) < 1e-6, "Zero-alpha blend remains opaque");
        Require(signals.Albedo.ToArray().Cast<Float4>().Any(v => v.W == 1), "Transparent misses lose SR reactive mask");
        alpha = (SceneMaterial[])alpha.Clone(); alpha[0].BaseColor.W = .5f;
        backend.SetScene(asset with { Materials = alpha }); Trace(3);
        float transparencyRatio = Energy(raw.ToArray()) / energy;
        Require(transparencyRatio > .4 && transparencyRatio < .6, $"Alpha blend energy {transparencyRatio}");
        var lamp = asset.Lights[0]; lamp.PositionType.W = 0; lamp.DirectionRange = new(0,0,-1,0); lamp.ColorIntensity.W = 2;
        backend.SetScene(asset with { Lights = [lamp] }); Trace(4);
        Require(Energy(raw.ToArray()) > .001, "Directional light is dark");
        lamp.PositionType.W = 2; lamp.ColorIntensity.W = 40; lamp.Spot = new(.95f,.7f,0,0);
        backend.SetScene(asset with { Lights = [lamp] }); Trace(4);
        Require(Energy(raw.ToArray()) > .001, "Spot light is dark");
        lamp.DirectionRange.Z = 1;
        backend.SetScene(asset with { Lights = [lamp] }); Trace(4);
        Require(Energy(raw.ToArray()) < 1e-6, "Spot light emits behind its cone");
        var emissive = (SceneMaterial[])asset.Materials.Clone(); emissive[0].Emissive = new(1,.2f,.1f,0);
        backend.SetScene(asset with { Materials = emissive, Lights = [] }); Trace(5);
        Require(Energy(signals.Unfiltered.ToArray()) > .01, "Emissive material missing");
        // Rendering a scaled copy with the correspondingly framed camera must preserve texture LOD.
        backend.SetScene(asset); Trace(6); var originalAlbedo = signals.Albedo.ToArray(); var originalSurface = surface.ToArray();
        var scaled = asset with { Instances = asset.Instances.Select(i => i with { Transform = i.Transform * Matrix4x4.CreateScale(100) }).ToArray(), Minimum = asset.Minimum * 100, Maximum = asset.Maximum * 100 };
        camera.FrameBounds(scaled.Minimum,scaled.Maximum,width/(float)height); view=camera.Advance(0,out _,out _);
        backend.SetScene(scaled); Trace(6); var scaledAlbedo=signals.Albedo.ToArray(); var scaledSurface=surface.ToArray();
        double scaleError=0; int scaleCount=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(originalSurface[y,x].W>0&&scaledSurface[y,x].W>0)
        { scaleError+=Math.Abs(originalAlbedo[y,x].Y-scaledAlbedo[y,x].Y); scaleCount++; }
        Require(scaleCount>100 && scaleError/scaleCount<.002,"Texture sharpness depends on model scale");
        camera.FrameBounds(asset.Minimum,asset.Maximum,width/(float)height); view=camera.Advance(0,out _,out _);
        backend.SetScene(asset);
        VerifyDebug(debugQueue);
        Console.WriteLine($"PASS: MASK/BLEND (ratio {transparencyRatio:F3}), reactive mask, directional/spot lights, emissive surfaces, scale-invariant texture LOD.");

        using var sr = new SuperResolutionRenderer(device);
        var sunlight = new SunLightSettings(true, 315, 40, 3);
        backend.SetScene(asset with { Lights = [] }); Trace(0, sun: sunlight, bounces: 1);
        float sunEnergy = Energy(raw.ToArray()); Require(sunEnergy > .005, "Enabled sun is dark");
        var litPixels = raw.ToArray();
        Trace(1, sun: sunlight with { Intensity = 6 }, bounces: 1);
        Require(Math.Abs(Energy(raw.ToArray()) / sunEnergy - 2) < .001, "Sun intensity is not linear");
        Trace(2, sun: sunlight with { Enabled = false }, bounces: 1);
        Require(Energy(raw.ToArray()) < 1e-6, "Disabled sun still contributes");
        Trace(3, sun: sunlight with { Azimuth = 180 }, bounces: 1);
        Require(Energy(raw.ToArray()) < 1e-6, "Sun direction does not follow angles");
        var occluder = new SceneInstance("Shadow occluder", 0, Matrix4x4.CreateScale(.35f) * Matrix4x4.CreateTranslation(-1.2f, .2f, .6f));
        var shadowMaterials = (SceneMaterial[])asset.Materials.Clone(); shadowMaterials[0].Flags.Z = 1;
        backend.SetScene(asset with { Lights = [], Materials = shadowMaterials, Instances = [..asset.Instances, occluder], Maximum = asset.Maximum + Vector3.UnitZ });
        Trace(0, sun: sunlight, bounces: 1);
        int shadowPixels = 0; var shadowedPixels = raw.ToArray(); var shadowedSurface = surface.ToArray();
        for (int y=0;y<height;y++) for(int x=0;x<width;x++)
            if(shadowedSurface[y,x].W>0 && Math.Abs(shadowedSurface[y,x].Z)<.001 && shadowedPixels[y,x].X<1e-6 && litPixels[y,x].X>.01) shadowPixels++;
        Require(shadowPixels>8, "Shadow regression fixture has no visible cast shadow");
        sr.Configure(ReconstructionMode.Off,100,width,height,false,true);
        Require(sr.Execute(raw,normals,surface,target,default,2.7f,default,RayTraceDenoiserMode.None,HdrRenderParameters.Default,1.0/60,guide,view,signals),sr.Status.Message);
        var referencePixels = target.ToArray(); Save(target,"output/scenes/sun-shadow-reference.png");
        sr.Configure(ReconstructionMode.Off,100,width,height,true,true);
        for(int frame=0;frame<12;frame++)
        {
            Trace(frame,sun:sunlight,bounces:1);
            Require(sr.Execute(raw,normals,surface,target,default,2.7f,default,RayTraceDenoiserMode.NrdRelax,HdrRenderParameters.Default,1.0/60,guide,view,signals),sr.Status.Message);
        }
        Require(sr.Status.NrdActive,"Shadow check did not run NRD");
        Save(target,"output/scenes/sun-shadow-nrd.png");
        var nrdPixels=target.ToArray(); double shadowError=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            shadowError=Math.Max(shadowError,Math.Abs(nrdPixels[y,x].R-referencePixels[y,x].R)/65535.0);
        Require(shadowError<.0001,$"NRD changes deterministic sun/shadow edges: {shadowError}");
        Console.WriteLine($"PASS: sun toggle, angles, linear intensity; {shadowPixels} shadow pixels, NRD shadow max error={shadowError:E2}.");
        backend.SetScene(asset);
        foreach (var mode in new[] { RayTraceDenoiserMode.None, RayTraceDenoiserMode.TemporalOnly, RayTraceDenoiserMode.NrdRelax })
        {
            sr.Configure(ReconstructionMode.Off, 100, width, height, mode == RayTraceDenoiserMode.NrdRelax, true);
            Require(sr.LinearPipelineActive, sr.Status.Message);
            for (int frame = 0; frame < 12; frame++) { Trace(frame, .3f); Require(sr.Execute(raw, normals, surface, target, default, 2.7f, default, mode,
                HdrRenderParameters.Default, 1.0/60, guide, view, signals), sr.Status.Message); }
            if (mode == RayTraceDenoiserMode.NrdRelax) Require(sr.Status.NrdActive && sr.NrdDispatchCount > 5, "PBR NRD is a fallback");
            Save(target, $"output/scenes/{mode}.png");
            Require(Energy(raw.ToArray()) > 0, "Empty image");
        }
        Console.WriteLine($"PASS: DXR PBR energy={energy:F6}, lobe error={difference:E2}, metal, explicit lights, 3 denoisers, actual NRD {sr.NrdDispatchCount} dispatches.");

        using var pass = (RayTracePass)DXRDemo.Shaders.ShaderCatalog.All[0].Factory(); pass.Initialize(device, default);
        var packagedDocument = await GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "RainyCorner.glb"));
        var packagedAsset = packagedDocument.Scenes[packagedDocument.DefaultScene];
        string packagedLightCount = $"{packagedAsset.Lights.Length} LIGHTS";
        Require(pass.SceneIndex == RayTracePass.RainyCornerSceneIndex && !pass.ExternalLightingEnabled && pass.Exposure == -2,
            "The application does not start with the packaged night demo");
        for (int attempt = 0; attempt < 300 && pass.ReconstructionStatus?.NrdActive != true; attempt++)
        {
            pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
            await Task.Delay(10);
        }
        Require(pass.ReconstructionStatus?.NrdActive == true && pass.DiagnosticText.Contains(packagedLightCount), "Packaged startup scene did not render: " + pass.ReconstructionStatus?.Message + " / " + pass.DiagnosticText);
        Save(target, "output/scenes/default-demo.png");
        pass.ExternalLightingEnabled = true; pass.Exposure = 0;
        Task<SceneDocument> loading = pass.ImportAsync(external ?? fixture);
        while (!loading.IsCompleted) { pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default); await Task.Delay(1); }
        await loading;
        int previousScene = pass.SceneIndex;
        Require(previousScene > RayTracePass.RainyCornerSceneIndex && pass.Scenes[RayTracePass.RainyCornerSceneIndex].FileName == "RainyCorner.glb",
            "Import replaced the packaged demo catalog entry");
        pass.SceneIndex = 0;
        pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
        pass.SceneIndex = RayTracePass.RainyCornerSceneIndex;
        pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
        Require(pass.DiagnosticText.Contains(packagedLightCount), "Cannot return to packaged demo after an import");
        pass.SceneIndex = previousScene;
        Console.WriteLine("PASS: packaged GLB startup, default selection, persistent demo list and scene switching after import.");
        bool failed = false;
        try { await pass.ImportAsync(Path.GetFullPath("output/scenes/required.gltf")); } catch (Exception) { failed = true; }
        Require(failed && pass.SceneIndex == previousScene, "Failed import replaced current scene");
        for (int frame = 0; frame < 48; frame++) pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
        Save(target, "output/scenes/imported.png");
        // A master override must suppress only external lighting and must restore the stored settings.
        pass.DenoiserMode=RayTraceDenoiserMode.None;pass.MaxBounces=1;pass.Samples=4;
        pass.Sun=new(true,315,45,3);pass.EnvironmentIntensity=4;pass.ExternalLightingEnabled=false;
        pass.TryExecute(target,width,height,TimeSpan.Zero,HdrRenderParameters.Default);var modelOnly=target.ToArray();
        pass.Sun=pass.Sun with { Enabled=false };pass.EnvironmentIntensity=0;pass.ExternalLightingEnabled=true;
        pass.TryExecute(target,width,height,TimeSpan.Zero,HdrRenderParameters.Default);var manualDisable=target.ToArray();
        double overrideError=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)overrideError=Math.Max(overrideError,Math.Abs(modelOnly[y,x].R-manualDisable[y,x].R)/65535.0);
        Require(overrideError<.0001,"Model-only lighting differs from manually disabling external lights");
        pass.DenoiserMode=RayTraceDenoiserMode.NrdRelax;pass.MaxBounces=10;pass.Samples=2;pass.EnvironmentIntensity=1;
        pass.Camera.Mode = CameraMode.Fly; pass.Camera.SetMovement(CameraMovement.Right);
        for (int frame = 0; frame < 8; frame++) pass.TryExecute(target, width, height, TimeSpan.FromSeconds(frame / 60.0), HdrRenderParameters.Default);
        pass.Camera.SetMovement(0); pass.ResetCamera(); pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
        foreach (var reconstruction in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS })
        {
            if (pass.ReconstructionStatus?.Supports(reconstruction) != true) continue;
            pass.ReconstructionMode = reconstruction; pass.RenderScalePercent = 67;
            for (int frame = 0; frame < 8; frame++) pass.TryExecute(target, width, height, TimeSpan.Zero, HdrRenderParameters.Default);
            Require(pass.ReconstructionStatus!.Active == reconstruction && pass.ReconstructionStatus.NrdActive, "PBR NRD+SR fell back");
        }
        Console.WriteLine("PASS: transactional import, free camera, reset, available NRD+SR combinations.");
        await BuiltinShadowTest(device);
        VerifyDebug(debugQueue);
    }

    private static async Task BuiltinShadowTest(GraphicsDevice device)
    {
        const int width=480,height=270;
        var mesh=await MeshData.LoadAsync(0);
        using var backend=new DxrMeshBackend();backend.Initialize(device);
        using var raw=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var normal=device.AllocateReadWriteTexture2D<Rgba32,Float4>(width,height);
        using var surface=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var guide=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var direct=device.AllocateReadWriteTexture2D<Float4>(width,height);
        using var target=device.AllocateReadWriteTexture2D<Rgba64,Float4>(width,height);
        using var sr=new SuperResolutionRenderer(device);sr.Configure(ReconstructionMode.Off,100,width,height,true);
        var sun=new SunLightSettings(true,315,45,5.5f);
        double maxError=0;int shadowPixels=0;
        foreach(float yaw in new[]{0f,.35f})
        {
            var camera=CameraFrame.FromOrbit(yaw,.165f,2.7f);
            for(int frame=0;frame<12;frame++)
            {
                backend.Trace(mesh,width,height,2,1,frame,default,2.7f,raw,normal,surface,default,guide,camera,sun,direct,0);
                device.ForEach(target,new MeshEncodeShader(raw,false,200,1000));
                var reference=target.ToArray();
                if(frame==11 && yaw==0) Save(target,"output/scenes/bunny-shadow-reference.png");
                Require(sr.Execute(raw,normal,surface,target,default,2.7f,default,RayTraceDenoiserMode.NrdRelax,
                    HdrRenderParameters.Default,1.0/60,guide,camera,directLight:direct),sr.Status.Message);
                var actual=target.ToArray();
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    maxError=Math.Max(maxError,Math.Abs(actual[y,x].R-reference[y,x].R)/65535.0);
                if(frame==11 && yaw==0)
                {
                    Save(target,"output/scenes/bunny-shadow-nrd.png");
                    var p=surface.ToArray();var r=raw.ToArray();
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                        if(p[y,x].W>0 && Math.Abs(p[y,x].Y)<.001 && r[y,x].X<1e-6)shadowPixels++;
                }
            }
        }
        Require(shadowPixels>100 && maxError<.0005,$"Bunny shadow edges changed: pixels={shadowPixels}, max={maxError}");
        Console.WriteLine($"PASS: built-in Bunny NRD direct shadows, {shadowPixels} shadow pixels; still/moving max error={maxError:E2}.");
    }
    private static unsafe ID3D12Device5 NativeDevice(GraphicsDevice device)
    { Guid iid=typeof(ID3D12Device5).GUID; nint pointer; InteropServices.GetID3D12Device(device,&iid,(void**)&pointer); return new(pointer); }
    private static void VerifyDebug(ID3D12InfoQueue queue)
    {
        for(ulong i=0;i<queue.NumStoredMessagesAllowedByRetrievalFilter;i++)
        {
            var message=queue.GetMessage(i);
            if(message.Severity is MessageSeverity.Error or MessageSeverity.Corruption) throw new Exception(message.Description);
        }
        queue.ClearStoredMessages();
    }

    private static void CameraTests()
    {
        var c = new CameraController(); var start = c.Advance(0, out _, out _);
        c.Rotate(0,0); Require(c.Advance(0,out _,out _) == start, "Zero pointer delta moves camera");
        c.Rotate(.3f,.1f); var orbit = c.Advance(0,out _,out _); c.Mode=CameraMode.Fly;
        Require(c.Advance(0,out _,out _) == orbit, "Orbit->Fly jumps");
        c.Mode=CameraMode.Orbit; Require(Vector3.Distance(c.Advance(0,out _,out _).Origin,orbit.Origin)<1e-6, "Fly->Orbit jumps");
        Vector3 Travel(int fps, CameraMovement keys)
        {
            var controller=new CameraController { Mode=CameraMode.Fly }; controller.SetMovement(keys);
            CameraFrame f=default; for(int i=0;i<fps;i++) f=controller.Advance(1.0/fps,out _,out _); return f.Origin-start.Origin;
        }
        Require(Vector3.Distance(Travel(30,CameraMovement.Forward),Travel(144,CameraMovement.Forward))<.0001,"Movement depends on FPS");
        Require(Math.Abs(Travel(60,CameraMovement.Forward).Length()-Travel(60,CameraMovement.Forward|CameraMovement.Right).Length())<.0001,"Diagonal speed boost");
        c.SetMovement(CameraMovement.Forward); c.Reset(); Require(c.Advance(1,out _,out _) == start,"Reset retains held movement");
    }
    private static float Energy(Float4[,] data)
    {
        double energy=0; foreach(var pixel in data) { Require(float.IsFinite(pixel.X+pixel.Y+pixel.Z),"Non-finite render output"); energy+=pixel.X+pixel.Y+pixel.Z; }
        return (float)(energy/data.Length/3);
    }
    private static string MakeFixture()
    {
        using var data=new MemoryStream(); using var writer=new BinaryWriter(data,Encoding.UTF8,true);
        foreach(var p in new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0) }) { writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z); }
        foreach(var uv in new[] { new Vector2(0,1),new Vector2(1,1),new Vector2(1,0),new Vector2(0,0) }) {writer.Write(uv.X);writer.Write(uv.Y);}
        foreach(uint index in new uint[]{0,1,2,0,2,3})writer.Write(index);
        byte[] binary=data.ToArray(); File.WriteAllBytes("output/scenes/fixture.bin",binary);
        byte[] pixels=new byte[3*3*4];for(int i=0;i<9;i++){pixels[i*4]=220;pixels[i*4+1]=(byte)(i%2==0?70:180);pixels[i*4+2]=40;pixels[i*4+3]=255;}
        WritePng("output/scenes/albedo.png",3,3,pixels);
        string json="""
        {"asset":{"version":"2.0"},"extensionsUsed":["KHR_lights_punctual"],"scene":0,
         "buffers":[{"uri":"fixture.bin","byteLength":104}],
         "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":32},{"buffer":0,"byteOffset":80,"byteLength":24}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3","min":[-1,-1,0],"max":[1,1,0]},
           {"bufferView":1,"componentType":5126,"count":4,"type":"VEC2"},{"bufferView":2,"componentType":5125,"count":6,"type":"SCALAR"}],
         "images":[{"uri":"albedo.png"}],"textures":[{"source":0}],
         "materials":[{"pbrMetallicRoughness":{"baseColorFactor":[1,1,1,1],"baseColorTexture":{"index":0},"metallicFactor":0,"roughnessFactor":0.35}}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1},"indices":2,"material":0}]}],
         "nodes":[{"mesh":0,"translation":[-1.2,0,0]},{"mesh":0,"translation":[1.2,0,0],"scale":[-1,1,1]},
           {"translation":[0,1,3],"extensions":{"KHR_lights_punctual":{"light":0}}}],
         "scenes":[{"name":"Instanced PBR","nodes":[0,1,2]},{"name":"Single","nodes":[0,2]}],
         "extensions":{"KHR_lights_punctual":{"lights":[{"type":"point","color":[1,1,1],"intensity":40}]}}}
        """;
        string file=Path.GetFullPath("output/scenes/fixture.gltf"); File.WriteAllText(file,json);
        var root=JsonNode.Parse(json)!; root["buffers"]![0]!.AsObject().Remove("uri");
        byte[] utf8=Encoding.UTF8.GetBytes(root.ToJsonString());int jsonSize=(utf8.Length+3)&~3;
        using var glb=File.Create(Path.ChangeExtension(file,".glb"));using var output=new BinaryWriter(glb);
        output.Write(0x46546c67u);output.Write(2u);output.Write((uint)(12+8+jsonSize+8+binary.Length));
        output.Write(jsonSize);output.Write(0x4e4f534au);output.Write(utf8);for(int i=utf8.Length;i<jsonSize;i++)output.Write((byte)32);
        output.Write(binary.Length);output.Write(0x004e4942u);output.Write(binary); return file;
    }
    internal static void Save(ReadWriteTexture2D<Rgba64,Float4> target,string file)
    {
        var pixels=target.ToArray();var values=MemoryMarshal.Cast<Rgba64,ushort>(MemoryMarshal.CreateReadOnlySpan(ref pixels[0,0],pixels.Length));
        byte[] rgba=new byte[values.Length];for(int i=0;i<rgba.Length;i++)rgba[i]=(byte)(values[i]>>8);
        WritePng(file,target.Width,target.Height,rgba);
    }
    private static void WritePng(string path,int width,int height,byte[] rgba)
    {
        using var stream=File.Create(path);stream.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string name,byte[] bytes)
        {
            Span<byte> length=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);stream.Write(length);
            byte[] type=Encoding.ASCII.GetBytes(name);stream.Write(type);stream.Write(bytes);uint crc=0xffffffff;
            foreach(byte b in type.Concat(bytes)){crc^=b;for(int j=0;j<8;j++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
            BinaryPrimitives.WriteUInt32BigEndian(length,~crc);stream.Write(length);
        }
        byte[] header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);header[8]=8;header[9]=6;Chunk("IHDR",header);
        using var compressed=new MemoryStream();using(var zip=new ZLibStream(compressed,CompressionLevel.Optimal,true))
            for(int y=0;y<height;y++){zip.WriteByte(0);zip.Write(rgba,y*width*4,width*4);}
        Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);
    }
}
