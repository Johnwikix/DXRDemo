using System.Numerics;
using System.Text.Json;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Float4 = System.Numerics.Vector4;

/// <summary>Exercises the real NRD adapter with a noise-free world-anchored signal and a deterministic camera path.</summary>
internal static unsafe class NrdMotionProbe
{
    internal static void Run(string[] args)
    {
        int Option(string name, int fallback, int maximum)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < 1 || value > maximum)
                throw new ArgumentException($"{name} must be between 1 and {maximum}.");
            return value;
        }
        int width = Option("--width", 3840, 3840), height = Option("--height", 2160, 2160);
        int frameCount = Option("--frames", 32, 64);
        if (frameCount < 25) throw new ArgumentException("--frames must be at least 25 to cover stationary, translation and FOV reprojection.");
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native = device.Device.QueryInterface<ID3D12Device5>();
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var sr = new SuperResolutionRenderer(device);
        sr.Configure(ReconstructionMode.Off, 100, width, height, true);
        Require(sr.Status.NrdActive, sr.Status.Message);
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normals = device.CreateTexture2D<Rgba32>(width, height);
        using var surfaces = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var target = device.CreateTexture2D<Rgba64>(width, height);
        var radiance = new Float4[height, width];
        var positions = new Float4[height, width];
        var normalGuide = new Float4[height, width];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) normalGuide[y, x] = new(0, 0, 1, 1);
        normals.CopyFrom(new Rgba32[height, width]);
        guide.CopyFrom(normalGuide);
        string directory = Path.GetFullPath("output/nrd-synthetic-motion");
        Directory.CreateDirectory(directory);
        double maximumRmse = 0;
        var frames = new List<object>();
        float fov = 2 * MathF.Atan(.5f);
        for (int frame = 0; frame < frameCount; frame++)
        {
            float x = frame < 8 ? 0 : frame < 24 ? (frame - 7) * .03f : .48f;
            float currentFov = frame < 24 ? fov : fov * (1 + (frame - 23) * .015f);
            var camera = CameraFrame.Look(new(x, 0, 3), new(0, 0, -1), currentFov, .01f, 100);
            float tangent = MathF.Tan(currentFov * .5f), aspect = width / (float)height;
            for (int y = 0; y < height; y++) for (int px = 0; px < width; px++)
            {
                float nx = (2f * (px + .5f) / width - 1) * aspect * tangent;
                float ny = (2f * (y + .5f) / height - 1) * tangent;
                Vector3 direction = camera.Forward + camera.Right * nx - camera.Up * ny;
                float distance = -camera.Origin.Z / direction.Z;
                Vector3 world = camera.Origin + direction * distance;
                float r = Math.Clamp(.5f + world.X * .08f, .05f, .95f);
                float g = Math.Clamp(.5f + world.Y * .08f, .05f, .95f);
                float b = Math.Clamp(.5f + (world.X - world.Y) * .04f, .05f, .95f);
                radiance[y, px] = new(r, g, b, 2);
                positions[y, px] = new(world.X, world.Y, world.Z, direction.Length() * distance);
            }
            raw.CopyFrom(radiance); surfaces.CopyFrom(positions);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.NrdRelax,
                HdrRenderParameters.Default, 1.0 / 60, guide, camera), sr.Status.Message);
            Require(sr.NrdDispatchCount > 5, "Synthetic motion silently fell back from NRD");
            Require(sr.NrdMaxAccumulatedFrames == (frame < 8 ? NativeNrd.DefaultMaxAccumulatedFrames : NativeNrd.MotionMaxAccumulatedFrames),
                "NRD history length did not follow the synthetic camera path");
            float[] denoised = ReconstructionProbe.ReadTexture(native, sr.DenoisedColor, true);
            double squared = 0, maximum = 0;
            int count = 0;
            for (int y = 8; y < height - 8; y += 4) for (int px = 8; px < width - 8; px += 4)
            {
                int index = (y * width + px) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float expected = c == 0 ? radiance[y, px].X : c == 1 ? radiance[y, px].Y : radiance[y, px].Z;
                    double error = Math.Abs(denoised[index + c] - expected);
                    squared += error * error; maximum = Math.Max(maximum, error); count++;
                }
            }
            double rmse = Math.Sqrt(squared / count);
            maximumRmse = Math.Max(maximumRmse, rmse);
            frames.Add(new { frame, cameraX = x, verticalFov = currentFov, rmse, maximumError = maximum });
            SceneProbe.Save(target, Path.Combine(directory, $"frame-{frame:000}.png"));
            Console.WriteLine($"Synthetic NRD frame {frame:D2}: RMSE={rmse:F7}, max={maximum:F7}");
        }
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        var result = new { width, height, frameCount, maximumRmse, frames };
        File.WriteAllText(Path.Combine(directory, "motion.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Require(maximumRmse < .01, $"Noise-free NRD reprojection RMSE is {maximumRmse:F6}");
        Console.WriteLine($"PASS: noise-free NRD camera reprojection at {width}x{height}. {directory}");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
