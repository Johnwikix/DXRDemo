using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Scene;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

/// <summary>Renders the actual Bistro camera through the production native Temporal path.</summary>
internal static class TemporalSceneProbe
{
    internal static async Task Run(string[] args)
    {
        int Option(string name, int fallback, int maximum)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < 1 || value > maximum)
                throw new ArgumentException($"{name} must be between 1 and {maximum}.");
            return value;
        }
        int width = Option("--width", 960, 3840), height = Option("--height", 540, 2160);
        int frames = Option("--frames", 256, 1024), referenceFrames = Option("--reference-frames", 16, 64);
        int referenceSpp = Option("--reference-spp", 32, 64), referenceSeed = Option("--reference-seed", 100000, 1000000000);
        int samples = Option("--spp", 1, 16), bounces = Option("--bounces", 8, 32);
        int textureSize = Option("--texture-size", 1024, 4096);
        bool useNrd = args.Contains("--nrd");
        int outputIndex = Array.IndexOf(args, "--output");
        string? outputDirectory = outputIndex >= 0 ? args.ElementAtOrDefault(outputIndex + 1)
            ?? throw new ArgumentException("--output requires a directory.") : null;
        int thresholdIndex = Array.IndexOf(args, "--firefly-threshold");
        float fireflyThreshold = thresholdIndex >= 0 ? float.Parse(args[thresholdIndex + 1], System.Globalization.CultureInfo.InvariantCulture) : 64;
        SceneDocument document = await RtxptAssets.LoadAsync(RtxptScene.Bistro, maximumTextureSize: textureSize);
        SceneAsset asset = document.Scenes[document.DefaultScene];
        if (args.Contains("--no-emission") || args.Contains("--no-normal-maps"))
            asset = asset with { Materials = asset.Materials.Select(material =>
            {
                if (args.Contains("--no-emission")) material.Emissive = Vector4.Zero;
                if (args.Contains("--no-normal-maps")) material.NormalMap = TextureBinding.Missing;
                return material;
            }).ToArray() };
        if (args.Contains("--uncompressed"))
            asset = asset with { Textures = asset.Textures.Select(texture => texture with { Bc7Mips = null }).ToArray() };
        float environment = args.Contains("--no-environment") ? 0 : 1;
        var controller = new CameraController(); controller.FrameScene(asset, width / (float)height);
        CameraFrame camera = controller.Advance(0, out _, out _);
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); layer.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        using var native = NativeDevice(device);
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend { ReferenceMode = false, CausticsEnabled = !args.Contains("--no-caustics"),
            AdaptiveLightingEnabled = !args.Contains("--no-adaptive-lighting"), FireflyThreshold = fireflyThreshold,
            UseInlineTraversal = !args.Contains("--legacy-trace"), ReorderThreads = false };
        backend.Initialize(device); backend.SetScene(asset);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var guide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        using var reconstruction = new SuperResolutionRenderer(device);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        reconstruction.Configure(ReconstructionMode.Off, 100, width, height, useNrd, true);
        Require(reconstruction.LinearPipelineActive && reconstruction.Status.NrdActive == useNrd, reconstruction.Status.Message);
        if (useNrd) reconstruction.PrepareStablePlanes();
        float exposureStops = RtxptAssets.InitialExposure(RtxptScene.Bistro);
        string directory = Path.GetFullPath(outputDirectory ?? ($"output/temporal-bistro-{width}x{height}" + (useNrd ? "-nrd" : ""))); Directory.CreateDirectory(directory);
        int pixelChannels = width * height * 3;
        double[] rawSum = new double[pixelChannels], rawSquare = new double[pixelChannels];
        double[] temporalSum = new double[pixelChannels], temporalSquare = new double[pixelChannels];
        Float4[,]? lastRaw = null;
        float[]? lastTemporal = null;
        var checkpoints = new List<object>();
        const int measuredFrames = 16;
        for (int frame = 0; frame < frames; frame++)
        {
            backend.Trace(camera, frame, samples, bounces, default, environment, raw, normals, surfaces, guide, signals, opaqueForNrd: useNrd);
            Require(reconstruction.Execute(raw, normals, surfaces, target, default, 3, default,
                useNrd ? RayTraceDenoiserMode.NrdRelax : RayTraceDenoiserMode.TemporalOnly,
                HdrRenderParameters.Default, 1.0 / 60, guide, camera, signals, exposureStops, transparentBackend: useNrd ? backend : null), reconstruction.Status.Message);
            int number = frame + 1;
            bool capture = number is 1 or 32 or 64 or 128 or 256 or 1024 || number == frames;
            bool measure = frame >= frames - measuredFrames;
            if (!capture && !measure) continue;
            Float4[,] rawFrame = raw.ToArray();
            float[] temporal = ReconstructionProbe.ReadTexture(native, reconstruction.DenoisedColor, true);
            Require(temporal.All(float.IsFinite), "Non-finite Temporal output in the actual Bistro scene");
            if (capture)
            {
                uint? minimum = null, maximum = null;
                int expected = 0;
                if (!useNrd)
                {
                    uint[] counts = TemporalConvergenceProbe.ReadSampleCounts(native, reconstruction.TemporalHistorySampleCounts);
                    minimum = uint.MaxValue; maximum = 0;
                    for (int pixel = 0; pixel < width * height; pixel++)
                    {
                        uint count = counts[pixel * 2];
                        minimum = Math.Min(minimum.Value, count); maximum = Math.Max(maximum.Value, count);
                        if (count == number && counts[pixel * 2 + 1] == 0) expected++;
                    }
                }
                double? coverage = useNrd ? null : expected / (double)(width * height);
                string temporalPath = Path.Combine(directory, $"temporal-{number:D4}.png");
                string rawPath = Path.Combine(directory, $"raw-{number:D4}.png");
                SceneProbe.Save(target, temporalPath);
                SaveLinear(rawFrame, exposureStops, rawPath);
                if (args.Contains("--capture-linear"))
                {
                    File.WriteAllBytes(Path.Combine(directory, $"raw-{number:D4}.f32"), MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref rawFrame[0, 0], rawFrame.Length)).ToArray());
                    File.WriteAllBytes(Path.Combine(directory, $"temporal-{number:D4}.f32"), MemoryMarshal.AsBytes(temporal.AsSpan()).ToArray());
                }
                double difference = Difference(rawFrame, temporal);
                var rawStats = Statistics(rawFrame);
                var temporalStats = Statistics(temporal);
                checkpoints.Add(new { frame = number, minimumCount = minimum, maximumCount = maximum, expectedCountCoverage = coverage,
                    compressedRawTemporalRmse = difference, rawMeanLinear = rawStats.Linear, temporalMeanLinear = temporalStats.Linear,
                    rawMeanCompressed = rawStats.Compressed, temporalMeanCompressed = temporalStats.Compressed, rawMaximum = rawStats.Maximum,
                    temporalPath, rawPath, traceGpuMilliseconds = backend.TraceGpuMilliseconds });
                string history = useNrd ? $"NRD history={reconstruction.NrdMaxAccumulatedFrames}" :
                    $"count min/max={minimum}/{maximum}, expected={coverage:P2}";
                Console.WriteLine($"Bistro frame {number}: {history}, raw-vs-temporal RMSE={difference:F6}, " +
                    $"mean linear raw/temporal={rawStats.Linear:E3}/{temporalStats.Linear:E3}, raw max={rawStats.Maximum:E2}");
            }
            if (measure) Accumulate(rawFrame, temporal, rawSum, rawSquare, temporalSum, temporalSquare);
            if (number == frames) { lastRaw = rawFrame; lastTemporal = temporal; }
        }
        Require(lastRaw != null && lastTemporal != null, "Missing final Bistro frame");
        double rawRms = TemporalRms(rawSum, rawSquare, measuredFrames);
        double temporalRms = TemporalRms(temporalSum, temporalSquare, measuredFrames);
        Console.WriteLine($"Bistro late {measuredFrames}-frame RMS: raw={rawRms:F6}, temporal={temporalRms:F6}");

        backend.ReferenceMode = args.Contains("--reference-independent");
        double[] referenceSum = new double[pixelChannels];
        for (int frame = 0; frame < referenceFrames; frame++)
        {
            backend.Trace(camera, referenceSeed + frame, referenceSpp, bounces, default, environment, raw, normals, surfaces, guide, signals);
            Float4[,] referenceFrame = raw.ToArray();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 3;
                Float4 value = referenceFrame[y, x];
                referenceSum[index] += value.X; referenceSum[index + 1] += value.Y; referenceSum[index + 2] += value.Z;
            }
        }
        double invReference = 1.0 / referenceFrames;
        for (int i = 0; i < referenceSum.Length; i++) referenceSum[i] *= invReference;
        string referenceKind = backend.ReferenceMode ? "independent" : "production";
        string referencePath = Path.Combine(directory, $"reference-{referenceKind}-{referenceFrames * referenceSpp}spp.png");
        SaveLinear(referenceSum, exposureStops, referencePath, width, height);
        double rawReference = ReferenceError(lastRaw!, referenceSum);
        double temporalReference = ReferenceError(lastTemporal!, referenceSum);
        var referenceStats = Statistics(referenceSum);
        Console.WriteLine($"Bistro vs {referenceKind} {referenceFrames * referenceSpp}-SPP reference: raw={rawReference:F6}, temporal={temporalReference:F6}, " +
            $"mean linear={referenceStats.Linear:E3}, compressed={referenceStats.Compressed:F4}");
        Console.WriteLine($"Images: {directory}");
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        var result = new { scene = asset.Name, gpu = device.Name.ToString(), denoiser = useNrd ? "NrdRelax" : "TemporalOnly", width, height, samples, bounces, frames,
            fireflyThreshold, adaptiveLighting = backend.AdaptiveLightingEnabled, caustics = backend.CausticsEnabled,
            referenceSamples = referenceFrames * referenceSpp, referenceKind, exposureStops, rawLateFrameRms = rawRms, temporalLateFrameRms = temporalRms,
            rawReferenceRmse = rawReference, temporalReferenceRmse = temporalReference, checkpoints, referencePath };
        File.WriteAllText(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Require(temporalRms < rawRms * .5, $"Temporal output still fluctuates like the 1-SPP input: raw={rawRms}, temporal={temporalRms}");
        Require(temporalReference < rawReference, "Temporal output is not closer to the production high-SPP reference than the raw frame");
    }

    private static void Accumulate(Float4[,] raw, float[] temporal, double[] rawSum, double[] rawSquare,
        double[] temporalSum, double[] temporalSquare)
    {
        int height = raw.GetLength(0), width = raw.GetLength(1);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x;
            Float4 value = raw[y, x];
            for (int c = 0; c < 3; c++)
            {
                double r = Compress(c == 0 ? value.X : c == 1 ? value.Y : value.Z);
                double t = Compress(temporal[pixel * 4 + c]);
                int index = pixel * 3 + c;
                rawSum[index] += r; rawSquare[index] += r * r;
                temporalSum[index] += t; temporalSquare[index] += t * t;
            }
        }
    }

    private static double TemporalRms(double[] sum, double[] square, int frames)
    {
        double variance = 0;
        for (int i = 0; i < sum.Length; i++)
        {
            double mean = sum[i] / frames;
            variance += Math.Max(0, square[i] / frames - mean * mean);
        }
        return Math.Sqrt(variance / sum.Length);
    }

    private static double Difference(Float4[,] raw, float[] temporal)
    {
        double error = 0;
        int height = raw.GetLength(0), width = raw.GetLength(1);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x;
            Float4 value = raw[y, x];
            for (int c = 0; c < 3; c++)
            {
                double delta = Compress(c == 0 ? value.X : c == 1 ? value.Y : value.Z) - Compress(temporal[pixel * 4 + c]);
                error += delta * delta;
            }
        }
        return Math.Sqrt(error / (width * height * 3));
    }

    private static double ReferenceError(Float4[,] raw, double[] reference)
    {
        double error = 0;
        int height = raw.GetLength(0), width = raw.GetLength(1);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x;
            Float4 value = raw[y, x];
            for (int c = 0; c < 3; c++)
            {
                double delta = Compress(c == 0 ? value.X : c == 1 ? value.Y : value.Z) - Compress(reference[pixel * 3 + c]);
                error += delta * delta;
            }
        }
        return Math.Sqrt(error / reference.Length);
    }

    private static double ReferenceError(float[] actual, double[] reference)
    {
        double error = 0;
        for (int pixel = 0; pixel < reference.Length / 3; pixel++) for (int c = 0; c < 3; c++)
        {
            double delta = Compress(actual[pixel * 4 + c]) - Compress(reference[pixel * 3 + c]);
            error += delta * delta;
        }
        return Math.Sqrt(error / reference.Length);
    }

    private static (double Linear, double Compressed, double Maximum) Statistics(Float4[,] pixels)
    {
        double linear = 0, compressed = 0, maximum = 0;
        foreach (Float4 value in pixels)
        {
            for (int c = 0; c < 3; c++)
            {
                double channel = c == 0 ? value.X : c == 1 ? value.Y : value.Z;
                linear += channel; compressed += Compress(channel); maximum = Math.Max(maximum, channel);
            }
        }
        return (linear / (pixels.Length * 3), compressed / (pixels.Length * 3), maximum);
    }

    private static (double Linear, double Compressed, double Maximum) Statistics(float[] pixels)
    {
        double linear = 0, compressed = 0, maximum = 0;
        for (int pixel = 0; pixel < pixels.Length / 4; pixel++) for (int c = 0; c < 3; c++)
        {
            double value = pixels[pixel * 4 + c];
            linear += value; compressed += Compress(value); maximum = Math.Max(maximum, value);
        }
        return (linear / (pixels.Length / 4 * 3), compressed / (pixels.Length / 4 * 3), maximum);
    }

    private static (double Linear, double Compressed, double Maximum) Statistics(double[] pixels)
    {
        double linear = 0, compressed = 0, maximum = 0;
        foreach (double value in pixels)
        {
            linear += value; compressed += Compress(value); maximum = Math.Max(maximum, value);
        }
        return (linear / pixels.Length, compressed / pixels.Length, maximum);
    }

    private static double Compress(double value) => Math.Max(value, 0) / (1 + Math.Max(value, 0));

    private static void SaveLinear(Float4[,] pixels, float exposureStops, string path)
    {
        int height = pixels.GetLength(0), width = pixels.GetLength(1);
        double[] linear = new double[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 3;
            Float4 value = pixels[y, x];
            linear[index] = value.X; linear[index + 1] = value.Y; linear[index + 2] = value.Z;
        }
        SaveLinear(linear, exposureStops, path, width, height);
    }

    private static void SaveLinear(double[] linear, float exposureStops, string path, int width, int height)
    {
        float exposure = MathF.Pow(2, exposureStops);
        byte[] rgba = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++) for (int c = 0; c < 3; c++)
        {
            float value = (float)linear[pixel * 3 + c] * exposure;
            value = Math.Clamp(value * (2.51f * value + .03f) / Math.Max(value * (2.43f * value + .59f) + .14f, 1e-8f), 0, 1);
            rgba[pixel * 4 + c] = (byte)Math.Clamp(MathF.Round(MathF.Pow(value, 1 / 2.2f) * 255), 0, 255);
        }
        for (int pixel = 0; pixel < width * height; pixel++) rgba[pixel * 4 + 3] = 255;
        SceneProbe.WritePng(path, width, height, rgba);
    }

    private static unsafe ID3D12Device5 NativeDevice(GraphicsDevice device)
    {
        Guid iid = typeof(ID3D12Device5).GUID;
        nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        return new(pointer);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
