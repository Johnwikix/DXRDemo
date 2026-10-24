using System.Numerics;
using System.Runtime.InteropServices;
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

/// <summary>Measures stationary temporal convergence against an independent arithmetic mean.</summary>
internal static unsafe class TemporalConvergenceProbe
{
    private const int Width = 64, Height = 64, EarlyFrames = 32, TotalFrames = 256;

    internal static void Run()
    {
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        Guid iid = typeof(ID3D12Device5).GUID;
        nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        using var native = new ID3D12Device5(pointer);
        using var debug = native.QueryInterface<ID3D12InfoQueue>();
        var failures = new List<Exception>();
        void Case(Action test)
        {
            try { test(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine($"FAIL {error.Message}"); }
        }
        Case(() => VerifyLinear(device, native, debug));
        Case(() => VerifyStationaryIsolation(device, native));
        Case(() => VerifyEncoded(device, debug));
        if (failures.Count != 0) throw new AggregateException("Temporal convergence regression failed", failures);
        Console.WriteLine("PASS stationary temporal convergence continues beyond the old 32/64-frame limits.");
    }

    private static void VerifyLinear(GraphicsDevice device, ID3D12Device5 native, ID3D12InfoQueue debug)
    {
        using var sr = new SuperResolutionRenderer(device);
        sr.Configure(ReconstructionMode.Off, 100, Width, Height, false, true);
        Require(sr.LinearPipelineActive && !sr.Status.NrdActive, "The native linear Temporal path was not active: " + sr.Status.Message);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(Width, Height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(Width, Height);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(Width, Height);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(Width, Height);
        using var signals = new PbrSignals(device, Width, Height);
        var samples = new Float4[Height, Width];
        var reference = new Float4[Height, Width];
        var geometry = new Float4[Height, Width];
        double[] sums = new double[Width * Height * 3];
        var camera = CameraFrame.Look(new Vector3(0, 0, 3), new Vector3(0, 0, -1), 2 * MathF.Atan(.5f), .01f, 1000);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            float wx = (2 * (x + .5f) / Width - 1) * Width / (float)Height * 1.5f;
            float wy = (1 - 2 * (y + .5f) / Height) * 1.5f;
            geometry[y, x] = new(wx, wy, 0, MathF.Sqrt(wx * wx + wy * wy + 9));
            reference[y, x] = new(.25f + .35f * x / (Width - 1), .3f + .3f * y / (Height - 1), .45f, 1);
        }
        surfaces.CopyFrom(geometry);
        normals.CopyFrom(new Rgba32[Height, Width]);
        double early = 0, late = 0;
        for (int frame = 0; frame < TotalFrames; frame++)
        {
            FillSamples(samples, reference, frame, .24f);
            Accumulate(sums, samples);
            raw.CopyFrom(samples);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.TemporalOnly,
                HdrRenderParameters.Default, 1.0 / 60, null, camera, signals), sr.Status.Message);
            if (frame == EarlyFrames - 1)
                early = Rmse(ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false), reference);
            if (frame == TotalFrames - 1)
            {
                float[] history = ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false);
                late = Rmse(history, reference);
                double meanError = RmseFromMean(history, sums, frame + 1);
                Console.WriteLine($"Linear Temporal: RMSE {early:F6} @ {EarlyFrames} -> {late:F6} @ {TotalFrames}; CPU-mean RMSE={meanError:E3}");
                Require(late < early * .45, "Linear Temporal retained a noise floor after its old 32-frame limit");
                Require(meanError < .0001, "Linear Temporal does not converge to the independent arithmetic mean");
            }
        }
        sr.Reset();
        surfaces.CopyFrom(geometry);
        for (int frame = 0; frame < 64; frame++)
        {
            FillSamples(samples, reference, 2000 + frame, .24f);
            raw.CopyFrom(samples);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.TemporalOnly,
                HdrRenderParameters.Default, 1.0 / 60, null, camera, signals), sr.Status.Message);
        }
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            float wx = (2 * (x + .5f) / Width - 1) * Width / (float)Height * 2;
            float wy = (1 - 2 * (y + .5f) / Height) * 2;
            geometry[y, x] = new(wx, wy, -1, MathF.Sqrt(wx * wx + wy * wy + 16));
        }
        surfaces.CopyFrom(geometry);
        double[] disoccludedSums = new double[sums.Length];
        for (int frame = 0; frame < 2; frame++)
        {
            FillSamples(samples, reference, 3000 + frame, .24f);
            Accumulate(disoccludedSums, samples);
            raw.CopyFrom(samples);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.TemporalOnly,
                HdrRenderParameters.Default, 1.0 / 60, null, camera, signals), sr.Status.Message);
            uint[] counts = ReadSampleCounts(native, sr.TemporalHistorySampleCounts);
            uint minimum = uint.MaxValue, maximum = 0;
            bool validCounts = true;
            for (int pixel = 0; pixel < Width * Height; pixel++)
            {
                minimum = Math.Min(minimum, counts[pixel * 2]); maximum = Math.Max(maximum, counts[pixel * 2]);
                validCounts &= counts[pixel * 2] == frame + 1 && counts[pixel * 2 + 1] == 0;
            }
            Console.WriteLine($"Linear Temporal post-disocclusion counts @ {frame + 1}: min={minimum}, max={maximum}");
            Require(validCounts, $"Post-disocclusion sample counts did not restart at frame {frame + 1}");
        }
        double disocclusionError = RmseFromMean(ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false), disoccludedSums, 2);
        Console.WriteLine($"Linear Temporal post-disocclusion two-sample mean RMSE={disocclusionError:E3}");
        Require(disocclusionError < .0001, "A rejected pixel retained the global frame count instead of restarting its own history");
        VerifyDebug(debug);
    }

    private static void VerifyStationaryIsolation(GraphicsDevice device, ID3D12Device5 native)
    {
        using var sr = new SuperResolutionRenderer(device);
        sr.Configure(ReconstructionMode.Off, 100, Width, Height, false, true);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(Width, Height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(Width, Height);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(Width, Height);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(Width, Height);
        using var signals = new PbrSignals(device, Width, Height);
        var camera = CameraFrame.Look(new Vector3(-20, 1.8f, 12), Vector3.Normalize(new(1, .03f, -.17f)), 1.04f, .001f, 1000);
        var geometry = new Float4[Height, Width];
        const float z = 2;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Vector3 position = camera.Origin + camera.Forward * z +
                camera.Right * ((2 * (x + .5f) / Width - 1) * MathF.Tan(camera.VerticalFov * .5f) * z) +
                camera.Up * ((1 - 2 * (y + .5f) / Height) * MathF.Tan(camera.VerticalFov * .5f) * z);
            geometry[y, x] = new(position.X, position.Y, position.Z, Vector3.Distance(camera.Origin, position));
        }
        surfaces.CopyFrom(geometry);
        normals.CopyFrom(new Rgba32[Height, Width]);
        var samples = new Float4[Height, Width];
        const int frames = 32;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            if (x % 7 == 0 && y % 11 == 0) samples[y, x] = new(64, 32, 16, 1);
        for (int frame = 0; frame < frames; frame++)
        {
            raw.CopyFrom(samples);
            Require(sr.Execute(raw, normals, surfaces, target, default, 3, default, RayTraceDenoiserMode.TemporalOnly,
                HdrRenderParameters.Default, 1.0 / 60, null, camera, signals), sr.Status.Message);
            Array.Clear(samples);
        }
        float[] history = ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false);
        double leakage = 0, peakError = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            if (x % 7 == 0 && y % 11 == 0)
                peakError = Math.Max(peakError, Math.Abs(history[(y * Width + x) * 4] - 64f / frames));
            else
                leakage = Math.Max(leakage, history[(y * Width + x) * 4]);
        Console.WriteLine($"Stationary isolated sample: neighbor leakage={leakage:E3}, mean error={peakError:E3}");
        Require(leakage < 1e-7 && peakError < 1e-5,
            "A stationary pixel's running mean spread a bright sample into neighboring pixels during reprojection");

        // An unchanged camera with a changed SR jitter still needs a different raster footprint.
        Vector3 shift = camera.Right * (MathF.Tan(camera.VerticalFov * .5f) * z / Width);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Float4 p = geometry[y, x];
            Vector3 position = new Vector3(p.X, p.Y, p.Z) + shift;
            geometry[y, x] = new(position.X, position.Y, position.Z, Vector3.Distance(camera.Origin, position));
        }
        surfaces.CopyFrom(geometry);
        Require(sr.Execute(raw, normals, surfaces, target, default, 3, new Float2(.5f, 0), RayTraceDenoiserMode.TemporalOnly,
            HdrRenderParameters.Default, 1.0 / 60, null, camera, signals), sr.Status.Message);
        history = ReconstructionProbe.ReadTexture(native, sr.DenoiserHistory, false);
        float expected = 32f / (frames + 1);
        double jitterError = Math.Max(Math.Abs(history[(22 * Width + 20) * 4] - expected),
            Math.Abs(history[(22 * Width + 21) * 4] - expected));
        Console.WriteLine($"Temporal changed-jitter reprojection error={jitterError:E3}");
        Require(jitterError < .002, "Changed sampling jitter bypassed history reprojection");
    }

    private static void VerifyEncoded(GraphicsDevice device, ID3D12InfoQueue debug)
    {
        using var signal = device.AllocateReadWriteTexture2D<Rgba64, Float4>(Width, Height);
        using var history = device.AllocateReadWriteTexture2D<Rgba64, Float4>(Width, Height);
        using var output = device.AllocateReadWriteTexture2D<Rgba64, Float4>(Width, Height);
        var samples = new Float4[Height, Width];
        var encoded = new Rgba64[Height, Width];
        var reference = new Float4[Height, Width];
        double[] sums = new double[Width * Height * 3];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            reference[y, x] = new(.25f + .35f * x / (Width - 1), .3f + .3f * y / (Height - 1), .45f, 1);
        double early = 0, late = 0;
        for (int frame = 0; frame < TotalFrames; frame++)
        {
            FillSamples(samples, reference, frame + 10000, .24f);
            Encode(samples, encoded);
            Accumulate(sums, encoded);
            signal.CopyFrom(encoded);
            device.ForEach(output, new NaiveTemporalAccumulationShader(frame, signal, history));
            if (frame == EarlyFrames - 1) early = Rmse(output.ToArray(), reference);
            if (frame == TotalFrames - 1)
            {
                Rgba64[,] result = output.ToArray();
                late = Rmse(result, reference);
                double meanError = RmseFromMean(result, sums, frame + 1);
                Console.WriteLine($"Encoded Temporal: RMSE {early:F6} @ {EarlyFrames} -> {late:F6} @ {TotalFrames}; CPU-mean RMSE={meanError:E3}");
                Require(late < early * .45, "Encoded Temporal retained a noise floor after its old 64-frame limit");
                Require(meanError < .002, "Encoded Temporal does not converge to the independent arithmetic mean");
            }
        }
        VerifyDebug(debug);
    }

    private static void FillSamples(Float4[,] samples, Float4[,] reference, int frame, float amplitude)
    {
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Float4 expected = reference[y, x];
            samples[y, x] = new(
                Math.Clamp(expected.X + Noise(x, y, frame, 0) * amplitude, 0, 1),
                Math.Clamp(expected.Y + Noise(x, y, frame, 1) * amplitude, 0, 1),
                Math.Clamp(expected.Z + Noise(x, y, frame, 2) * amplitude, 0, 1), 1);
        }
    }

    private static void Encode(Float4[,] samples, Rgba64[,] encoded)
    {
        static ushort Channel(float value) => (ushort)Math.Clamp((int)MathF.Round(value * 65535), 0, 65535);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Float4 value = samples[y, x];
            encoded[y, x] = new(Channel(value.X), Channel(value.Y), Channel(value.Z), Channel(value.W));
        }
    }

    private static float Noise(int x, int y, int frame, int channel)
    {
        uint value = (uint)(x + 1) * 0x9e3779b1u ^ (uint)(y + 1) * 0x85ebca6bu ^
            (uint)(frame + 1) * 0xc2b2ae35u ^ (uint)(channel + 1) * 0x27d4eb2fu;
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu; value ^= value >> 16;
        return (value >> 8) * (2f / 16777216f) - 1;
    }

    private static void Accumulate(double[] sums, Float4[,] samples)
    {
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int index = (y * Width + x) * 3;
            Float4 value = samples[y, x];
            sums[index] += value.X; sums[index + 1] += value.Y; sums[index + 2] += value.Z;
        }
    }

    private static void Accumulate(double[] sums, Rgba64[,] samples)
    {
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int index = (y * Width + x) * 3;
            Rgba64 value = samples[y, x];
            sums[index] += value.R / 65535.0;
            sums[index + 1] += value.G / 65535.0;
            sums[index + 2] += value.B / 65535.0;
        }
    }

    private static double Rmse(float[] actual, Float4[,] reference)
    {
        double error = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int source = (y * Width + x) * 4;
            Float4 expected = reference[y, x];
            error += (actual[source] - expected.X) * (actual[source] - expected.X);
            error += (actual[source + 1] - expected.Y) * (actual[source + 1] - expected.Y);
            error += (actual[source + 2] - expected.Z) * (actual[source + 2] - expected.Z);
        }
        return Math.Sqrt(error / (Width * Height * 3));
    }

    private static double Rmse(Rgba64[,] actual, Float4[,] reference)
    {
        double error = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Rgba64 a = actual[y, x];
            Float4 expected = reference[y, x];
            double dx = a.R / 65535.0 - expected.X, dy = a.G / 65535.0 - expected.Y, dz = a.B / 65535.0 - expected.Z;
            error += dx * dx + dy * dy + dz * dz;
        }
        return Math.Sqrt(error / (Width * Height * 3));
    }

    private static double RmseFromMean(float[] actual, double[] sums, int frames)
    {
        double error = 0;
        for (int pixel = 0; pixel < Width * Height; pixel++) for (int channel = 0; channel < 3; channel++)
        {
            double delta = actual[pixel * 4 + channel] - sums[pixel * 3 + channel] / frames;
            error += delta * delta;
        }
        return Math.Sqrt(error / (Width * Height * 3));
    }

    private static double RmseFromMean(Rgba64[,] actual, double[] sums, int frames)
    {
        double error = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int index = (y * Width + x) * 3;
            Rgba64 value = actual[y, x];
            double dx = value.R / 65535.0 - sums[index] / frames;
            double dy = value.G / 65535.0 - sums[index + 1] / frames;
            double dz = value.B / 65535.0 - sums[index + 2] / frames;
            error += dx * dx + dy * dy + dz * dz;
        }
        return Math.Sqrt(error / (Width * Height * 3));
    }

    internal static uint[] ReadSampleCounts(ID3D12Device5 device, ID3D12Resource source)
    {
        var desc = source.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        uint pitch = (uint)((width * 2 * sizeof(uint) + 255) & ~255);
        using var readback = device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer((ulong)pitch * (uint)height), ResourceStates.CopyDest);
        using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var commands = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        using var fence = device.CreateFence(0);
        commands.ResourceBarrierTransition(source, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(readback, new PlacedSubresourceFootPrint
        {
            Footprint = new SubresourceFootPrint(desc.Format, (uint)width, (uint)height, 1, pitch)
        }), 0, 0, 0, new TextureCopyLocation(source, 0));
        commands.ResourceBarrierTransition(source, ResourceStates.CopySource, ResourceStates.NonPixelShaderResource);
        commands.Close(); queue.ExecuteCommandList(commands); queue.Signal(fence, 1).CheckError();
        using var done = new AutoResetEvent(false);
        fence.SetEventOnCompletion(1, done.SafeWaitHandle.DangerousGetHandle()).CheckError();
        Require(done.WaitOne(TimeSpan.FromSeconds(30)), "Sample-count readback timed out");
        byte* address = readback.Map<byte>(0);
        var result = new uint[width * height * 2];
        for (int y = 0; y < height; y++) for (int x = 0; x < width * 2; x++)
            result[y * width * 2 + x] = ((uint*)(address + y * pitch))[x];
        readback.Unmap(0);
        return result;
    }

    private static void VerifyDebug(ID3D12InfoQueue queue)
    {
        for (ulong i = 0; i < queue.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = queue.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        queue.ClearStoredMessages();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
