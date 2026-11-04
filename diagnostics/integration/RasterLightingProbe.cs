using System.Numerics;
using System.Text.Json;
using DXRDemo.Assets.Importers;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

/// <summary>有限帧、低分辨率的环境/SSR/透明时序回归与真实场景离屏预览。</summary>
internal static class RasterLightingProbe
{
    internal static async Task Run(string[] args)
    {
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault(false);
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        using var cache = new GpuSceneCache();
        bool preview = args.Contains("--raster-preview") || args.Contains("--raster-cards-compare"), hardwareTest = args.Contains("--raster-hardware") || args.Contains("--raster-sparse");
        bool nrdTest = args.Contains("--raster-nrd") || args.Contains("--raster-nrd-missing");
        bool cardsTest = args.Contains("--raster-cards");
        bool clipmapTest = args.Contains("--raster-world-clipmaps");
        bool worldTest = args.Contains("--raster-world") || clipmapTest || cardsTest;
        bool hardware = worldTest || hardwareTest || nrdTest || (preview && !args.Contains("--no-hardware-gi"));
        int shadowSize = preview ? Option(args, "--shadow-size", 1024, 2048) : 256;
        using var backend = new RasterBackend(cache, shadowSize, preview ? 256 : 128, hardware, !hardwareTest && !cardsTest && !args.Contains("--no-nrd"), preview ? Option(args, "--gi-divisor", 4, 4) : args.Contains("--raster-sparse") || worldTest ? 4 : 1, cachedGi: worldTest || preview && !args.Contains("--reference-pixel-gi"), cards: !args.Contains("--no-cards"));
        Console.WriteLine($"Adapter: {GpuName(device)}; hardware lighting requested={hardware}.");
        var initialize=System.Diagnostics.Stopwatch.StartNew();
        backend.Initialize(device);
        Console.WriteLine($"Backend initialization CPU wall={initialize.Elapsed.TotalSeconds:F3} s (excluded from GPU timings).");
        if (preview)
            await Preview(device, cache, backend, args);
        else if (cardsTest)
            RasterCardsProbe.Run(device,backend);
        else if (clipmapTest)
            RasterWorldClipmapProbe.Run(device, backend);
        else if (worldTest)
            RasterWorldProbe.Run(device, backend);
        else if (nrdTest)
            RasterNrdProbe.Run(device, cache, backend, args.Contains("--raster-nrd-missing"));
        else if (hardwareTest)
        {
            RasterHardwareProbe.Run(device, cache, backend);
            if (args.Contains("--raster-sparse")) RasterSparseProbe.Run(device, cache);
        }
        else
        {
            EnvironmentEnergy();
            Reflections(device, backend);
            Transparency(device, backend);
        }
        RasterProbe.Require(backend.HardwareLightingActive || cache.AccelerationBuildCount == 0, "Screen-space raster unexpectedly built AS");
        RasterProbe.Verify(debug);
        Console.WriteLine($"PASS: bounded raster lighting; D3D12 Error/Corruption=0, AS builds={cache.AccelerationBuildCount}, hardware lighting={backend.HardwareLightingActive}.");
    }

