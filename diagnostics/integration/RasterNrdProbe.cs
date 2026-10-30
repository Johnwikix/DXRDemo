using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using DXRDemo.SuperResolution;
using DXRDemo.Hdr;

/// <summary>在同随机样本下比较光栅 RELAX 与双边过滤基线，并核对 opaque guides/历史重置。</summary>
internal static class RasterNrdProbe
{
    internal static void Run(DxrDevice device, GpuSceneCache cache, RasterBackend backend, bool missing)
    {
        const int width = 64, height = 48, frames = 32;
        var material = SceneMaterial.Default;
        material.BaseColor = new(.5f, .5f, .5f, 1);
        material.Factors = new(0, .65f, 1, 1);
        var environment = new Vector4[16 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
                environment[y * 16 + x] = x < 4 && y is >= 2 and <= 5 ? new(6, 3, 1, 0) : new(.03f, .03f, .03f, 0);
        var asset = RasterProbe.Fixture(material) with { EnvironmentMap = new(16, 8, environment) };
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        backend.SetScene(asset);
        var first = backend.Render(camera, camera, default, default, width, height, 1.0 / 60, true, 1, default);
        RasterProbe.Require(backend.HardwareLightingActive, "NRD probe requires real hardware GI");
        if (missing)
        {
            RasterProbe.Require(!backend.NrdActive && backend.NrdStatus.Length > 0, "Missing NRD did not report fallback");
            using var renderer = new SceneRenderer();
            renderer.Initialize(device);
            renderer.Reconstruction.Configure(ReconstructionMode.Fsr, 100, width, height, false);
            using var target = device.CreateTexture2D<Rgba64>(width, height);
            renderer.Output(first, target, HdrRenderParameters.Default, 0);
            RasterProbe.Require(renderer.Reconstruction.Status.Active == ReconstructionMode.Fsr &&
                ReconstructionProbe.ReadTexture(device.Device, first.Color, true).All(float.IsFinite), "Missing NRD disabled FSR or produced invalid GI");
            Console.WriteLine($"PASS: NRD missing fallback + real FSR; {backend.NrdStatus}");
            return;
        }
        RasterProbe.Require(backend.NrdActive && backend.NrdDispatchCount > 0, $"NRD did not execute: {backend.NrdStatus}");
        using var baseline = new RasterBackend(cache, 256, 128, hardwareLighting: true, denoiseHardware: false);
        baseline.Initialize(device);
        baseline.SetScene(asset);
        double baselineSquare = 0, denoisedSquare = 0, baselineMean = 0, denoisedMean = 0;
        int count = 0;
        float[]? oldBaseline = null, oldDenoised = null;
        Vector2 previousJitter = default;
        for (int i = 0; i < frames; i++)
        {
            Vector2 jitter = new((i & 1) == 0 ? .25f : -.25f, (i & 2) == 0 ? -.25f : .25f);
            var raw = baseline.Render(camera, camera, jitter, previousJitter, width, height, 1.0 / 60, i == 0, 1, default);
            var denoised = backend.Render(camera, camera, jitter, previousJitter, width, height, 1.0 / 60, i == 0, 1, default);
            var a = ReconstructionProbe.ReadTexture(device.Device, raw.Color, true);
            var b = ReconstructionProbe.ReadTexture(device.Device, denoised.Color, true);
            RasterProbe.Require(a.All(float.IsFinite) && b.All(float.IsFinite), "NRD/baseline produced non-finite values");
            if (i >= 16)
                for (int y = 15; y < 33; y++)
                    for (int x = 20; x < 44; x++)
                    {
                        int k = (y * width + x) * 4;
                        baselineSquare += Math.Pow(a[k] - oldBaseline![k], 2);
                        denoisedSquare += Math.Pow(b[k] - oldDenoised![k], 2);
                        baselineMean += a[k]; denoisedMean += b[k]; count++;
                    }
            oldBaseline = a; oldDenoised = b; previousJitter = jitter;
        }
        double baselineRms = Math.Sqrt(baselineSquare / count), denoisedRms = Math.Sqrt(denoisedSquare / count);
        baselineMean /= count; denoisedMean /= count;
        RasterProbe.Require(baselineRms > .001 && denoisedRms < baselineRms * .6,
            $"NRD did not suppress stochastic GI: bilateral RMS={baselineRms:R}, NRD={denoisedRms:R}");
        RasterProbe.Require(Math.Abs(denoisedMean / baselineMean - 1) < .15,
            $"NRD changed mean energy: bilateral={baselineMean:R}, NRD={denoisedMean:R}");
        var viewZ = ReconstructionProbe.ReadTexture(device.Device, backend.NrdViewZ!, false);
        var motion = ReconstructionProbe.ReadTexture(device.Device, backend.NrdMotion!, true);
        RasterProbe.Require(Math.Abs(viewZ[height / 2 * width + width / 2] + 3) < .001 && motion.Max(Math.Abs) < .002,
            "NRD viewZ sign or static unjittered motion is wrong");
        RasterProbe.Require(backend.NrdMaxAccumulatedFrames == NativeNrd.DefaultMaxAccumulatedFrames, "Stationary NRD history not restored");
        var moving = camera with { Origin = camera.Origin + new Vector3(.1f, 0, 0) };
        backend.Render(moving, camera, default, previousJitter, width, height, 1.0 / 60, false, 1, default);
        motion = ReconstructionProbe.ReadTexture(device.Device, backend.NrdMotion!, true);
        float expectedMotion = .1f / (3 * MathF.Tan(camera.VerticalFov * .5f) * width / height) * width * .5f;
        float error = Math.Abs(motion[(height / 2 * width + width / 2) * 2] - expectedMotion);
        RasterProbe.Require(error < .002 && backend.NrdMaxAccumulatedFrames == NativeNrd.MotionMaxAccumulatedFrames,
            $"NRD camera motion/history contract: error={error:R}, frames={backend.NrdMaxAccumulatedFrames}");
        var reset = backend.Render(moving, moving, default, default, width, height, 1.0 / 60, true, 0, default);
        RasterProbe.Require(ReconstructionProbe.ReadTexture(device.Device, reset.Color, true).Where((_, i) => i % 4 != 3).Max(Math.Abs) < 1e-5,
            "NRD retained old lighting after reset");
        backend.Render(camera, camera, default, default, 32, 24, 1.0 / 60, true, 1, default);
        RasterProbe.Require(backend.NrdActive && backend.NrdDispatchCount > 0, "NRD resize did not recreate a real context");
        Console.WriteLine($"PASS: raster RELAX dispatches={backend.NrdDispatchCount}; RMS bilateral={baselineRms:R}, NRD={denoisedRms:R}; mean bilateral={baselineMean:R}, NRD={denoisedMean:R}; motion error={error:R}; static jitter, motion/8-frame history, reset and resize.");
    }
}
