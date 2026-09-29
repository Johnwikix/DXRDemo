using ComputeSharp;
using ComputeSharp.Interop;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using System.Runtime.InteropServices;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

/// <summary>Exercises actual vendor dispatch, shared texture states and temporal camera guides without a window.</summary>
internal static unsafe class ReconstructionProbe
{
    internal static void Run(bool missingNrd = false)
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        debug.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        Console.WriteLine($"GPU: {device.Name}");
        Guid iid = typeof(ID3D12Device5).GUID;
        nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        using var native = new ID3D12Device5(pointer);
        using var queue = native.QueryInterface<ID3D12InfoQueue>();
        using var pass = new RayTracePass(new DxrMeshBackend());
        pass.Initialize(device, default);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(480, 270);
        MeshData.LoadAsync(0).GetAwaiter().GetResult();
        Render(pass, target, false, 3);
        var status = pass.ReconstructionStatus!;
        Console.WriteLine($"Capabilities: 0x{status.Capabilities:X}");
        Console.WriteLine($"NRD available: {status.NrdAvailable}; active: {status.NrdActive}; dispatches: {pass.NrdDispatchCount}; {status.Message}");
        if (missingNrd)
        {
            Require(!status.NrdAvailable && !status.NrdActive && status.Message.Contains("NRD"), "Missing NRD DLL was not reported");
            VerifyPixels(target); VerifyDebug(queue);
            pass.ReconstructionMode = ReconstructionMode.Fsr;
            Render(pass, target, false, 6);
            Require(pass.ReconstructionStatus is { Active: ReconstructionMode.Fsr, NrdActive: false }, "Missing NRD disabled the independent SR provider");
            VerifyPixels(target); VerifyDebug(queue);
            Console.WriteLine("PASS missing NRD DLL: explicit legacy denoiser fallback; FSR still executes");
            return;
        }
        Require(status.NrdAvailable && status.NrdActive && pass.NrdDispatchCount > 5, "Official NRD did not run at native resolution");
        Console.WriteLine(Marshal.PtrToStringUTF8(NativeReconstruction.FsrDiagnostic()));
        Require(status.Supports(ReconstructionMode.Fsr), "Bundled FSR provider not found: run IntegrationProbe.exe, not dotnet IntegrationProbe.dll");
        foreach (var mode in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS, ReconstructionMode.Dlss })
        {
            if (!status.Supports(mode))
            {
                pass.ReconstructionMode = mode;
                Render(pass, target, false, 2);
                Require(pass.ReconstructionStatus!.Active == ReconstructionMode.Off, "Unsupported provider did not fall back");
                Console.WriteLine($"PASS {mode} unavailable; explicit native fallback");
                continue;
            }
            foreach (int scale in new[] { 67, 50, 100, 33, 37 })
            {
                pass.ReconstructionMode = mode;
                pass.RenderScalePercent = scale;
                foreach (var denoiser in Enum.GetValues<RayTraceDenoiserMode>())
                foreach (bool hdr in new[] { false, true })
                {
                    pass.DenoiserMode = denoiser;
                    Render(pass, target, hdr, 6);
                    var active = pass.ReconstructionStatus!;
                    Require(active.Active == mode, active.Message);
                    Require(active.NrdActive == (denoiser == RayTraceDenoiserMode.NrdRelax), active.Message);
                    if (active.NrdActive) Require(pass.NrdDispatchCount > 5, "NRD fallback was counted as success");
                    Require(active.InputWidth == 480 * scale / 100 && active.InputHeight == 270 * scale / 100, "Input dimensions differ from requested scale");
                    VerifyPixels(target);
                    VerifyDebug(queue);
                    if (scale == 67 && (denoiser is RayTraceDenoiserMode.Relax or RayTraceDenoiserMode.NrdRelax) && !hdr)
                        SavePpm(target, $"output/{mode}-{denoiser}-67-sdr.ppm");
                }
                Console.WriteLine($"PASS {mode} {scale}% / all denoisers / SDR+HDR");
            }
            using (var odd = device.AllocateReadWriteTexture2D<Rgba64, Float4>(401, 227))
            {
                Render(pass, odd, false, 3);
                Require(pass.ReconstructionStatus!.Active == mode, pass.ReconstructionStatus.Message);
                Require(pass.ReconstructionStatus.InputWidth == 148 && pass.ReconstructionStatus.InputHeight == 83, "Odd resize dimensions");
                VerifyPixels(odd); VerifyDebug(queue);
            }
            pass.SceneIndex = 1;
            MeshData.LoadAsync(1).GetAwaiter().GetResult();
            Render(pass, target, false, 3);
            VerifyPixels(target); VerifyDebug(queue);
            pass.SceneIndex = 0;
            using (var tinyInput = device.AllocateReadWriteTexture2D<Rgba64, Float4>(1600, 900))
            {
                pass.RenderScalePercent = 1;
                Render(pass, tinyInput, false, 3);
                Require(pass.ReconstructionStatus!.Active == mode, pass.ReconstructionStatus.Message);
                Require(pass.ReconstructionStatus.InputWidth == 16 && pass.ReconstructionStatus.InputHeight == 9, "1% input was silently clamped");
                VerifyDebug(queue);
                Console.WriteLine($"PASS {mode} 16x9 input -> 1600x900 (execution, not perceptual quality)");
            }
        }
        pass.ReconstructionMode = ReconstructionMode.Off;
        Render(pass, target, false, 3);
        Require(pass.ReconstructionStatus!.Active == ReconstructionMode.Off, "Failed to disable SR");
        VerifyPixels(target); VerifyDebug(queue);
        VerifyGuides(device, native, queue, status);
        VerifyTemporalReprojection(device, native, queue, status);
        VerifyNrdQuality(device, native, queue);
        VerifyDebug(queue);
        Console.WriteLine("PASS reconstruction mode/scale/denoiser/HDR transitions, odd resize, scene switch, fallback and camera guides.");
    }

    private static void SavePpm(ReadWriteTexture2D<Rgba64, Float4> target, string path)
    {
        var pixels = target.ToArray();
        var channels = MemoryMarshal.Cast<Rgba64, ushort>(MemoryMarshal.CreateReadOnlySpan(ref pixels[0, 0], pixels.Length));
        using var file = File.Create(path);
        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{target.Width} {target.Height}\n255\n"));
        for (int i = 0; i < channels.Length; i += 4)
        {
            file.WriteByte((byte)(channels[i] >> 8)); file.WriteByte((byte)(channels[i + 1] >> 8)); file.WriteByte((byte)(channels[i + 2] >> 8));
        }
    }

    private static void Render(RayTracePass pass, ReadWriteTexture2D<Rgba64, Float4> target, bool hdr, int frames)
    {
        for (int i = 0; i < frames; i++) pass.TryExecute(target, target.Width, target.Height, TimeSpan.Zero, new HdrRenderParameters(hdr, 200, 1000));
    }

    private static void VerifyPixels(ReadWriteTexture2D<Rgba64, Float4> target)
    {
        var pixels = target.ToArray();
        var channels = MemoryMarshal.Cast<Rgba64, ushort>(MemoryMarshal.CreateReadOnlySpan(ref pixels[0, 0], pixels.Length));
        double rgb = 0; int lo = 65535, hi = 0;
        for (int i = 0; i < channels.Length; i += 4)
        {
            rgb += channels[i] + channels[i + 1] + channels[i + 2];
            lo = Math.Min(lo, channels[i]); hi = Math.Max(hi, channels[i]);
        }
        Require(rgb / (pixels.Length * 3) > 500 && hi - lo > 1000, "Empty or constant reconstructed RGB");
    }

    private static void VerifyGuides(GraphicsDevice device, ID3D12Device5 native, ID3D12InfoQueue info, ReconstructionStatus status)
    {
        var mode = status.Supports(ReconstructionMode.Fsr) ? ReconstructionMode.Fsr : ReconstructionMode.XeSS;
        Require(status.Supports(mode), "No SR provider available for guide verification");
        using var sr = new SuperResolutionRenderer(device);
        using var backend = new DxrMeshBackend();
        backend.Initialize(device);
        sr.Configure(mode, 101, 320, 180); // Force the native ABI to reject an oversized input before allocation.
        Require(sr.Active == ReconstructionMode.Off, "SDK initialization failure did not fall back");
        sr.Configure(mode, 67, 320, 180);
        Require(sr.Active == mode, sr.Status.Message);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(320, 180);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(sr.InputWidth, sr.InputHeight);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(sr.InputWidth, sr.InputHeight);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(sr.InputWidth, sr.InputHeight);
        MeshData mesh = MeshData.LoadAsync(0).GetAwaiter().GetResult();
        void Frame(Float2 orbit, Float2 jitter, int index)
        {
            backend.Trace(mesh, sr.InputWidth, sr.InputHeight, 2, 10, index, orbit, 2.7f, raw, normals, surfaces, jitter);
            Require(sr.Execute(raw, normals, surfaces, target, orbit, 2.7f, jitter, RayTraceDenoiserMode.Relax, HdrRenderParameters.Default, 1.0 / 60), sr.Status.Message);
            VerifyDebug(info);
        }
        Frame(new(0, 0.165f), new(-0.37f, 0.29f), 0);
        Frame(new(0, 0.165f), new(0.41f, -0.33f), 1);
        float[] still = ReadTexture(native, sr.Motion, true);
        Require(still.Max(Math.Abs) < 0.002f, "Static motion incorrectly includes jitter");
        float[] depth = ReadTexture(native, sr.Depth, false);
        Require(depth.All(x => float.IsFinite(x) && x is >= 0 and <= 1) && depth.Min() < 0.999f && depth.Max() > 0.9999f, "Invalid device depth");
        Frame(new(0.05f, 0.165f), new(0.1f, -0.2f), 2);
        float[] moving = ReadTexture(native, sr.Motion, true);
        Require(moving.All(float.IsFinite) && moving.Max(Math.Abs) > 0.1f, "Moving camera did not produce finite nonzero motion");
        VerifyProjection(surfaces.ToArray(), moving, ReadTexture(native, sr.Depth, false), sr.InputWidth, sr.InputHeight);
        Require(ReadTexture(native, sr.LinearOutput, true, ResourceStates.UnorderedAccess).All(float.IsFinite), "Nonfinite linear SR output");
        sr.Reset();
        Frame(new(0.2f, 0.165f), default, 0);
        Require(ReadTexture(native, sr.Motion, true).Max(Math.Abs) < 0.002f, "History reset retained stale motion");
        Console.WriteLine("PASS GPU depth range, zero static motion under jitter, moving camera motion and reset");
    }

    private static void VerifyProjection(Float4[,] surfaces, float[] motion, float[] depth, int width, int height)
    {
        Matrix4x4 ViewProjection(float yaw)
        {
            Vector3 center = new(0, 0.9f, 0);
            Vector3 eye = center + new Vector3(MathF.Sin(yaw) * MathF.Cos(0.165f), MathF.Sin(0.165f), MathF.Cos(yaw) * MathF.Cos(0.165f)) * 2.7f;
            return Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY) *
                Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(0.5f), width / (float)height, 0.01f, 1000);
        }
        var current = ViewProjection(0.05f); var previous = ViewProjection(0);
        int checkedPixels = 0;
        for (int y = 0; y < height; y += 7) for (int x = 0; x < width; x += 7)
        {
            Float4 surface = surfaces[y, x];
            if (surface.W < 0) continue;
            Vector4 world = new(surface.X, surface.Y, surface.Z, 1);
            Vector4 a = Vector4.Transform(world, current), b = Vector4.Transform(world, previous);
            if (b.W <= 0) continue;
            float mx = (b.X / b.W - a.X / a.W) * width * 0.5f;
            float my = (a.Y / a.W - b.Y / b.W) * height * 0.5f;
            int index = y * width + x;
            Require(MathF.Abs(motion[index * 2] - mx) < 0.02f && MathF.Abs(motion[index * 2 + 1] - my) < 0.02f,
                "Motion vector disagrees with independent CPU view/projection matrices");
            Require(MathF.Abs(depth[index] - a.Z / a.W) < 0.00001f, "Depth disagrees with camera projection");
            checkedPixels++;
        }
        Require(checkedPixels > 50, "Insufficient geometry samples for projection verification");
    }

    /// <summary>Checks that a stationary analytic signal does not advect when the Halton jitter repeats.</summary>
    private static void VerifyTemporalReprojection(GraphicsDevice device, ID3D12Device5 native,
        ID3D12InfoQueue info, ReconstructionStatus status)
    {
        foreach (var mode in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS })
        {
            if (!status.Supports(mode)) continue;
            using var sr = new SuperResolutionRenderer(device);
            sr.Configure(mode, 67, 320, 180);
            Require(sr.Active == mode, sr.Status.Message);
            int width = sr.InputWidth, height = sr.InputHeight;
            using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(320, 180);
            using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
            using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
            using var surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
            // Reuse CPU staging arrays; the production render loop gains no new allocations.
            var radiance = new Float4[height, width];
            var positions = new Float4[height, width];
            normals.CopyFrom(new Rgba32[height, width]);
            float maxDrift = 0;
            for (int frame = 0; frame < 96; frame++)
            {
                Float2 jitter = sr.Jitter();
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    float px = x + 0.5f + jitter.X, py = y + 0.5f + jitter.Y;
                    // Plane at view Z=2. Camera is (0, .9, 2.7), looking along -Z.
                    float wx = (2 * px / width - 1) * width / height;
                    float wy = 1 - 2 * py / height;
                    positions[y, x] = new(wx, 0.9f + wy, 0.7f, MathF.Sqrt(wx * wx + wy * wy + 4));
                    radiance[y, x] = new(0.2f + px * 0.002f, 0.2f + py * 0.003f, 0.4f, 1);
                }
                raw.CopyFrom(radiance); surfaces.CopyFrom(positions);
                Require(sr.Execute(raw, normals, surfaces, target, default, 2.7f, jitter,
                    RayTraceDenoiserMode.TemporalOnly, HdrRenderParameters.Default, 1.0 / 60), sr.Status.Message);
                float[] history = ReadTexture(native, sr.DenoiserHistory, false);
                // A linear radiance field has an exact continuous solution, independent of interpolation implementation.
                for (int y = 16; y < height - 16; y += 11) for (int x = 16; x < width - 16; x += 11)
                {
                    int index = (y * width + x) * 4;
                    maxDrift = Math.Max(maxDrift, Math.Abs(history[index] - radiance[y, x].X) / 0.002f);
                    maxDrift = Math.Max(maxDrift, Math.Abs(history[index + 1] - radiance[y, x].Y) / 0.003f);
                }
            }
            VerifyDebug(info);
            Console.WriteLine($"{mode} stationary temporal history: maximum drift {maxDrift:F6} input pixels over 96 frames");
            Require(maxDrift < 0.02f, "Stationary denoiser history drifts under changing jitter");

            // Adjacent planes have different depths and colors. Interpolation must not mix their histories.
            sr.Reset();
            float maxEdgeError = 0;
            for (int frame = 0; frame < 3; frame++)
            {
                Float2 jitter = frame switch { 0 => new(-0.4f, 0.3f), 1 => new(0.4f, -0.3f), _ => default };
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    float z = x < width / 2 ? 2 : 4;
                    float wx = (2 * (x + 0.5f + jitter.X) / width - 1) * width / height * z * 0.5f;
                    float wy = (1 - 2 * (y + 0.5f + jitter.Y) / height) * z * 0.5f;
                    positions[y, x] = new(wx, 0.9f + wy, 2.7f - z, MathF.Sqrt(wx * wx + wy * wy + z * z));
                    float value = z == 2 ? 0.1f : 0.9f;
                    // The last frame reveals new geometry everywhere: no old depth may contribute.
                    if (frame == 2)
                    {
                        positions[y, x].Z -= 6;
                        // Preserve a wide neighborhood range so color clamping cannot mask stale-history reuse.
                        value = ((x + y) % 3) switch { 0 => 0.1f, 1 => 0.9f, _ => 0.35f };
                    }
                    radiance[y, x] = new(value, value, value, 1);
                }
                raw.CopyFrom(radiance); surfaces.CopyFrom(positions);
                Require(sr.Execute(raw, normals, surfaces, target, default, 2.7f, jitter,
                    RayTraceDenoiserMode.TemporalOnly, HdrRenderParameters.Default, 1.0 / 60), sr.Status.Message);
                float[] history = ReadTexture(native, sr.DenoiserHistory, false);
                // Include the image border and the depth discontinuity, on both sides of the filter footprint.
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    maxEdgeError = Math.Max(maxEdgeError, Math.Abs(history[(y * width + x) * 4] - radiance[y, x].X));
            }
            VerifyDebug(info);
            Require(maxEdgeError < 0.00001f, "Bilinear history leaked across a depth discontinuity or disocclusion");
            Console.WriteLine($"PASS {mode} history depth rejection, disocclusion and image borders");
        }
    }

    /// <summary>Compares the actual NRD output with an independently sampled 512-SPP reference.</summary>
    private static void VerifyNrdQuality(GraphicsDevice device, ID3D12Device5 native, ID3D12InfoQueue info)
    {
        const int width = 320, height = 180;
        using var sr = new SuperResolutionRenderer(device);
        sr.Configure(ReconstructionMode.Off, 100, width, height, true);
        Require(sr.Status.NrdActive, sr.Status.Message);
        using var backend = new DxrMeshBackend(); backend.Initialize(device);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        using var raw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normals = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var surfaces = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var normalRoughness = device.AllocateReadWriteTexture2D<Float4>(width, height);
        MeshData mesh = MeshData.LoadAsync(0).GetAwaiter().GetResult();
        Float2 orbit = new(0, 0.165f);
        void Frame(int frame, Float2 camera, float distance, int bounces = 10)
        {
            backend.Trace(mesh, width, height, 2, bounces, frame, camera, distance, raw, normals, surfaces, default, normalRoughness);
            Require(sr.Execute(raw, normals, surfaces, target, camera, distance, default,
                RayTraceDenoiserMode.NrdRelax, HdrRenderParameters.Default, 1.0 / 60, normalRoughness), sr.Status.Message);
            Require(sr.NrdDispatchCount > 5, "Official NRD dispatch missing");
        }
        for (int frame = 0; frame < 64; frame++) Frame(frame, orbit, 2.7f);
        SavePpm(target, "output/NRD-native-sdr.ppm");
        float[] denoised = ReadTexture(native, sr.DenoisedColor, true);
        float[] viewZ = ReadTexture(native, sr.NrdViewZ, false);
        Float4[,] noisy = raw.ToArray(), geometry = surfaces.ToArray(), normalGuide = normalRoughness.ToArray();
        int shortHits = 0, escapedHits = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (geometry[y, x].W < 0) { Require(viewZ[y * width + x] == -1000000, "Sky entered NRD denoising range"); continue; }
            Require(viewZ[y * width + x] < 0 && viewZ[y * width + x] > -1000, "NRD linear view Z disagrees with RH camera");
            Require(normalGuide[y, x].W == 1 && Math.Max(Math.Abs(normalGuide[y, x].X), Math.Max(Math.Abs(normalGuide[y, x].Y), Math.Abs(normalGuide[y, x].Z))) > 0.999f,
                "NRD normal/roughness encoding invalid");
            Require(noisy[y, x].W > 0 && noisy[y, x].W <= 65504, "Diffuse hit distance is not a positive world-space distance");
            if (noisy[y, x].W < 5) shortHits++;
            if (noisy[y, x].W > 60000) escapedHits++;
        }
        Require(shortHits > 100 && escapedHits > 100, "Diffuse hit distances do not distinguish secondary hits and misses");
        var reference = new Vector3[width * height];
        for (int frame = 0; frame < 32; frame++)
        {
            backend.Trace(mesh, width, height, 16, 10, 10000 + frame, orbit, 2.7f, raw, normals, surfaces, default, normalRoughness);
            var sample = raw.ToArray();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                reference[y * width + x] += new Vector3(sample[y, x].X, sample[y, x].Y, sample[y, x].Z) / 32;
        }
        double rawError = 0, nrdError = 0, expectedEnergy = 0, actualEnergy = 0;
        int count = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (geometry[y, x].W < 0) continue;
            int index = y * width + x;
            Vector3 expected = reference[index], actual = new(denoised[index * 4], denoised[index * 4 + 1], denoised[index * 4 + 2]);
            rawError += Vector3.DistanceSquared(new(noisy[y, x].X, noisy[y, x].Y, noisy[y, x].Z), expected);
            nrdError += Vector3.DistanceSquared(actual, expected);
            expectedEnergy += expected.X + expected.Y + expected.Z; actualEnergy += actual.X + actual.Y + actual.Z;
            count++;
        }
        Console.WriteLine($"NRD vs 512-SPP reference: raw RMSE={Math.Sqrt(rawError / (count * 3)):F6}, NRD RMSE={Math.Sqrt(nrdError / (count * 3)):F6}, energy={actualEnergy / expectedEnergy:F5}");
        Require(nrdError < rawError * 0.8 && actualEnergy / expectedEnergy is > 0.8 and < 1.2, "NRD did not reduce noise or changed material energy");
        for (int frame = 0; frame < 12; frame++) Frame(100 + frame, new(0.2f + frame * 0.01f, 0.25f), 2.2f);
        VerifyPixels(target);
        Require(ReadTexture(native, sr.DenoisedColor, true).All(float.IsFinite), "Nonfinite NRD output while moving camera");
        sr.Reset(); Frame(0, orbit, 2.7f, 1);
        float[] reset = ReadTexture(native, sr.DenoisedColor, true);
        using var fresh = new SuperResolutionRenderer(device);
        fresh.Configure(ReconstructionMode.Off, 100, width, height, true);
        Require(fresh.Execute(raw, normals, surfaces, target, orbit, 2.7f, default,
            RayTraceDenoiserMode.NrdRelax, HdrRenderParameters.Default, 1.0 / 60, normalRoughness), fresh.Status.Message);
        float[] clean = ReadTexture(native, fresh.DenoisedColor, true);
        Require(reset.Zip(clean, (a, b) => Math.Abs(a - b)).Max() < 0.0001f, "NRD reset differs from a new context");
        VerifyDebug(info);
        Console.WriteLine("PASS NRD guide semantics, energy/noise, camera motion/zoom, direct-light-only tracing and reset");
    }

    private static float[] ReadTexture(ID3D12Device5 device, ID3D12Resource source, bool half2,
        ResourceStates sourceState = ResourceStates.NonPixelShaderResource)
    {
        var desc = source.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        int channels = desc.Format is Format.R16G16B16A16_Float or Format.R32G32B32A32_Float ? 4 : half2 ? 2 : 1;
        uint pitch = (uint)((width * channels * (half2 ? 2 : 4) + 255) & ~255);
        using var readback = device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer((ulong)pitch * (uint)height), ResourceStates.CopyDest);
        using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var commands = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        using var fence = device.CreateFence(0);
        commands.ResourceBarrierTransition(source, sourceState, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(readback, new PlacedSubresourceFootPrint
        {
            Footprint = new SubresourceFootPrint(desc.Format, (uint)width, (uint)height, 1, pitch)
        }), 0, 0, 0, new TextureCopyLocation(source, 0));
        commands.ResourceBarrierTransition(source, ResourceStates.CopySource, sourceState);
        commands.Close(); queue.ExecuteCommandList(commands); queue.Signal(fence, 1).CheckError();
        using var done = new AutoResetEvent(false);
        fence.SetEventOnCompletion(1, done.SafeWaitHandle.DangerousGetHandle()).CheckError();
        Require(done.WaitOne(TimeSpan.FromSeconds(30)), "GPU readback timed out");
        byte* address = readback.Map<byte>(0);
        var result = new float[width * height * channels];
        for (int y = 0; y < height; y++) for (int x = 0; x < width * channels; x++)
            result[y * width * channels + x] = half2 ? (float)((Half*)(address + y * pitch))[x] : ((float*)(address + y * pitch))[x];
        readback.Unmap(0);
        return result;
    }

    private static void VerifyDebug(ID3D12InfoQueue queue)
    {
        bool failed = false;
        for (ulong i = 0; i < queue.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = queue.GetMessage(i);
            if (message.Severity is MessageSeverity.Error or MessageSeverity.Corruption)
            {
                if (!failed) Console.Error.WriteLine($"D3D12 {message.Id}: {message.Description}");
                failed = true;
            }
        }
        queue.ClearStoredMessages();
        Require(!failed, "D3D12 debug errors");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