    private static void EnvironmentEnergy()
    {
        Vector4 value = new(.5f, 1, 2, 0);
        var uniform = new SceneEnvironment(8, 4, Enumerable.Repeat(value, 32).ToArray(), .7f);
        var filtered = RasterEnvironment.Prepare(uniform, default);
        float error = filtered.Max(p => Vector4.Distance(p, value));
        RasterProbe.Require(filtered.Length == RasterEnvironment.TexelCount && error < 1e-5,
            $"Constant environment lost energy: {error:R}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try { RasterEnvironment.Prepare(uniform, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        RasterProbe.Require(canceled, "Environment preparation ignored cancellation");
        Console.WriteLine($"PASS: independent constant-radiance E/PI and GGX convolution; max error={error:R}.");
    }

    private static void Reflections(DxrDevice device, RasterBackend backend)
    {
        const int width = 128, height = 96;
        var mirror = SceneMaterial.Default;
        mirror.Factors = new(1, .08f, 1, 1);
        var emission = mirror;
        emission.Emissive = new(4, .2f, .1f, 0);
        var asset = RasterProbe.Fixture(mirror);
        var receiver = asset.Meshes[0].Vertices.Select(v =>
        {
            v.Normal = new(.38268343f, 0, .92387953f, 0);
            return v;
        }).ToArray();
        var wall = asset.Meshes[0] with { Material = 1 };
        asset = asset with
        {
            Meshes = [asset.Meshes[0] with { Vertices = receiver }, wall],
            Materials = [mirror, emission],
            Instances = [new("mirror", 0, Matrix4x4.Identity),
                new("emitter", 1, Matrix4x4.CreateScale(.8f) * Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 0, 1))],
            Minimum = new(-1, -1, 0), Maximum = new(1, 1, 2)
        };
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        backend.SetScene(asset);
        float[] Draw(bool enabled, Vector2 jitter = default)
        {
            var f = backend.Render(camera, camera, jitter, default, width, height, 1.0 / 60, true, 0, default, screenReflections: enabled);
            return ReconstructionProbe.ReadTexture(device.Device, f.Color, true);
        }
        int center = (height / 2 * width + width / 2) * 4;
        var off = Draw(false);
        var on = Draw(true);
        RasterProbe.Require(off[center] < 1e-5 && on[center] > 2 && on[center] > on[center + 1] * 10,
            $"SSR did not reflect the visible red emitter: off={off[center]:R}, on={on[center]:R}");
        var repeat = Draw(true);
        RasterProbe.Require(on.SequenceEqual(repeat), "SSR is not deterministic at a fixed camera");
        var jittered = Draw(true, new(.25f, -.25f));
        RasterProbe.Require(jittered[center] > 2, "SSR projection lost raster jitter alignment");
        backend.SetScene(asset with { Instances = [asset.Instances[0]] });
        var removed = Draw(true);
        RasterProbe.Require(removed[center] < 1e-5, "Removed emitter persisted in SSR or produced self-reflection");
        backend.Render(camera, camera, default, default, 64, 48, 1.0 / 60, true, 1, default);
        Console.WriteLine($"PASS: SSR visible emitter={on[center]:R}, disabled={off[center]:R}, removed={removed[center]:R}; deterministic, jitter and resize.");
    }

