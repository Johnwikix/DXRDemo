using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using DXRDemo.Hdr;
using DXRDemo.SuperResolution;
using DXRDemo.Shaders.RayTrace;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Float2 = System.Numerics.Vector2;
using Float4 = System.Numerics.Vector4;

/// <summary>Bounded headless single-frame and temporal camera-motion checks, including NRD/DLSS up to 4K.</summary>
internal static class OfflineProbe
{
    internal static async Task Run(string[] args)
    {
        bool cpuOnly = args.Contains("--cpu-only"), convergence = args.Contains("--convergence");
        bool bistro = args.Contains("--bistro");
        if (bistro && convergence) throw new ArgumentException("Choose either --bistro or --convergence.");
        bool contracts = args.Contains("--contracts"), production = args.Contains("--production");
        bool stable = args.Contains("--stable"), nrd = args.Contains("--nrd");
        bool rr = args.Contains("--rr-4k"), motion = args.Contains("--motion");
        bool dlss = args.Contains("--dlss-4k") || rr;
        if (rr && (nrd || stable)) throw new ArgumentException("Ray Reconstruction consumes raw signals; omit --nrd and --stable.");
        if (motion && (rr || contracts || cpuOnly)) throw new ArgumentException("--motion requires the NRD scene path and cannot be combined with --rr-4k, --contracts or --cpu-only.");
        if ((dlss && !rr) || motion) nrd = true;
        if (nrd) stable = true;
        await OfflineFeatureChecks.RunCpu();
        ValidateLightWeights();
        Require(Recompose(new(2, 3, 5, 0), new(7, 11, 13, 1), new(1, 1, 1, 0), new(.5f, 1, 2, 1), new(1, 2, 3, 0)) ==
            new Vector3(15.5f, 36, 70), "CPU signal validation must use scalar arithmetic and remain independent of shader vector operators");
        int Option(string name, int fallback, int maximum)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < 1 || value > maximum)
                throw new ArgumentException($"{name} must be between 1 and {maximum}.");
            return value;
        }
        int width = Option("--width", 256, 3840), height = Option("--height", 144, 2160);
        int samples = Option("--spp", dlss ? 1 : 4, 64), bounces = Option("--bounces", dlss ? 10 : 8, 32);
        int textureLimit = Option("--texture-size", dlss ? 1024 : 256, 4096);
        if (dlss) { width = 1920; height = 1080; }
        var timer = Stopwatch.StartNew();
        // Small checks retain a texture budget; 4K checks match the app's 1024 limit.
        SceneAsset asset;
        if (contracts) { asset = OfflineFeatureChecks.Fixture(); width = 9; height = 4; samples = 1; }
        else
        {
            SceneDocument document = await RtxptAssets.LoadAsync(bistro ? RtxptScene.Bistro : convergence ? RtxptScene.Convergence : RtxptScene.Kitchen,
                maximumTextureSize: textureLimit);
            asset = document.Scenes[document.DefaultScene];
        }
        int environmentArgument = Array.IndexOf(args, "--environment");
        if (environmentArgument >= 0)
        {
            if (environmentArgument + 1 >= args.Length) throw new ArgumentException("--environment requires a Radiance .hdr path.");
            asset = asset with { EnvironmentMap = SceneEnvironment.LoadRadiance(args[environmentArgument + 1], 256) };
        }
        Require(asset.TriangleCount > 0 && asset.Instances.Length > 0, "Empty packaged scene");
        if (args.Contains("--uncompressed")) asset = asset with { Textures = asset.Textures.Select(t => t with { Bc7Mips = null }).ToArray() };
        Require(asset.Textures.All(t => t.Width <= textureLimit && t.Height <= textureLimit), "Offline texture budget exceeded");
        Require(Marshal.SizeOf<SceneMaterial>() == 432 && Marshal.SizeOf<SceneVertex>() == 80 && Marshal.SizeOf<NativeNrd.Frame>() == 384, "Scene/NRD ABI changed");
        var distribution = new SceneLightDistribution(asset);
        double mass = distribution.Emitters.Sum(e => (double)e.Meta.X * e.E1.W);
        if (distribution.Emitters.Length > 0)
        {
            Require(Math.Abs(mass - 1) < 1e-5, "Emitter PDF is not normalized");
            Require(distribution.Emitters[^1].Meta.Y == 1, "Emitter CDF does not end at one");
            float previous = 0;
            foreach (var emitter in distribution.Emitters)
            {
                Require(emitter.Meta.Y >= previous && (emitter.E1.W > 0 || emitter.Meta.X == 0), "Invalid emitter CDF/PDF");
                Require(emitter.Meta.X == 0 || emitter.E1.W == distribution.InstanceAreaPdf[(int)emitter.Meta.Z], "NEE and hit-emission PDFs disagree");
                previous = emitter.Meta.Y;
            }
            var aliasMass = new double[distribution.Emitters.Length];
            for (int i = 0; i < aliasMass.Length; i++)
            {
                var emitter = distribution.Emitters[i];
                Require(emitter.P0.W >= 0 && emitter.P0.W <= 1 && emitter.E2.W >= 0 && emitter.E2.W < aliasMass.Length, "Invalid emitter alias.");
                aliasMass[i] += emitter.P0.W / aliasMass.Length;
                aliasMass[(int)emitter.E2.W] += (1 - emitter.P0.W) / aliasMass.Length;
            }
            for (int i = 0; i < aliasMass.Length; i++)
                Require(Math.Abs(aliasMass[i] - distribution.Emitters[i].Meta.X * distribution.Emitters[i].E1.W) < 1e-6, "Emitter alias and hit-light PDFs disagree.");
        }
        var controller = new CameraController(); controller.FrameScene(asset, width / (float)height);
        CameraFrame camera = controller.Advance(0, out _, out _);
        Require(asset.DefaultCamera is { } authored && Vector3.Distance(camera.Origin, authored.Origin) < 1e-4f &&
            Vector3.Distance(camera.Forward, authored.Forward) < 1e-4f && camera.VerticalFov == authored.VerticalFov, "Authored camera not applied");
        controller.Rotate(.2f, .1f); controller.Reset();
        Require(controller.Advance(0, out _, out _) == camera, "Scene camera reset failed");
        foreach (string warning in asset.Warnings) Console.WriteLine("Asset warning: " + warning);
        Console.WriteLine($"CPU validated {asset.Name}: {asset.TriangleCount:N0} triangles, {asset.Instances.Length} instances, {asset.Textures.Length} texture views ({asset.Textures.Count(t => t.Bc7Mips != null)} BC7), {distribution.Emitters.Length} emitters.");
        if (cpuOnly) { Console.WriteLine("PASS: CPU assets, light PDFs and authored camera. GPU dispatches: 0."); return; }

        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native = NativeDevice(device);
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend { ReferenceMode = !production, CausticsEnabled = false,
            UseInlineTraversal = !args.Contains("--no-ser") && !args.Contains("--legacy-trace"),
            ReorderThreads = args.Contains("--reorder") && !args.Contains("--no-reorder"),
            ShaderLibraryFile = contracts ? "ContractTracer.hlsl" : "SceneTrace.hlsl" };
        backend.Initialize(device); backend.SetScene(asset);
        Console.WriteLine($"Runtime: {backend.ActualShaderModel}, SER active: {backend.SerActive}");
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normals = device.CreateTexture2D<Rgba32>(width, height);
        using var surfaces = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        using var reconstruction = (nrd || rr) && !contracts ? new SuperResolutionRenderer(device) : null;
        using var target = reconstruction != null ? device.CreateTexture2D<Rgba64>(dlss ? 3840 : width, dlss ? 2160 : height) : null;
        float exposureStops = RtxptAssets.InitialExposure(bistro ? RtxptScene.Bistro : convergence ? RtxptScene.Convergence : RtxptScene.Kitchen);
        if (reconstruction != null)
        {
            var mode = rr ? ReconstructionMode.DlssRayReconstruction : dlss ? ReconstructionMode.Dlss : ReconstructionMode.Off;
            reconstruction.Configure(mode, dlss ? 50 : 100,
                dlss ? 3840 : width, dlss ? 2160 : height, nrd, true);
            if (dlss) Require(reconstruction.Status.Active == mode && reconstruction.InputWidth == width && reconstruction.InputHeight == height,
                "Explicit 4K DLSS / 50% input configuration unavailable.");
            if (nrd) Require(reconstruction.Status.NrdActive, "NRD is unavailable for the explicit --nrd check.");
            reconstruction.PrepareStablePlanes();
        }
        double setupMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (motion)
        {
            const int warmup = 16, orbitEnd = 32, panEnd = 40, fovEnd = 56;
            int frameCount = Option("--motion-frames", 64, 256);
            if (frameCount <= fovEnd) throw new ArgumentException("--motion-frames must be at least 57 to cover every camera phase, including stopped history recovery.");
            string motionName = "";
            int motionNameArgument = Array.IndexOf(args, "--motion-name");
            if (motionNameArgument >= 0)
            {
                if (motionNameArgument + 1 >= args.Length) throw new ArgumentException("--motion-name requires a file-safe label.");
                motionName = args[motionNameArgument + 1];
                if (motionName.Length == 0 || motionName.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new ArgumentException("--motion-name requires a file-safe label.");
            }
            string motionDirectory = Path.GetFullPath("output/rtxpt-motion" + (motionName.Length > 0 ? "-" + motionName : ""));
            Directory.CreateDirectory(motionDirectory);
            CameraFrame previous = camera;
            float fovScale = 1;
            double maxMotionError = 0;
            var frames = new List<object>();
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (frame >= warmup && frame < orbitEnd) controller.Rotate(.015f, .0015f);
                else if (frame >= orbitEnd && frame < panEnd) controller.Pan(.0015f, -.0005f);
                else if (frame >= panEnd && frame < fovEnd) fovScale *= 1.015f;
                CameraFrame current = controller.Advance(0, out _, out _) with { VerticalFov = camera.VerticalFov * fovScale };
                Float2 jitter = reconstruction!.Jitter();
                bool reset = reconstruction.HistoryReset;
                backend.Trace(current, frame, samples, bounces, jitter, 1, raw, normals, surfaces, guide, signals,
                    rayReconstruction: false, opaqueForNrd: true);
                var motionTimer = Stopwatch.StartNew();
                Require(reconstruction.Execute(raw, normals, surfaces, target!, default, 3, jitter, RayTraceDenoiserMode.NrdRelax,
                    HdrRenderParameters.Default, 1.0 / 60, guide, current, signals, exposure: exposureStops, transparentBackend: backend),
                    reconstruction.Status.Message);
                Require(reconstruction.Status.NrdActive && reconstruction.NrdDispatchCount > 5, "Motion sequence silently fell back from NRD.");
                uint expectedHistory = !reset && current != previous ? NativeNrd.MotionMaxAccumulatedFrames : NativeNrd.DefaultMaxAccumulatedFrames;
                Require(reconstruction.NrdMaxAccumulatedFrames == expectedHistory, "NRD history length did not follow camera motion.");
                double motionReconstructionMilliseconds = motionTimer.Elapsed.TotalMilliseconds;
                double? motionError = null, stableMotionError = null;
                int? stableMotionSamples = null;
                if (frame is warmup or orbitEnd or panEnd)
                {
                    float[] vectors = ReconstructionProbe.ReadTexture(native, reconstruction.Motion, true);
                    motionError = MeasureMotionError(current, previous, surfaces.ToArray(), vectors, width, height);
                    Require(backend.StablePlanesActive && reconstruction.StableMotion != null, "Motion sequence did not execute stable-plane NRD.");
                    float[] stableVectors = ReconstructionProbe.ReadTexture(native, reconstruction.StableMotion!, true);
                    stableMotionError = MeasureMotionError(current, previous, signals.StableTransmissionSurface.ToArray(), stableVectors, width, height, out int stableSamples);
                    stableMotionSamples = stableSamples;
                    Require(stableSamples > 0, "Stable transmission motion was not exercised by the motion sequence.");
                    motionError = Math.Max(motionError.Value, stableMotionError.Value);
                    maxMotionError = Math.Max(maxMotionError, motionError.Value);
                }
                string file = Path.Combine(motionDirectory, $"frame-{frame:D3}.png");
                SceneProbe.Save(target!, file);
                frames.Add(new { frame, phase = frame < warmup ? "warmup" : frame < orbitEnd ? "orbit" : frame < panEnd ? "pan" : frame < fovEnd ? "fov" : "stopped",
                    reset, maxAccumulatedFrames = reconstruction.NrdMaxAccumulatedFrames, jitterX = jitter.X, jitterY = jitter.Y, origin = current.Origin, forward = current.Forward,
                    verticalFov = current.VerticalFov, motionError, stableMotionError, stableMotionSamples, traceGpuMilliseconds = backend.TraceGpuMilliseconds,
                    reconstructionGpuMilliseconds = reconstruction.GpuMilliseconds, reconstructionMilliseconds = motionReconstructionMilliseconds });
                Console.WriteLine($"Motion frame {frame:D2}/{frameCount - 1}: motion error = {(motionError.HasValue ? motionError.Value.ToString("F4") : "not sampled")} px");
                previous = current;
            }
            for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
            {
                var message = messages.GetMessage(i);
                Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
            }
            Require(backend.TraceDispatchCount == frameCount && backend.PhotonBuildCount == 0, "Motion sequence frame contract violated");
            var motionResult = new { scene = asset.Name, gpu = GpuName(device), width, height, outputWidth = target!.Width, outputHeight = target.Height,
                samples, bounces, textureLimit, frames = frameCount, stablePlanes = true, nrd = true, dlss,
                temporalValidation = true, maxMotionError, frameDetails = frames };
            string motionJson = JsonSerializer.Serialize(motionResult, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(motionDirectory, "motion.json"), motionJson);
            Console.WriteLine(motionJson);
            Require(maxMotionError < .25, $"Camera motion vectors disagree with the previous camera by {maxMotionError:F4} pixels.");
            Console.WriteLine($"PASS: {frameCount} offline NRD motion frames. {motionDirectory}");
            return;
        }
        // The only DispatchRays frame in the single-frame command. No warmup, history, photon pass or NRC.
        backend.Trace(camera, 0, samples, bounces, default, 1, raw, normals, surfaces, guide, signals, rayReconstruction: rr, opaqueForNrd: stable);
        double frameMilliseconds = timer.Elapsed.TotalMilliseconds - setupMilliseconds;
        double reconstructionMilliseconds = 0, reconstructionGpuMilliseconds = 0;
        // Keep GPU stages adjacent, as in the app. No image readback, CPU pixel
        // validation, pipeline compilation or extra frame between these stages.
        if (reconstruction != null)
        {
            var reconstructionTimer = Stopwatch.StartNew();
            Require(reconstruction.Execute(raw, normals, surfaces, target!, default, 3, default, RayTraceDenoiserMode.NrdRelax,
                HdrRenderParameters.Default, 1.0 / 30, guide, camera, signals, exposure: exposureStops, transparentBackend: backend), reconstruction.Status.Message);
            reconstructionMilliseconds = reconstructionTimer.Elapsed.TotalMilliseconds;
            reconstructionGpuMilliseconds = reconstruction.GpuMilliseconds;
        }
        Require(backend.TraceDispatchCount == 1 && backend.PhotonBuildCount == 0, "Single-frame contract violated");
        if (contracts)
        {
            Float4[,] results = raw.ToArray();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                Require(results[y, x].X == 1, $"GPU contract ({x},{y}) failed: error={results[y, x].Y}, details={results[y, x].Z}/{results[y, x].W}");
            for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
            {
                var message = messages.GetMessage(i);
                Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
            }
            Console.WriteLine("PASS: 36 GPU contracts in ONE dispatch: materials, media, HDR/light PDF, TIR, delta BSDF, culling/mirrored traversal, near-delta GGX and large-PDF MIS.");
            return;
        }
        Float4[,] pixels = raw.ToArray(), hits = surfaces.ToArray(), d = signals.Diffuse.ToArray(), s = signals.Specular.ToArray();
        Float4[,] df = signals.DiffuseFactor.ToArray(), sf = signals.SpecularFactor.ToArray(), e = signals.Unfiltered.ToArray();
        Float4[,] reflection = signals.StableReflection.ToArray(), transmission = signals.StableTransmission.ToArray();
        Float4[,] reflectionSurface = signals.StableReflectionSurface.ToArray(), transmissionSurface = signals.StableTransmissionSurface.ToArray();
        double energy = 0, maxRecompositionError = 0; int hitPixels = 0, invalidSignals = 0;
        int reflectionPixels = 0, transmissionPixels = 0, virtualGuidePixels = 0;
        byte[] rgba = new byte[width * height * 4];
        float exposure = MathF.Pow(2, exposureStops);
        static byte Encode(float linear)
        {
            float mapped = linear / (1 + linear);
            float srgb = mapped <= .0031308f ? 12.92f * mapped : 1.055f * MathF.Pow(mapped, 1 / 2.4f) - .055f;
            return (byte)Math.Clamp(MathF.Round(srgb * 255), 0, 255);
        }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            Float4 p = pixels[y, x];
            Require(float.IsFinite(p.X + p.Y + p.Z) && p.X >= 0 && p.Y >= 0 && p.Z >= 0, "Non-finite/negative radiance");
            energy += p.X + p.Y + p.Z;
            if (hits[y, x].W > 0) hitPixels++;
            Vector3 recomposed = Recompose(d[y, x], df[y, x], s[y, x], sf[y, x], e[y, x]);
            Float4 pr = reflection[y, x], pt = transmission[y, x];
            recomposed += new Vector3(pr.X + pt.X, pr.Y + pt.Y, pr.Z + pt.Z);
            if (pr.X + pr.Y + pr.Z > 0) reflectionPixels++;
            if (pt.X + pt.Y + pt.Z > 0) transmissionPixels++;
            if (reflectionSurface[y, x].W > 0 || transmissionSurface[y, x].W > 0) virtualGuidePixels++;
            float error = MathF.Max(MathF.Abs(recomposed.X - p.X), MathF.Max(MathF.Abs(recomposed.Y - p.Y), MathF.Abs(recomposed.Z - p.Z)));
            if (!float.IsFinite(error)) invalidSignals++;
            else maxRecompositionError = Math.Max(maxRecompositionError, error / Math.Max(1, Math.Max(p.X, Math.Max(p.Y, p.Z))));
            int offset = (y * width + x) * 4;
            rgba[offset] = Encode(p.X * exposure); rgba[offset + 1] = Encode(p.Y * exposure); rgba[offset + 2] = Encode(p.Z * exposure); rgba[offset + 3] = 255;
        }
        Require(energy > .001 && hitPixels > width * height / 20, "Empty/black camera view");
        if (stable) Require(reflectionPixels + transmissionPixels > 0, "Stable-plane test did not exercise a smooth branch.");
        byte[]? denoisedRgba = null;
        byte[]? outputRgba = null;
        if (reconstruction != null)
        {
            if (dlss)
            {
                Rgba64[,] output = target!.ToArray();
                ReadOnlySpan<ushort> channels = MemoryMarshal.Cast<Rgba64, ushort>(
                    MemoryMarshal.CreateReadOnlySpan(ref output[0, 0], output.Length));
                outputRgba = new byte[output.Length * 4];
                long outputEnergy = 0;
                for (int i = 0; i < outputRgba.Length; i++)
                {
                    outputRgba[i] = (byte)(channels[i] >> 8);
                    if ((i & 3) != 3) outputEnergy += channels[i];
                }
                Require(outputEnergy > 0, "4K DLSS output is black.");
            }
            if (nrd)
            {
                float[] denoised = ReconstructionProbe.ReadTexture(native, reconstruction.DenoisedColor, true, ResourceStates.NonPixelShaderResource);
                double sum = 0; denoisedRgba = new byte[rgba.Length];
                for (int p = 0; p < width * height; p++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float value = denoised[p * 4 + c];
                        Require(float.IsFinite(value) && value >= 0, "Non-finite/negative stable NRD output."); sum += value;
                        denoisedRgba[p * 4 + c] = Encode(value * exposure);
                    }
                    denoisedRgba[p * 4 + 3] = 255;
                }
                Require(sum > 0, "Stable NRD composition is black.");
                Console.WriteLine("PASS: single-frame NRD primary + independent reflected/transmitted histories and composition.");
            }
            else
            {
                float[] reconstructed = ReconstructionProbe.ReadTexture(native, reconstruction.LinearOutput, true, ResourceStates.UnorderedAccess);
                Require(reconstructed.All(float.IsFinite), "Non-finite DLSS Ray Reconstruction output.");
                Console.WriteLine("PASS: single-frame DLSS Ray Reconstruction with finite 4K output.");
            }
        }
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        string directory = Path.GetFullPath("output/rtxpt-offline"); Directory.CreateDirectory(directory);
        string name = (bistro ? "bistro" : convergence ? "convergence" : "kitchen") + (production ? "-production" : "") + (asset.EnvironmentMap != null ? "-hdr" : "") + (stable ? "-planes" : "") + (rr ? "-rr" : "");
        SceneProbe.WritePng(Path.Combine(directory, name + ".png"), width, height, rgba);
        if (denoisedRgba != null) SceneProbe.WritePng(Path.Combine(directory, name + "-nrd.png"), width, height, denoisedRgba);
        if (outputRgba != null) SceneProbe.WritePng(Path.Combine(directory, name + "-4k.png"), 3840, 2160, outputRgba);
        File.WriteAllBytes(Path.Combine(directory, name + ".rgba32f"), MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref pixels[0, 0], pixels.Length)).ToArray());
        var result = new { scene = asset.Name, gpu = GpuName(device), width, height, samples, bounces, exposureStops,
            textureLimit, compressedTextures = asset.Textures.Count(t => t.Bc7Mips != null),
            textureGpuBytes = asset.Textures.Sum(t => (t.Bc7Mips ?? t.Mips).Sum(m => (long)m.Length)),
            frames = backend.TraceDispatchCount, photonPasses = backend.PhotonBuildCount,
            referenceMode = backend.ReferenceMode, adaptiveLighting = production, hdrEnvironment = asset.EnvironmentMap != null,
            stablePlanes = stable, nrd, rayReconstruction = rr, reflectionPixels, transmissionPixels, virtualGuidePixels,
            meanRadiance = energy / (width * height * 3), hitPixels, invalidSignals, maxRecompositionError,
            setupMilliseconds, frameMilliseconds, traceGpuMilliseconds = backend.TraceGpuMilliseconds, reconstructionMilliseconds,
            reconstructionGpuMilliseconds, inlineTraversal = backend.InlineTraversalActive, serRequested = backend.ReorderThreads, serActive = backend.SerActive,
            shaderModel = backend.ActualShaderModel, outputWidth = dlss ? 3840 : width, outputHeight = dlss ? 2160 : height, dlss,
            targetMilliseconds = 1000.0 / 30, measuredGpuMilliseconds = backend.TraceGpuMilliseconds + reconstructionGpuMilliseconds,
            withinGpuBudget = backend.TraceGpuMilliseconds + reconstructionGpuMilliseconds <= 1000.0 / 30,
            debugErrors = 0, temporalValidation = false };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
        Console.WriteLine(json);
        Require(invalidSignals == 0 && maxRecompositionError < 1e-4, "NRD lobe signal recomposition disagrees with raw radiance");
        Console.WriteLine($"PASS: one offline frame. {Path.Combine(directory, name + ".png")}");
    }

    private static double MeasureMotionError(CameraFrame current, CameraFrame previous, Float4[,] surfaces, float[] motion, int width, int height)
    {
        double maximum = MeasureMotionError(current, previous, surfaces, motion, width, height, out int checkedPixels);
        Require(checkedPixels > 100, "Insufficient geometry samples for motion-sequence projection verification");
        return maximum;
    }

    private static double MeasureMotionError(CameraFrame current, CameraFrame previous, Float4[,] surfaces, float[] motion, int width, int height, out int checkedPixels)
    {
        static Matrix4x4 ViewProjection(CameraFrame camera, int width, int height) =>
            Matrix4x4.CreateLookAt(camera.Origin, camera.Origin + camera.Forward, camera.Up) *
            Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov, width / (float)height, camera.Near, camera.Far);
        Matrix4x4 currentVp = ViewProjection(current, width, height), previousVp = ViewProjection(previous, width, height);
        double maximum = 0;
        checkedPixels = 0;
        int stepX = Math.Max(1, width / 64), stepY = Math.Max(1, height / 64);
        for (int y = 0; y < height; y += stepY) for (int x = 0; x < width; x += stepX)
        {
            Float4 surface = surfaces[y, x];
            if (surface.W < 0) continue;
            Vector4 world = new(surface.X, surface.Y, surface.Z, 1);
            Vector4 a = Vector4.Transform(world, currentVp), b = Vector4.Transform(world, previousVp);
            if (a.W <= 0 || b.W <= 0) continue;
            float expectedX = (b.X / b.W - a.X / a.W) * width * .5f;
            float expectedY = (a.Y / a.W - b.Y / b.W) * height * .5f;
            int index = (y * width + x) * 2;
            double error = Math.Max(Math.Abs(motion[index] - expectedX), Math.Abs(motion[index + 1] - expectedY));
            maximum = Math.Max(maximum, error);
            checkedPixels++;
        }
        return maximum;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    // Keep CPU signal validation scalar and independent of the shader's vector arithmetic.
    // Read individual components so this remains an independent check of the signal contract.
    private static Vector3 Recompose(Float4 d, Float4 df, Float4 s, Float4 sf, Float4 e) =>
        new(d.X * df.X + s.X * sf.X + e.X, d.Y * df.Y + s.Y * sf.Y + e.Y, d.Z * df.Z + s.Z * sf.Z + e.Z);
    private static void ValidateLightWeights()
    {
        var dim = SceneMaterial.Default; dim.Emissive = new(1, 1, 1, 0);
        var bright = SceneMaterial.Default; bright.Emissive = new(9, 9, 9, 0);
        SceneVertex[] vertices = [new() { Position = new(0, 0, 0, 1) },
            new() { Position = new(1, 0, 0, 1) }, new() { Position = new(0, 1, 0, 1) }];
        var fixture = new SceneAsset("power fixture", [new("dim", vertices, [0, 1, 2], 0), new("bright", vertices, [0, 1, 2], 1)],
            [new("dim", 0, Matrix4x4.Identity), new("bright", 1, Matrix4x4.CreateTranslation(0, 0, 1))],
            [dim, bright], [], [], Vector3.Zero, Vector3.One, []);
        var distribution = new SceneLightDistribution(fixture);
        Require(MathF.Abs(distribution.Emitters[0].Meta.Y - .1f) < 1e-6f, "Equal-area lights must be selected in proportion to emitted power");
        Require(MathF.Abs(distribution.InstanceAreaPdf[1] / distribution.InstanceAreaPdf[0] - 9) < 1e-5f, "Hit-light PDFs lost the power distribution");
    }
    private static ID3D12Device5 NativeDevice(DxrDevice device) => device.Device.QueryInterface<ID3D12Device5>();

    private static unsafe string GpuName(DxrDevice device)
    {
        long luid = device.Device.AdapterLuid;
        using var adapter = device.DxgiFactory.EnumAdapterByLuid<IDXGIAdapter1>(*(Vortice.Luid*)&luid);
        return adapter.Description1.Description;
    }
}
