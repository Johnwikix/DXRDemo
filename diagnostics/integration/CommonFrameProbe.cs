using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using DXRDemo.Shaders.Raster;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

internal static class CommonFrameProbe
{
    internal static void Run()
    {
        var cleanup = new ResourceCleanup();
        var failure = new InvalidOperationException("cleanup failure fixture");
        int cleanupCount = 0;
        cleanup.Run(() => throw failure);
        cleanup.Run(() => cleanupCount++);
        bool reported = false;
        try
        {
            cleanup.Finish();
        }
        catch (InvalidOperationException error) { reported = ReferenceEquals(error, failure); }
        RasterProbe.Require(reported && cleanupCount == 1, "Cleanup skipped a remaining owner or changed the original error");
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault(false);
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        debug.SetBreakOnSeverity(MessageSeverity.Error, false);
        debug.SetBreakOnSeverity(MessageSeverity.Corruption, false);
        TaaAndDisplay(device);
        PipelineSwitch(device);
        Warp(device);
        RasterProbe.Verify(debug);
        Console.WriteLine("PASS: shared LinearFrame, NativeTAA reprojection/reactive/disocclusion/reset, SDR/HDR encoding, shared pipeline services/SDKs, WARP raster without AS; D3D12 Error/Corruption=0.");
    }
    private static GpuTexture<T> Texture<T>(DxrDevice device, int width, int height, Format format) where T : unmanaged
        => new(device, DxrResources.CreateUnorderedAccessTexture2D(device.Device, (uint)width, (uint)height, format, ResourceStates.UnorderedAccess), width, height, format);
    private static void TaaAndDisplay(DxrDevice device)
    {
        const int width = 16, height = 16;
        int center = (8 * width + 8) * 4;
        using var color = device.CreateTexture2D<Vector4>(width, height);
        using var depth = Texture<float>(device, width, height, Format.R32_Float);
        using var motion = Texture<Vector2>(device, width, height, Format.R32G32_Float);
        using var reactive = Texture<byte>(device, width, height, Format.R8_UNorm);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        using var renderer = new SceneRenderer();
        renderer.Initialize(device);
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        var pixels = new Vector4[height, width];
        var depths = new float[height, width];
        var vectors = new Vector2[height, width];
        var response = new byte[height, width];
        void Upload(int phase, float z, byte reaction)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    pixels[y, x] = new(((x + y + phase) & 1) == 0 ? 1 : 0);
                    depths[y, x] = camera.Near * (camera.Far / z - 1) / (camera.Far - camera.Near);
                    response[y, x] = reaction;
                }
            color.CopyFrom(pixels);
            depth.CopyFrom(depths);
            motion.CopyFrom(vectors);
            reactive.CopyFrom(response);
        }
        LinearFrame Frame(bool reset, bool taa = true) => new(color.Resource, depth.Resource, motion.Resource, reactive.Resource, width, height, camera, default, default, 1.0 / 60, reset, taa);
        void Draw(bool reset, bool taa = true)
        {
            device.CommandAllocator.Reset();
            device.CommandList.Reset(device.CommandAllocator);
            foreach (var resource in new[] { color.Resource, depth.Resource, motion.Resource, reactive.Resource })
                device.CommandList.ResourceBarrierTransition(resource, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            device.CommandList.Close();
            device.CommandQueue.ExecuteCommandList(device.CommandList);
            device.SignalAndWait();
            renderer.Output(Frame(reset, taa), target, HdrRenderParameters.Default, 0);
            device.CommandAllocator.Reset();
            device.CommandList.Reset(device.CommandAllocator);
            foreach (var resource in new[] { color.Resource, depth.Resource, motion.Resource, reactive.Resource })
                device.CommandList.ResourceBarrierTransition(resource, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            device.CommandList.Close();
            device.CommandQueue.ExecuteCommandList(device.CommandList);
            device.SignalAndWait();
        }
        renderer.Reconstruction.Configure(ReconstructionMode.Off, 100, width, height, false, true);
        Upload(0, 1, 0);
        Draw(true);
        Upload(1, 1, 0);
        Draw(false);
        float temporal = ReconstructionProbe.ReadTexture(device.Device, renderer.Reconstruction.Output, true, ResourceStates.UnorderedAccess)[center];
        RasterProbe.Require(Math.Abs(temporal - .9) < .001, $"Native TAA history weight: {temporal}");
        Upload(1, 1, 255);
        Draw(false);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device, renderer.Reconstruction.Output, true, ResourceStates.UnorderedAccess)[center] == 0, "Reactive pixel retained history");
        Upload(0, 2, 0);
        Draw(false);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device, renderer.Reconstruction.Output, true, ResourceStates.UnorderedAccess)[center] == 1, "Disoccluded pixel retained history");
        Upload(1, 2, 0);
        Draw(true);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device, renderer.Reconstruction.Output, true, ResourceStates.UnorderedAccess)[center] == 0, "Camera cut retained history");
        renderer.Reconstruction.Configure(ReconstructionMode.Off, 100, width, height, false, false);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y, x] = new(.25f, .5f, .75f, 1);
        color.CopyFrom(pixels);
        device.CommandAllocator.Reset();
        device.CommandList.Reset(device.CommandAllocator);
        color.Resource.Name = "LinearFrame encoding input";
        foreach (var resource in new[] { color.Resource, depth.Resource, motion.Resource, reactive.Resource })
            device.CommandList.ResourceBarrierTransition(resource, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        device.CommandList.Close();
        device.CommandQueue.ExecuteCommandList(device.CommandList);
        device.SignalAndWait();
        foreach (bool hdr in new[] { false, true })
        {
            renderer.Output(Frame(true, false), target, new HdrRenderParameters(hdr, 203, 1000), 1);
            var actual = target.ToArray()[8, 8];
            float[] value = [.5f, 1, 1.5f];
            if (hdr)
            {
                for (int i = 0; i < 3; i++)
                    value[i] /= 1 + value[i] / (1000f / 203);
                value = [Vector3.Dot(new(value[0],value[1],value[2]),new(.627404f,.329283f,.043313f)),
                       Vector3.Dot(new(value[0],value[1],value[2]),new(.069097f,.919540f,.011362f)),
                       Vector3.Dot(new(value[0],value[1],value[2]),new(.016391f,.088013f,.895595f))];
                for (int i = 0; i < 3; i++)
                {
                    double p = Math.Pow(Math.Min(value[i] * 203, 1000) / 10000, .1593017578125);
                    value[i] = (float)Math.Pow((.8359375 + 18.8515625 * p) / (1 + 18.6875 * p), 78.84375);
                }
            }
            else
                for (int i = 0; i < 3; i++)
                {
                    float c = value[i];
                    value[i] = (float)Math.Pow(Math.Clamp(c * (2.51 * c + .03) / (c * (2.43 * c + .59) + .14), 0, 1), 1 / 2.2);
                }
            RasterProbe.Require(Math.Abs(actual.R / 65535f - value[0]) < .0001 && Math.Abs(actual.G / 65535f - value[1]) < .0001 && Math.Abs(actual.B / 65535f - value[2]) < .0001, $"Shared {(hdr ? "HDR10" : "SDR")} encoding/exposure differs from CPU");
        }
        Console.WriteLine($"PASS: NativeTAA weight={temporal}, reactive/disocclusion/cut; SDR/HDR10 CPU reference max tolerance=0.0001.");
    }
    private static void PipelineSwitch(DxrDevice device)
    {
        var material = SceneMaterial.Default;
        material.Emissive = new(.25f, .5f, .75f, 0);
        var asset = RasterProbe.Fixture(material);
        var document = new SceneDocument("shared", [asset], 0);
        var invalid = document with
        {
            Scenes = [asset with { Instances = [] }]
        };
        using var session = new SceneSession((_, _) => Task.FromResult(document), (path, _) => Task.FromResult(path == "invalid" ? invalid : document));
        session.ReconstructionMode = ReconstructionMode.Off;
        session.RenderScalePercent = 100;
        using var cache = new GpuSceneCache();
        using var renderer = new SceneRenderer();
        using var path = new RayTracePass(session, cache, renderer) { DenoiserMode = RayTraceDenoiserMode.None, MaxBounces = 2 };
        using var raster = new RasterPass(session, cache, renderer) { UseHardwareLighting = false };
        path.Initialize(device, default);
        raster.Initialize(device, default);
        using var target = device.CreateTexture2D<Rgba64>(320, 180);
        raster.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
        RasterProbe.Require(cache.UploadCount == 1 && cache.AccelerationBuildCount == 0, "Raster pass uploaded duplicate scene/built AS");
        CameraFrame camera = session.Camera.Advance(0, out _, out _);
        path.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
        RasterProbe.Require(cache.UploadCount == 1 && cache.AccelerationBuildCount == 1 && session.Camera.Advance(0, out _, out _) == camera, "Switch did not preserve shared GPU scene/camera");
        raster.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
        RasterProbe.Require(cache.UploadCount == 1 && session.Snapshot.Asset == asset, "Switch back duplicated upload or scene");
        foreach (var mode in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS, ReconstructionMode.Dlss })
        {
            if (!renderer.Reconstruction.Status.Supports(mode))
                continue;
            session.ReconstructionMode = mode;
            session.RenderScalePercent = 67;
            for (int i = 0; i < 3; i++)
                raster.TryExecute(target, 320, 180, TimeSpan.FromSeconds((i + 1) / 60.0), HdrRenderParameters.Default);
            var status = session.ReconstructionStatus ?? throw new InvalidOperationException("Missing reconstruction status");
            RasterProbe.Require(status.Active == mode, $"Raster {mode} fell back: {status.Message}");
            RasterProbe.Require(target.ToArray().Cast<Rgba64>().Any(v => v.R > 1000), $"Raster {mode} empty output");
            Console.WriteLine($"PASS: raster actual {mode}, input {status.InputWidth}x{status.InputHeight}, shared SR bridge.");
        }
        if (renderer.Reconstruction.Status.Active != ReconstructionMode.Off)
        {
            var requestedMode = session.ReconstructionMode;
            renderer.Reconstruction.FallbackToNative("SDK failure policy fixture");
            RasterProbe.Require(raster.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default), "Native fallback did not recover raster output");
            RasterProbe.Require(session.ReconstructionStatus is { Active: ReconstructionMode.Off, InputWidth: 320, InputHeight: 180 } &&
                session.ReconstructionStatus.Requested == requestedMode, "Fallback retried failed SDK or lost the original request");
        }
        session.ReconstructionMode = ReconstructionMode.DlssRayReconstruction;
        raster.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
        RasterProbe.Require(session.ReconstructionStatus?.Active != ReconstructionMode.DlssRayReconstruction, "Raster fabricated RR guides");
        if (renderer.Reconstruction.Status.Supports(ReconstructionMode.DlssRayReconstruction))
        {
            using var rrTarget = device.CreateTexture2D<Rgba64>(640, 400);
            for (int i = 0; i < 3; i++)
                RasterProbe.Require(path.TryExecute(rrTarget, 640, 400, TimeSpan.FromSeconds((i + 1) / 60.0), HdrRenderParameters.Default), "Shared RR execution failed");
            RasterProbe.Require(session.ReconstructionStatus is { Active: ReconstructionMode.DlssRayReconstruction, NrdActive: false, OptixActive: false }, "Path RR fell back or executed another denoiser");
            Console.WriteLine("PASS: path actual DLSSD on shared RR endpoint, NRD/OptiX bypassed.");
        }
        var before = session.Snapshot;
        var failedImport = session.ImportAsync("invalid");
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!failedImport.IsCompleted && timeout.Elapsed.TotalSeconds < 15)
            raster.TryExecute(target, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
        RasterProbe.Require(failedImport.IsFaulted && ReferenceEquals(session.Snapshot, before), "Raster candidate failure changed active snapshot");
        try
        {
            failedImport.GetAwaiter().GetResult();
        }
        catch (InvalidDataException) { }
        using var resized = device.CreateTexture2D<Rgba64>(192, 128);
        raster.TryExecute(resized, 192, 128, TimeSpan.Zero, new HdrRenderParameters(true, 203, 1000));
        Console.WriteLine("PASS: raster/path/raster borrowed scene + camera + services, RR endpoint restricted, resize/HDR.");
    }
    private static void Warp(DxrDevice hardware)
    {
        using var adapter = hardware.DxgiFactory.EnumWarpAdapter<IDXGIAdapter>();
        using var native = D3D12.D3D12CreateDevice<ID3D12Device5>(adapter, Vortice.Direct3D.FeatureLevel.Level_11_0);
        using var device = new DxrDevice();
        device.Initialize(native, requireRayTracing: false);
        using var debug = native.QueryInterface<ID3D12InfoQueue>();
        RasterProbe.Require(native.AdapterLuid != hardware.Device.AdapterLuid, "WARP fixture reused hardware adapter");
        using var cache = new GpuSceneCache();
        using var backend = new RasterBackend(cache);
        backend.Initialize(device);
        var material = SceneMaterial.Default;
        material.Emissive = new(.25f, .5f, .75f, 0);
        backend.SetScene(RasterProbe.Fixture(material));
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        var frame = backend.Render(camera, camera, default, default, 32, 24, 1.0 / 60, true, 0, default);
        var color = ReconstructionProbe.ReadTexture(native, frame.Color, true);
        RasterProbe.Require(color[(12 * 32 + 16) * 4] > .24 && cache.AccelerationBuildCount == 0, "WARP raster failed or built AS");
        RasterProbe.Verify(debug);
        Console.WriteLine($"PASS: same raster backend on WARP; AS builds=0, reported DXR capability={device.SupportsRayTracing} (no physical no-DXR hardware claim).");
    }
}
