using System.Numerics;
using ComputeSharp;
using DXRDemo.DXR;
using DXRDemo.Scene;

internal static class EnvironmentSamplingProbe
{
    internal static void Run()
    {
        const int width = 512, height = 512;
        var map = SceneEnvironment.LoadRadiance(Path.Combine(AppContext.BaseDirectory,
            "Assets", "Models", "RTXPT", "EnvironmentMaps", "shanghai_bund_1k.hdr"));
        var asset = OfflineFeatureChecks.Fixture() with { EnvironmentMap = map };
        using var device = GraphicsDevice.GetDefault();
        using var backend = new DxrSceneBackend { ShaderLibraryFile = "EnvironmentSamplerTracer.hlsl" };
        backend.Initialize(device); backend.SetScene(asset);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var guide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        backend.Trace(asset.DefaultCamera!.Value, 0, 1, 1, default, 1, raw, normals, surfaces, guide, signals);
        var pixels = raw.ToArray();
        double expectedR = 0, expectedG = 0, expectedB = 0;
        for (int y = 0; y < map.Height; y++)
        {
            double omega = 2 * Math.PI / map.Width * (Math.Cos(Math.PI * y / map.Height) - Math.Cos(Math.PI * (y + 1) / map.Height));
            for (int x = 0; x < map.Width; x++)
            {
                Vector4 p = map.Pixels[y * map.Width + x];
                expectedR += p.X * omega; expectedG += p.Y * omega; expectedB += p.Z * omega;
            }
        }
        double r = 0, g = 0, b = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            Float4 p = pixels[y, x];
            r += p.X; g += p.Y; b += p.Z;
        }
        double count = width * height;
        Console.WriteLine($"Environment integral: GPU {r / count:F6}, {g / count:F6}, {b / count:F6}; CPU {expectedR:F6}, {expectedG:F6}, {expectedB:F6}");
        if (Math.Abs(r / count / expectedR - 1) > .04 || Math.Abs(g / count / expectedG - 1) > .04 || Math.Abs(b / count / expectedB - 1) > .04)
            throw new InvalidOperationException("Production HDR environment sampling disagrees with the independent spherical integral.");
        Console.WriteLine("PASS production HDR environment sampling preserves the independent spherical integral within 4%.");
    }
}
