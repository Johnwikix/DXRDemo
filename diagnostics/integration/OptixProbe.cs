using System.Numerics;
using System.Runtime.InteropServices;
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

internal static unsafe class OptixProbe
{
    internal static void Run()
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native = device.Device.QueryInterface<ID3D12Device5>();
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var sr = new SuperResolutionRenderer(device);
        Require(sr.Status.OptixAvailable, "OptiX must actually be available on this test GPU: " + Marshal.PtrToStringUTF8(NativeOptix.Diagnostic()));
        Verify(device, native, sr, 128, 96);
        Verify(device, native, sr, 80, 64); // Recreate all external-memory/denoiser state at another size.
        VerifyGlass(device, native);
        sr.Configure(ReconstructionMode.Off, 100, 80, 64, false, true);
        Require(!sr.Status.OptixActive, "Switching off OptiX retained its context");
        sr.Configure(ReconstructionMode.Off, 100, 80, 64, true, true);
        Require(sr.Status.NrdActive && !sr.Status.OptixActive, "NRD/OptiX mode transition failed");
        string? previous = Environment.GetEnvironmentVariable("DXR_DISABLE_OPTIX");
        try
        {
            Environment.SetEnvironmentVariable("DXR_DISABLE_OPTIX", "1");
            using var fallback = new SuperResolutionRenderer(device);
            Verify(device, native, fallback, 64, 64, unavailable: true);
        }
        finally { Environment.SetEnvironmentVariable("DXR_DISABLE_OPTIX", previous); }
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        Console.WriteLine("PASS actual OptiX AI dispatch, noise reduction, isolated raw mean, thin-glass transmission, camera/jitter resets, resize, mode transition and unavailable fallback.");
    }

    private static void VerifyGlass(DxrDevice device, ID3D12Device5 native)
    {
        const int width = 256, height = 160;
        var fixture = GlassReconstructionProbe.Fixture();
        var materials = (SceneMaterial[])fixture.Materials.Clone();
        materials[2].Factors.Y = 0; materials[2].Transmission = new(1, 1, 0, 0);
        var glass = fixture with { Materials = materials, Instances = fixture.Instances[..^1] };
        using var backend = new DxrSceneBackend { CausticsEnabled = false }; backend.Initialize(device);
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normal = device.CreateTexture2D<Rgba32>(width, height);
        using var surface = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        using var sr = new SuperResolutionRenderer(device);
        var view = CameraFrame.Look(new(.3f, 0, 3), Vector3.Normalize(new(-.12f, 0, -1)), .7f, .01f, 100);
        backend.SetScene(glass with { Instances = fixture.Instances[..^2] });
        backend.Trace(view, 0, 1, 8, default, 0, raw, normal, surface, guide, signals);
        var reference = raw.ToArray();
        backend.SetScene(glass);
        backend.Trace(view, 0, 16, 8, default, 0, raw, normal, surface, guide, signals);
        var withoutGuides = raw.ToArray();
        backend.Trace(view, 0, 16, 8, default, 0, raw, normal, surface, guide, signals, optixGuides: true);
        Require(withoutGuides.Cast<Float4>().SequenceEqual(raw.ToArray().Cast<Float4>()), "OptiX guide tracing changed physical glass radiance");
        sr.Configure(ReconstructionMode.Off, 100, width, height, false, true, true);
        for (int frame = 0; frame < 8; frame++)
        {
            backend.Trace(view, frame, 16, 8, default, 0, raw, normal, surface, guide, signals, optixGuides: true);
            Require(sr.Execute(raw, normal, surface, target, default, 3, default, RayTraceDenoiserMode.Optix,
                HdrRenderParameters.Default, 1.0 / 60, guide, view, signals), sr.Status.Message);
        }
        float[] history = ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false);
        float[] denoised = ReconstructionProbe.ReadTexture(native, sr.DenoisedColor, true);
        double rawError = 0, aiError = 0, transmitted = 0, expected = 0; int count = 0;
        for (int y = 24; y < height - 24; y++) for (int x = 24; x < width - 24; x++)
        {
            int pixel = (y * width + x) * 4; double truth = reference[y, x].X;
            rawError += Math.Pow(history[pixel] - truth, 2); aiError += Math.Pow(denoised[pixel] - truth, 2);
            transmitted += denoised[pixel]; expected += truth; count++;
        }
        rawError = Math.Sqrt(rawError / count); aiError = Math.Sqrt(aiError / count);
        Console.WriteLine($"Oblique thin glass vs glass-free reference: raw RMSE={rawError:F6}, OptiX RMSE={aiError:F6}, transmission={transmitted / expected:F4}");
        Require(rawError < .05 && aiError < .12 && transmitted / expected > .85, "Thin glass lost or misdirected the visible background");
        Directory.CreateDirectory("output/optix"); SceneProbe.Save(target, "output/optix/thin-glass.png");
    }

    private static void Verify(DxrDevice device, ID3D12Device5 native, SuperResolutionRenderer sr, int width, int height, bool unavailable = false)
    {
        sr.Configure(ReconstructionMode.Off, 100, width, height, false, true, true);
        Require(sr.Status.OptixActive != unavailable && !sr.Status.NrdActive, sr.Status.Message);
        if (unavailable) Require(sr.Status.Message.Contains("时间累积"), "Unavailable OptiX was not reported as Temporal fallback");
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normals = device.CreateTexture2D<Rgba32>(width, height);
        using var surfaces = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, 2 * MathF.Atan(.5f), .01f, 1000);
        var geometry = new Float4[height, width]; var albedo = new Float4[height, width]; var normalGuide = new Float4[height, width];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            float wx = (2 * (x + .5f) / width - 1) * width / (float)height * 1.5f;
            float wy = (1 - 2 * (y + .5f) / height) * 1.5f;
            geometry[y, x] = new(wx, wy, 0, MathF.Sqrt(wx * wx + wy * wy + 9));
            albedo[y, x] = x < width / 2 ? new(.18f, .32f, .55f, 1) : new(.65f, .25f, .12f, 1);
            normalGuide[y, x] = new(0, 0, 1, .7f);
        }
        surfaces.CopyFrom(geometry); guide.CopyFrom(normalGuide); signals.Albedo.CopyFrom(albedo);
        normals.CopyFrom(new Rgba32[height, width]);
        double[] sums = new double[width * height * 3];
        var samples = new Float4[height, width]; var random = new Random(0x4057);
        const int frames = 8;
        for (int frame = 0; frame < frames; frame++)
        {
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                float noise = (float)-Math.Log(1 - random.NextDouble()); Float4 a = albedo[y, x];
                samples[y, x] = new(a.X * noise, a.Y * noise, a.Z * noise, 1);
                int i = (y * width + x) * 3;
                sums[i] += samples[y, x].X; sums[i + 1] += samples[y, x].Y; sums[i + 2] += samples[y, x].Z;
            }
            raw.CopyFrom(samples);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.Optix,
                HdrRenderParameters.Default, 1.0 / 60, guide, camera, signals), sr.Status.Message);
            Require(unavailable || sr.Status.OptixActive && sr.OptixDispatchCount == frame + 1, sr.Status.Message);
        }
        float[] history = ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false);
        float[] output = ReconstructionProbe.ReadTexture(native, sr.DenoisedColor, true);
        double meanError = 0, rawError = 0, outputError = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 3; c++)
        {
            int pixel = y * width + x; Float4 a = albedo[y, x]; double truth = c == 0 ? a.X : c == 1 ? a.Y : a.Z;
            double delta = history[pixel * 4 + c] - sums[pixel * 3 + c] / frames; meanError += delta * delta;
            delta = history[pixel * 4 + c] - truth; rawError += delta * delta;
            delta = output[pixel * 4 + c] - truth; outputError += delta * delta;
        }
        double count = width * height * 3;
        meanError = Math.Sqrt(meanError / count); rawError = Math.Sqrt(rawError / count); outputError = Math.Sqrt(outputError / count);
        Console.WriteLine($"OptiX {width}x{height} unavailable={unavailable}: raw-mean RMSE={meanError:E3}, raw/reference={rawError:F6}, AI/reference={outputError:F6}, inference={sr.OptixGpuMilliseconds:F3} ms");
        Require(meanError < 1e-6, "AI output contaminated the original Monte Carlo mean");
        Require(output.All(float.IsFinite), "Non-finite OptiX output");
        if (unavailable) return;
        Require(outputError < rawError * .65, "OptiX did not reduce the known fixture's noise");

        // Moving a camera or changing the raster jitter must start a fresh progressive image.
        raw.CopyFrom(albedo);
        foreach (Float2 jitter in new[] { default(Float2), new Float2(.5f, 0) })
        {
            camera = camera with { Origin = camera.Origin + new Vector3(.01f, 0, 0) };
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, jitter, RayTraceDenoiserMode.Optix,
                HdrRenderParameters.Default, 1.0 / 60, guide, camera, signals), sr.Status.Message);
            uint[] counts = TemporalConvergenceProbe.ReadSampleCounts(native, sr.TemporalHistorySampleCounts);
            Require(counts.Where((_, i) => i % 2 == 0).All(n => n == 1), "OptiX kept stale progressive samples after camera movement");
            Require(ReconstructionProbe.ReadTexture(native, sr.Motion, true).Any(v => Math.Abs(v) > .01f),
                "Resetting OptiX accumulation suppressed the moving-camera SR motion vectors");
        }
        Float2 changedJitter = new(-.25f, .25f);
        Require(sr.Execute(raw, normals, surfaces, target, default, 3, changedJitter, RayTraceDenoiserMode.Optix,
            HdrRenderParameters.Default, 1.0 / 60, guide, camera, signals), sr.Status.Message);
        Require(TemporalConvergenceProbe.ReadSampleCounts(native, sr.TemporalHistorySampleCounts).Where((_, i) => i % 2 == 0).All(n => n == 1),
            "OptiX kept stale samples after only the jitter changed");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
