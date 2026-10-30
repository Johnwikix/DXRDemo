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
        bool preview = args.Contains("--raster-preview"), hardwareTest = args.Contains("--raster-hardware");
        bool hardware = hardwareTest || (preview && !args.Contains("--no-hardware-gi"));
        int shadowSize = preview ? Option(args, "--shadow-size", 1024, 2048) : 256;
        using var backend = new RasterBackend(cache, shadowSize, preview ? 256 : 128, hardware);
        Console.WriteLine($"Adapter: {GpuName(device)}; hardware lighting requested={hardware}.");
        backend.Initialize(device);
        if (args.Contains("--raster-preview"))
            await Preview(device, backend, args);
        else if (hardwareTest)
            RasterHardwareProbe.Run(device, cache, backend);
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

    private static async Task Preview(DxrDevice device, RasterBackend backend, string[] args)
    {
        int width = Option(args, "--width", 1280, 1920), height = Option(args, "--height", 720, 1080);
        int frames = Option(args, "--frames", 8, 16), textureLimit = Option(args, "--texture-size", 1024, 1024);
        bool screenReflections = !args.Contains("--no-ssr");
        var sun = new SunLightSettings(!args.Contains("--no-sun"), Option(args, "--sun-azimuth", 315, 360), Option(args, "--sun-elevation", 45, 90), 3, 0);
        string name = args.Contains("--bistro") ? "bistro" : args.Contains("--rainy") ? "rainy" : "kitchen";
        SceneDocument document = name == "rainy"
            ? await GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "RainyCorner.glb"), maximumTextureSize: textureLimit)
            : await RtxptAssets.LoadAsync(name == "bistro" ? RtxptScene.Bistro : RtxptScene.Kitchen, maximumTextureSize: textureLimit);
        var asset = document.Scenes[document.DefaultScene];
        backend.SetScene(asset);
        var controller = new CameraController();
        controller.FrameScene(asset, width / (float)height);
        var camera = controller.Advance(0, out _, out _);
        using var renderer = new SceneRenderer();
        renderer.Initialize(device);
        renderer.Reconstruction.Configure(ReconstructionMode.Off, 100, width, height, false, true);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        Vector2 previous = default;
        var timings = new double[frames];
        string directory = Path.GetFullPath("../../../../../output/raster-validation");
        Directory.CreateDirectory(directory);
        string stem = $"{name}-{width}x{height}" + (screenReflections ? "" : "-no-ssr") + (sun.Enabled ? $"-sun-a{sun.Azimuth}-e{sun.Elevation}" : "-no-sun") + (backend.HardwareLightingActive ? "-rt" : "-raster");
        for (int i = 0; i < frames; i++)
        {
            var jitter = renderer.Reconstruction.Jitter(true);
            var frame = backend.Render(camera, camera, jitter, previous, width, height, 1.0 / 60, i == 0, 1,
                sun, screenReflections: screenReflections);
            timings[i] = backend.GpuMilliseconds;
            renderer.Output(frame, target, HdrRenderParameters.Default, 0);
            previous = jitter;
        }
        SceneProbe.Save(target, Path.Combine(directory, stem + ".png"));
        var report = new { name, width, height, frames, textureLimit, shadows = $"{Option(args, "--shadow-size", 1024, 2048)}/256", environment = 1, exposure = 0, sun, gpuMilliseconds = timings,
            gi = backend.HardwareLightingActive ? "one bounce HWRT" : "not implemented", reflections = backend.HardwareLightingActive ? "RT hit lighting" : screenReflections ? "SSR + prefiltered environment" : "prefiltered environment only", camera, hardware = GpuName(device) };
        File.WriteAllText(Path.Combine(directory, stem + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine($"PASS: {name} headless preview: {width}x{height}, {frames} frames, hardware lighting={backend.HardwareLightingActive}, textures {textureLimit}; {directory}");
    }
}