    private static void Transparency(DxrDevice device, RasterBackend backend)
    {
        const int width = 128, height = 96;
        var material = SceneMaterial.Default;
        material.Flags.X = 2;
        material.BaseColor.W = .5f;
        material.Emissive = new(1, 1, 1, 0);
        material.EmissiveMap.Info.X = 0;
        var bytes = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++)
        {
            byte value = (i % 32 + i / 32) % 2 == 0 ? (byte)255 : (byte)0;
            bytes[i * 4] = bytes[i * 4 + 1] = bytes[i * 4 + 2] = value;
            bytes[i * 4 + 3] = 255;
        }
        backend.SetScene(RasterProbe.Fixture(material) with { Textures = [new("checker", 32, 32, [bytes], false)] });
        using var renderer = new SceneRenderer();
        renderer.Initialize(device);
        renderer.Reconstruction.Configure(ReconstructionMode.Off, 100, width, height, false, true);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        Vector2 previousJitter = default;
        float[]? oldRaw = null, oldTaa = null;
        double rawChange = 0, taaChange = 0;
        for (int i = 0; i < 12; i++)
        {
            Vector2 jitter = renderer.Reconstruction.Jitter(true);
            var frame = backend.Render(camera, camera, jitter, previousJitter, width, height, 1.0 / 60, i == 0, 0, default);
            var raw = ReconstructionProbe.ReadTexture(device.Device, frame.Color, true);
            var reactive = ReconstructionProbe.ReadTexture(device.Device, frame.Reactive, false);
            RasterProbe.Require(reactive.Max() is > 0 and < 1, "Transparent surface disables all temporal history");
            renderer.Output(frame, target, HdrRenderParameters.Default, 0);
            var taa = ReconstructionProbe.ReadTexture(device.Device, renderer.Reconstruction.Output, true);
            if (i >= 4)
                for (int y = 24; y < 72; y++)
                    for (int x = 40; x < 88; x++)
                    {
                        int k = (y * width + x) * 4;
                        rawChange += Math.Abs(raw[k] - oldRaw![k]);
                        taaChange += Math.Abs(taa[k] - oldTaa![k]);
                    }
            oldRaw = raw;
            oldTaa = taa;
            previousJitter = jitter;
        }
        RasterProbe.Require(rawChange > 1 && taaChange < rawChange * .8, $"Transparent TAA did not reduce jitter instability: raw={rawChange:R}, taa={taaChange:R}");
        material.BaseColor.W = 0;
        backend.SetScene(RasterProbe.Fixture(material) with { Textures = [new("checker", 32, 32, [bytes], false)] });
        var invisible = backend.Render(camera, camera, default, default, width, height, 1.0 / 60, true, 0, default);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device, invisible.Reactive, false).Max() == 0,
            "Zero-alpha geometry overwrote reconstruction guides");
        // 同一 primitive 故意先提交近面、再提交远面；重建信号不能由提交顺序决定。
        material.BaseColor.W = .5f;
        var guideAsset = RasterProbe.Fixture(material);
        var mesh = guideAsset.Meshes[0];
        var near = mesh.Vertices.Select(v => { v.Position.Z = .5f; return v; }).ToArray();
        guideAsset = guideAsset with { Meshes = [mesh with { Vertices = [.. near, .. mesh.Vertices], Indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7] }], Textures = [new("checker", 32, 32, [bytes], false)] };
        backend.SetScene(guideAsset);
        var moving = camera with { Origin = camera.Origin + new Vector3(.1f, 0, 0) };
        var guides = backend.Render(moving, camera, default, default, width, height, 1.0 / 60, false, 0, default);
        var depth = ReconstructionProbe.ReadTexture(device.Device, guides.Depth, false,
            ResourceStates.DepthRead | ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        var motion = ReconstructionProbe.ReadTexture(device.Device, guides.Motion, true);
        int pixel = height / 2 * width + width / 2;
        double expectedDepth = camera.Near * ((double)camera.Far / 2.5 - 1) / (camera.Far - camera.Near);
        double expectedMotion = .1 / (2.5 * Math.Tan(camera.VerticalFov * .5) * width / height) * width * .5;
        RasterProbe.Require(Math.Abs(depth[pixel] - expectedDepth) < 1e-6 && Math.Abs(motion[pixel * 2] - expectedMotion) < .02,
            $"Nearest transparent depth/motion disagree: depth={depth[pixel]:R}, motion={motion[pixel * 2]:R}");
        Console.WriteLine($"PASS: static transparent jitter variation sum raw={rawChange:R}, NativeTAA={taaChange:R}; zero-alpha guides preserved; nearest transparent depth={depth[pixel]:R}, motion={motion[pixel * 2]:R}.");
    }

    private static unsafe string GpuName(DxrDevice device)
    {
        long luid = device.Device.AdapterLuid;
        using var adapter = device.DxgiFactory.EnumAdapterByLuid<Vortice.DXGI.IDXGIAdapter1>(*(Vortice.Luid*)&luid);
        return adapter.Description1.Description;
    }

    private static int Option(string[] arguments, string name, int fallback, int maximum)
    {
        int index = Array.IndexOf(arguments, name);
        if (index < 0) return fallback;
        if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out int value) || value < 1 || value > maximum)
            throw new ArgumentException($"{name} must be between 1 and {maximum}.");
        return value;
    }

    private readonly record struct CardTiming(double Total,double Geometry,double World,double Reflection,double Capture,double Lighting,uint Hits);

    /// <summary>同进程共享同一资产/AS，交错交换先后顺序，降低独立进程与功耗状态对消融的干扰。</summary>
    private static void CompareCards(DxrDevice device,GpuSceneCache cache,RasterBackend enabled,SceneAsset asset,CameraFrame camera,
        int width,int height,float environment,SunLightSettings sun,string[] args,string name)
    {
        RasterProbe.Require(!args.Contains("--no-cards"),"Comparison primary backend must enable Cards");
        using var disabled=new RasterBackend(cache,Option(args,"--shadow-size",1024,2048),256,true,true,4,true,cards:false);
        disabled.Initialize(device);disabled.SetScene(asset);
        const int frames=64,warmup=32;
        var on=new CardTiming[frames];var off=new CardTiming[frames];
        CardTiming Draw(RasterBackend backend,int frame)
        {
            backend.Render(camera,camera,default,default,width,height,1.0/60,frame==0,environment,sun);
            RasterProbe.Require(backend.NrdActive&&backend.NrdDispatchCount>0,"Card comparison requires real NRD");
            return new(backend.GpuMilliseconds,backend.GeometryMilliseconds,backend.WorldCacheMilliseconds,backend.ReflectionMilliseconds,
                backend.CardAtlas?.CaptureMilliseconds??0,backend.CardAtlas?.LightingMilliseconds??0,backend.CardAtlas?.Hits??0);
        }
        for(int i=0;i<frames;i++)
        {
            if((i&1)==0) {on[i]=Draw(enabled,i);off[i]=Draw(disabled,i);}else {off[i]=Draw(disabled,i);on[i]=Draw(enabled,i);}
            Console.WriteLine($"Interleaved {i}: cards={on[i].Total:F3}, off={off[i].Total:F3}; world={on[i].World:F3}/{off[i].World:F3}, reflection={on[i].Reflection:F3}/{off[i].Reflection:F3} ms");
        }
        double Median(IEnumerable<double> values) {var s=values.Order().ToArray();return (s[(s.Length-1)/2]+s[s.Length/2])*.5;}
        object Statistics(CardTiming[] values)=>new
        {
            total=Median(values.Skip(warmup).Select(v=>v.Total)),geometry=Median(values.Skip(warmup).Select(v=>v.Geometry)),
            world=Median(values.Skip(warmup).Select(v=>v.World)),reflection=Median(values.Skip(warmup).Select(v=>v.Reflection)),
            capture=Median(values.Skip(warmup).Select(v=>v.Capture)),lighting=Median(values.Skip(warmup).Select(v=>v.Lighting)),hits=Median(values.Skip(warmup).Select(v=>(double)v.Hits))
        };
        RasterProbe.Require(cache.AccelerationBuildCount==1,"Card comparison duplicated the shared AS");
        var a=ReconstructionProbe.ReadTexture(device.Device,enabled.SceneColor,true);var b=ReconstructionProbe.ReadTexture(device.Device,disabled.SceneColor,true);
        RasterProbe.Require(a.All(float.IsFinite)&&b.All(float.IsFinite),"Card comparison generated non-finite output");
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root.Parent!=null&&!File.Exists(Path.Combine(root.FullName,"DXRDemo","DXRDemo.csproj")))root=root.Parent;
        string directory=Path.Combine(root.FullName,"output","raster-validation");Directory.CreateDirectory(directory);
        var report=new {name,width,height,frames,warmup,samples=frames-warmup,jitter="zero, no public reconstruction/output/present",sun,environment,
            cards=Statistics(on),noCards=Statistics(off),gpuCards=on,gpuNoCards=off,sharedAsBuilds=cache.AccelerationBuildCount};
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true,IncludeFields=true});
        File.WriteAllText(Path.Combine(directory,$"{name}-{width}x{height}-cards-interleaved.json"),json);
        Console.WriteLine($"PASS: interleaved Cards on/off p50={Median(on.Skip(warmup).Select(v=>v.Total)):R}/{Median(off.Skip(warmup).Select(v=>v.Total)):R} ms; shared AS=1; 32 warm +32 samples per mode.");
    }

    private static async Task Preview(DxrDevice device, GpuSceneCache cache, RasterBackend backend, string[] args)
    {
        int width = Option(args, "--width", 1280, 1920), height = Option(args, "--height", 720, 1080);
        bool cacheMotion = args.Contains("--cache-motion");
        int frames = Option(args, "--frames", cacheMotion ? 64 : 8, 64), textureLimit = Option(args, "--texture-size", 1024, 1024);
        RasterProbe.Require(!cacheMotion || frames >= 48, "--cache-motion requires at least 48 frames (32 static warmup + movement)");
        bool screenReflections = !args.Contains("--no-ssr");
        var sun = new SunLightSettings(!args.Contains("--no-sun"), Option(args, "--sun-azimuth", 315, 360), Option(args, "--sun-elevation", 45, 90), 3, 0);
        string name = args.Contains("--bistro") ? "bistro" : args.Contains("--rainy") ? "rainy" : "kitchen";
        SceneDocument document = name == "rainy"
            ? await GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "RainyCorner.glb"), maximumTextureSize: textureLimit)
            : await RtxptAssets.LoadAsync(name == "bistro" ? RtxptScene.Bistro : RtxptScene.Kitchen, maximumTextureSize: textureLimit);
        var asset = document.Scenes[document.DefaultScene];
        if (args.Contains("--no-emissive"))
            asset = asset with { Materials = asset.Materials.Select(m => { m.Emissive = Vector4.Zero; return m; }).ToArray() };
        if (args.Contains("--lamps-only"))
        {
            RasterProbe.Require(name == "kitchen", "--lamps-only is the packaged Kitchen fixture");
            // 固定资产中 46 是发光窗外平面，1/7/47 是橱柜灯、吊灯和微波炉灯；不添加替代灯。
            asset = asset with { Materials = asset.Materials.Select((m,index) => { if (index == 46) m.Emissive = Vector4.Zero; return m; }).ToArray() };
        }
        float environment = args.Contains("--no-environment") ? 0 : 1;
        var distribution = new SceneLightDistribution(asset);
        Console.WriteLine($"Scene: lights={asset.Lights.Length}, emitters={distribution.Emitters.Length}, power={distribution.TotalPower:R}");
        for (int m = 0; m < asset.Materials.Length; m++)
            if (asset.Materials[m].Emissive.LengthSquared() > 0)
                Console.WriteLine($"Emissive material {m}: {asset.Materials[m].Emissive}");
        backend.SetScene(asset);
        var controller = new CameraController();
        controller.FrameScene(asset, width / (float)height);
        var camera = controller.Advance(0, out _, out _);
        if(args.Contains("--raster-cards-compare"))
        {
            CompareCards(device,cache,backend,asset,camera,width,height,environment,sun,args,name);return;
        }
        var initialCamera = camera;
        var previousCamera = camera;
        float cacheSpacing = RasterWorldCache.Grid(asset,camera).W;
        using var renderer = new SceneRenderer();
        renderer.Initialize(device);
        renderer.Reconstruction.Configure(ReconstructionMode.Off, 100, width, height, false, true);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        Vector2 previous = default;
        var timings = new double[frames];
        var stages = new object[frames];
        var workspace=new DirectoryInfo(AppContext.BaseDirectory);
        while(workspace.Parent!=null&&!File.Exists(Path.Combine(workspace.FullName,"DXRDemo","DXRDemo.csproj")))workspace=workspace.Parent;
        string directory=Path.Combine(workspace.Parent==null?AppContext.BaseDirectory:workspace.FullName,"output","raster-validation");
        Directory.CreateDirectory(directory);
        for (int i = 0; i < frames; i++)
        {
            if (cacheMotion && i >= 32)
                camera = initialCamera with { Origin = initialCamera.Origin + initialCamera.Right * (cacheSpacing*.75f*MathF.Sin(2*MathF.PI*(i-31)/(frames-32))) };
            var jitter = renderer.Reconstruction.Jitter(true);
            var frame = backend.Render(camera, previousCamera, jitter, previous, width, height, 1.0 / 60, i == 0, environment,
                sun, screenReflections: screenReflections);
            timings[i] = backend.GpuMilliseconds;
            stages[i] = new { shadow = backend.ShadowMilliseconds, shadowDraws = backend.ShadowDrawCount, shadowsReused = backend.ShadowsReused, geometry = backend.GeometryMilliseconds, direct = backend.DirectMilliseconds, gi = backend.GiMilliseconds, trace = backend.TraceMilliseconds, worldCache=backend.WorldCacheMilliseconds, reflection=backend.ReflectionMilliseconds, denoise = backend.DenoiseMilliseconds, cardCapture = backend.CardAtlas?.CaptureMilliseconds??0, cardLighting = backend.CardAtlas?.LightingMilliseconds??0, cardHits=backend.CardAtlas?.Hits??0, cardResident=backend.CardAtlas?.ResidentPages??0, cardCaptures=backend.CardAtlas?.CaptureCount??0, cardOverflow=backend.CardAtlas?.QueueOverflow??0 };
            Console.WriteLine($"GPU {i}: total={timings[i]:F3}, shadow={backend.ShadowMilliseconds:F3}, geometry={backend.GeometryMilliseconds:F3}, direct={backend.DirectMilliseconds:F3}, GI trace={backend.TraceMilliseconds:F3}, denoise={backend.DenoiseMilliseconds:F3} ms");
            if (args.Contains("--require-nrd"))
                RasterProbe.Require(backend.NrdActive && backend.NrdDispatchCount > 0, $"NRD fixed preview fell back: {backend.NrdStatus}");
            renderer.Output(frame, target, HdrRenderParameters.Default, 0);
            if (backend.CachedGi) RasterProbe.Require(backend.WorldUpdates == i+1, "Camera motion reset world cache history");
            if (cacheMotion && i == 32+(frames-32)/4-1)
                SceneProbe.Save(target,Path.Combine(directory,$"{name}-{width}x{height}-world3-motion-peak.png"));
            previous = jitter;
            previousCamera = camera;
        }
        string stem = $"{name}-{width}x{height}" + (environment == 0 ? "-no-env" : "") + (args.Contains("--lamps-only") ? "-lamps" : "") + (args.Contains("--no-emissive") ? "-no-emissive" : "") + (screenReflections ? "" : "-no-ssr") + (sun.Enabled ? $"-sun-a{sun.Azimuth}-e{sun.Elevation}" : "-no-sun") + (backend.HardwareLightingActive ? $"-rt-{(backend.CachedGi ? "world3" : "pixel")}-d{backend.GiDivisor}" : "-raster") + (backend.CachedGi ? backend.CardAtlas?.Active==true ? "-cards" : "-no-cards" : "") + (backend.NrdActive ? "-nrd" : "-raw") + (cacheMotion ? "-motion" : "");
        var linear = ReconstructionProbe.ReadTexture(device.Device, backend.SceneColor, true);
        RasterProbe.Require(linear.All(float.IsFinite), "Preview/motion generated non-finite radiance");
        var emission = ReconstructionProbe.ReadTexture(device.Device, backend.EmissionBuffer, true);
        double receiverSum = 0; int receiverCount = 0;
        for (int pixel = 0; pixel < width*height; pixel++)
            if (emission[pixel*4+3] >= 0 && emission[pixel*4]+emission[pixel*4+1]+emission[pixel*4+2] == 0)
            { receiverSum += linear[pixel*4]; receiverCount++; }
        double receiverMean = receiverSum / Math.Max(receiverCount,1);
        Console.WriteLine($"Non-emissive receiver red mean={receiverMean:R}, pixels={receiverCount}.");
        if (args.Contains("--lamps-only") && environment == 0 && !sun.Enabled)
            RasterProbe.Require(args.Contains("--no-emissive") ? receiverMean < 1e-6 : receiverMean > .001, "Authored indoor lamps did not change receiver illumination");
        int warmup = cacheMotion ? 32 : Math.Min(backend.CachedGi ? 32 : 4, frames / 2);
        double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * .5 : sorted[sorted.Length / 2]; }
        double p95 = timings.Skip(warmup).Order().ElementAt((int)Math.Ceiling((frames-warmup)*.95)-1);
        Console.WriteLine($"Measured total GPU median={Median(timings.Skip(warmup)):R} ms, p95={p95:R} ms, discarded={warmup}, samples={frames-warmup}, motion={cacheMotion}.");
        SceneProbe.Save(target, Path.Combine(directory, stem + ".png"));
        if(backend.CardAtlas?.Active==true)RasterCardsProbe.SaveAtlas(device,backend.CardAtlas,Path.Combine(directory,stem));
        var report = new { stack=backend.HardwareStackDiagnostic, name, width, height, frames, textureLimit, shadows = $"{Option(args, "--shadow-size", 1024, 2048)}/256", environment, exposure = 0, sun, receiverMean, receiverCount, warmup, samples = frames - warmup, medianGpuMilliseconds = Median(timings.Skip(warmup)), p95GpuMilliseconds = p95, cacheMotion, cacheSpacing, initialCamera, gpuMilliseconds = timings, stages,
            cards=backend.CardAtlas?.Active??false, cardPages=backend.CardAtlas?.ResidentPages??0, cardTotalHits=backend.CardAtlas?.TotalHits??0, cardCaptureBudget=RasterCardAtlas.CaptureBudget, cardLightingTexels=RasterCardAtlas.Size*RasterCardAtlas.Size, giDivisor = backend.GiDivisor, gi = backend.HardwareLightingActive ? backend.CachedGi ? "3-level scrolling world irradiance cache" : "reference pixel HWRT" : "not implemented", worldUpdates = backend.WorldUpdates, worldLevels = backend.CachedGi ? RasterWorldCache.Levels : 0, worldProbeCount = backend.CachedGi ? RasterWorldCache.Count : 0, worldProbeRays = backend.CachedGi ? (RasterWorldCache.Rays+1)*RasterWorldCache.Budget : 0, pixelDiffuseRays = backend.CachedGi ? 0 : (width+backend.GiDivisor-1)/backend.GiDivisor*((height+backend.GiDivisor-1)/backend.GiDivisor), nrd = backend.NrdActive, nrdDispatches = backend.NrdDispatchCount, nrdStatus = backend.NrdStatus, reflections = backend.HardwareLightingActive ? "RT hit lighting" : screenReflections ? "SSR + prefiltered environment" : "prefiltered environment only", camera, hardware = GpuName(device) };
        File.WriteAllText(Path.Combine(directory, stem + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine($"PASS: {name} headless preview: {width}x{height}, {frames} frames, hardware lighting={backend.HardwareLightingActive}, NRD={backend.NrdActive}, dispatches={backend.NrdDispatchCount}, textures {textureLimit}; {directory}");
    }
}
