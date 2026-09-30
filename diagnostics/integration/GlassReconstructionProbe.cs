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

/// <summary>Checks detail, temporal stability and glass motion on the production NRD and SR pipelines.</summary>
internal static class GlassReconstructionProbe
{
    internal static unsafe void Run()
    {
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); layer.EnableDebugLayer();
        using var device = GraphicsDevice.GetDefault();
        Guid iid = typeof(ID3D12Device5).GUID; nint pointer;
        InteropServices.GetID3D12Device(device, &iid, (void**)&pointer);
        using var native = new ID3D12Device5(pointer);
        using var debug = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend(); backend.Initialize(device);
        const int width = 256, height = 160;
        var view = CameraFrame.Look(new(0, 0, 3), new(0, 0, -1), .7f, .01f, 100);
        SceneAsset asset = Fixture();
        var opaque = asset with { Instances = asset.Instances[..^2] };
        using var referenceRaw = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var referenceNormal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);
        using var referenceSurface = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var referenceGuide = device.AllocateReadWriteTexture2D<Float4>(width, height);
        using var referenceSignals = new PbrSignals(device, width, height);
        backend.SetScene(opaque);
        backend.Trace(view, 0, 1, 8, default, 0, referenceRaw, referenceNormal, referenceSurface, referenceGuide, referenceSignals);
        Float4[,] reference = referenceRaw.ToArray();
        backend.SetScene(asset);
        using var sr = new SuperResolutionRenderer(device);
        using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        Directory.CreateDirectory("output/glass-sr");

        void Case(ReconstructionMode mode, int scale)
        {
            sr.Configure(mode, scale, width, height, true, true);
            Require(sr.Status.NrdActive && sr.Active == mode, sr.Status.Message);
            int w = sr.InputWidth, h = sr.InputHeight;
            using var raw = device.AllocateReadWriteTexture2D<Float4>(w, h);
            using var normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(w, h);
            using var surface = device.AllocateReadWriteTexture2D<Float4>(w, h);
            using var guide = device.AllocateReadWriteTexture2D<Float4>(w, h);
            using var signals = new PbrSignals(device, w, h);
            double[] sum = new double[width * height], squares = new double[width * height];
            const int frames = 48, measured = 12;
            void Frame(int index, CameraFrame camera)
            {
                Float2 jitter = sr.Jitter();
                backend.Trace(camera, index, 2, 8, jitter, 0, raw, normal, surface, guide, signals, opaqueForNrd: true);
                Require(sr.Execute(raw, normal, surface, target, default, 3, jitter, RayTraceDenoiserMode.NrdRelax,
                    HdrRenderParameters.Default, 1.0 / 60, guide, camera, signals, transparentBackend: backend), sr.Status.Message);
                Require(sr.Status.NrdActive && sr.NrdDispatchCount > 5, "Glass case silently fell back from NRD");
            }
            for (int frame = 0; frame < frames; frame++)
            {
                Frame(frame, view);
                if (frame < frames - measured) continue;
                float[] pixels = ReconstructionProbe.ReadTexture(native,
                    mode == ReconstructionMode.Off ? sr.DenoisedColor : sr.LinearOutput, true,
                    mode == ReconstructionMode.Off ? ResourceStates.NonPixelShaderResource : ResourceStates.UnorderedAccess);
                Require(pixels.All(float.IsFinite), "Non-finite glass reconstruction");
                for (int p = 0; p < sum.Length; p++) { double value = pixels[p * 4]; sum[p] += value; squares[p] += value * value; }
            }
            double bright = 0, dark = 0, variance = 0; int brightCount = 0, darkCount = 0;
            for (int y = height / 3; y < height * 2 / 3; y++) for (int x = width / 4; x < width * 3 / 4; x++)
            {
                float expected = reference[y, x].X;
                // Measure stripe interiors; an antialiasing footprint at a real edge is expected.
                if (reference[y, x - 1].X != expected || reference[y, x + 1].X != expected) continue;
                int p = y * width + x; double mean = sum[p] / measured;
                variance += Math.Max(0, squares[p] / measured - mean * mean);
                if (expected > .8f) { bright += mean; brightCount++; } else { dark += mean; darkCount++; }
            }
            Require(brightCount > 100 && darkCount > 100, "Glass detail fixture is empty");
            double contrast = (bright / brightCount - dark / darkCount) / (1 - .65);
            double fluctuation = Math.Sqrt(variance / (brightCount + darkCount));
            Console.WriteLine($"Glass {mode} {scale}%: contrast={contrast:F5}, frame RMS={fluctuation:F5}, dispatches={sr.NrdDispatchCount}");
            SceneProbe.Save(target, $"output/glass-sr/{mode}-{scale}.png");
            Require(contrast is > .80 and < 1.2, "NRD/SR erased detail behind glass or changed its energy");
            Require(fluctuation < .04, "Static glass shimmers across the jitter sequence");
            float[] accumulation = ReconstructionProbe.ReadTexture(native, sr.GlassAccumulation, false);
            Float4[,] opaqueGuide = surface.ToArray(), glassGuide = signals.GlassSurface.ToArray();
            int cx = w / 2, cy = h / 2;
            Require(accumulation[(cy * w + cx) * 4 + 3] == frames, "SR sampling jitter resets stationary glass accumulation");
            Require(Math.Abs(opaqueGuide[cy, cx].Z) < .001 && Math.Abs(glassGuide[cy, cx].Z - 1) < .001 && glassGuide[cy, cx].W < -1,
                "Opaque NRD guides and closest-glass SR guides are not separated");
            Frame(frames, view with { Origin = view.Origin + new Vector3(.01f, 0, 0) });
            float[] motion = ReconstructionProbe.ReadTexture(native, sr.Motion, true);
            float expectedMotion = .01f * h / (4 * MathF.Tan(view.VerticalFov * .5f));
            Require(Math.Abs(motion[(cy * w + cx) * 2] - expectedMotion) < .015f, "SR motion follows the background instead of the closest glass");
            void FreshGlass()
            {
                var history = ReconstructionProbe.ReadTexture(native, sr.GlassAccumulation, false);
                var rawGlass = ReconstructionProbe.ReadTexture(native, sr.GlassUnfilteredColor, true);
                var resolvedGlass = ReconstructionProbe.ReadTexture(native, sr.DenoisedColor, true);
                Require(history[(cy * w + cx) * 4 + 3] == 1, "Camera change retained glass sample count");
                Require(rawGlass.Zip(resolvedGlass, (a, b) => Math.Abs(a - b)).Max() < .001, "Camera change retained glass radiance");
            }
            FreshGlass();
            CameraFrame changed = view with { Origin = view.Origin + new Vector3(.000001f, 0, 0) };
            Frame(frames + 1, changed); FreshGlass();
            Frame(frames + 2, changed);
            accumulation = ReconstructionProbe.ReadTexture(native, sr.GlassAccumulation, false);
            Require(accumulation[(cy * w + cx) * 4 + 3] == 2, "Glass did not resume accumulation after camera stopped");
            changed = CameraFrame.Look(changed.Origin, new(.001f, 0, -1), view.VerticalFov, view.Near, view.Far);
            Frame(frames + 3, changed); FreshGlass();
            changed = changed with { VerticalFov = changed.VerticalFov + .001f };
            Frame(frames + 4, changed); FreshGlass();
            var roll = Matrix4x4.CreateRotationZ(.001f);
            changed = changed with { Right = Vector3.TransformNormal(changed.Right, roll), Up = Vector3.TransformNormal(changed.Up, roll) };
            Frame(frames + 5, changed); FreshGlass();
            changed = changed with { Near = .02f, Far = 101 };
            Frame(frames + 6, changed); FreshGlass();
            sr.Reset(); Frame(0, view);
            FreshGlass();
            for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
            {
                var message = debug.GetMessage(i);
                Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
            }
            debug.ClearStoredMessages();
        }

        // Negative control: deliberately denoise transmission against the pane's guides.
        // This isolates the original integration error from NRD SDK availability and SR sharpness.
        sr.Configure(ReconstructionMode.Off, 100, width, height, true, true);
        for (int frame = 0; frame < 48; frame++)
        {
            backend.Trace(view, frame, 2, 8, default, 0, referenceRaw, referenceNormal, referenceSurface, referenceGuide, referenceSignals);
            Require(sr.Execute(referenceRaw, referenceNormal, referenceSurface, target, default, 3, default, RayTraceDenoiserMode.NrdRelax,
                HdrRenderParameters.Default, 1.0 / 60, referenceGuide, view, referenceSignals), sr.Status.Message);
        }
        float[] broken = ReconstructionProbe.ReadTexture(native, sr.DenoisedColor, true);
        double brokenBright = 0, brokenDark = 0; int controlBright = 0, controlDark = 0;
        for (int y = height / 3; y < height * 2 / 3; y++) for (int x = width / 4; x < width * 3 / 4; x++)
        {
            float expected = reference[y, x].X;
            if (reference[y, x - 1].X != expected || reference[y, x + 1].X != expected) continue;
            if (expected > .8f) { brokenBright += broken[(y * width + x) * 4]; controlBright++; }
            else { brokenDark += broken[(y * width + x) * 4]; controlDark++; }
        }
        double brokenContrast = (brokenBright / controlBright - brokenDark / controlDark) / (1 - .65);
        Console.WriteLine($"Glass incorrect-guide control: contrast={brokenContrast:F5}");
        Require(brokenContrast < .80, "Regression fixture no longer reproduces wrong-guide glass blur");
        SceneProbe.Save(target, "output/glass-sr/incorrect-guides.png");
        sr.Reset();
        Case(ReconstructionMode.Off, 100);
        foreach (var mode in new[] { ReconstructionMode.Fsr, ReconstructionMode.XeSS, ReconstructionMode.Dlss })
        {
            if (!sr.Status.Supports(mode)) { Console.WriteLine($"SKIP unsupported glass SR provider: {mode}"); continue; }
            Case(mode, 67); Case(mode, 100);
        }

        // Transmission factors alone must not hide opaque texels or metallic surfaces.
        var patternedMeshes = (SceneMesh[])asset.Meshes.Clone();
        for (int mesh = 48; mesh < 50; mesh++)
        {
            var vertices = (SceneVertex[])patternedMeshes[mesh].Vertices.Clone();
            for (int i = 0; i < vertices.Length; i++) vertices[i].TexCoord = new(vertices[i].Position.X > 0 ? 1 : 0, 0, 0, 0);
            patternedMeshes[mesh] = patternedMeshes[mesh] with { Vertices = vertices };
        }
        var patternedMaterials = (SceneMaterial[])asset.Materials.Clone();
        patternedMaterials[2].TransmissionMap = TextureBinding.Missing;
        patternedMaterials[2].TransmissionMap.Info.X = 0;
        var patternedAsset = asset with
        {
            Meshes = patternedMeshes, Materials = patternedMaterials,
            Textures = [new("Split transmission", 2, 1, [[0, 0, 0, 255, 255, 255, 255, 255]], false, 33071, 33071, 9728, 9728)]
        };
        backend.SetScene(patternedAsset);
        sr.Configure(ReconstructionMode.Off, 100, width, height, true, true); sr.Reset();
        backend.Trace(view, 0, 2, 8, default, 0, referenceRaw, referenceNormal, referenceSurface, referenceGuide, referenceSignals, opaqueForNrd: true);
        var splitGuide = referenceSurface.ToArray();
        Require(Math.Abs(splitGuide[height / 2, width * 3 / 8].Z - 1) < .001 && Math.Abs(splitGuide[height / 2, width * 5 / 8].Z) < .001,
            "NRD primary rays skipped opaque transmission-map texels");
        Require(sr.Execute(referenceRaw, referenceNormal, referenceSurface, target, default, 3, default, RayTraceDenoiserMode.NrdRelax,
            HdrRenderParameters.Default, 1.0 / 60, referenceGuide, view, referenceSignals, transparentBackend: backend), sr.Status.Message);
        var splitGlass = referenceSignals.GlassSurface.ToArray();
        Require(splitGlass[height / 2, width * 3 / 8].W > 0 && splitGlass[height / 2, width * 5 / 8].W < -1,
            "Glass composition ignored the transmission texture");
        var metalMaterials = (SceneMaterial[])patternedMaterials.Clone(); metalMaterials[2].Factors.X = 1;
        backend.SetScene(patternedAsset with { Materials = metalMaterials });
        backend.Trace(view, 0, 2, 8, default, 0, referenceRaw, referenceNormal, referenceSurface, referenceGuide, referenceSignals, opaqueForNrd: true);
        var metalGuide = referenceSurface.ToArray();
        Require(Math.Abs(metalGuide[height / 2, width * 5 / 8].Z - 1) < .001, "NRD primary rays skipped a metallic non-transmitting surface");
        for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = debug.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        Console.WriteLine("PASS transmission-map opaque texels and metallic surface remain in NRD guides.");
        Console.WriteLine("PASS glass detail, jitter stability, separate guides, closest-glass motion, camera translation/rotation/roll/FOV/projection resets, resumed accumulation and SR resize; no D3D12 errors.");
    }

    private static SceneAsset Fixture()
    {
        var materials = new SceneMaterial[3];
        for (int i = 0; i < 2; i++)
        {
            materials[i] = SceneMaterial.Default; materials[i].BaseColor = new(i == 0 ? new Vector3(1) : new Vector3(.65f), 1);
            materials[i].Factors = new(0, .8f, 1, 1); materials[i].Flags.W = 1;
        }
        // Unit IOR makes the optical reference exact and isolates filtering from refraction.
        // The real GLB/--glass probe separately checks non-unit IOR and absorption.
        materials[2] = SceneMaterial.Default; materials[2].Factors = new(0, .25f, 1, 1); materials[2].Transmission = new(1, 1, .01f, 0);
        SceneMesh Quad(string name, float x0, float x1, float y, float z, int material)
        {
            var vertices = new SceneVertex[4];
            Vector3[] points = [new(x0, -y, z), new(x1, -y, z), new(x1, y, z), new(x0, y, z)];
            for (int i = 0; i < 4; i++) vertices[i] = new() { Position = new(points[i], 1), Normal = new(0, 0, 1, 0), Tangent = new(1, 0, 0, 1), Color = Vector4.One };
            return new(name, vertices, [0, 1, 2, 0, 2, 3], material);
        }
        var meshes = new SceneMesh[50]; var instances = new SceneInstance[50];
        for (int i = 0; i < 48; i++)
        {
            meshes[i] = Quad($"Stripe {i}", -1.5f + i / 16f, -1.5f + (i + 1) / 16f, 1, 0, i % 2);
            instances[i] = new(meshes[i].Name, i, Matrix4x4.Identity);
        }
        meshes[48] = Quad("Glass", -1.4f, 1.4f, .9f, 1, 2); instances[48] = new("Glass", 48, Matrix4x4.Identity);
        SceneMesh back = Quad("Glass exit", -1.4f, 1.4f, .9f, .99f, 2);
        for (int i = 0; i < back.Vertices.Length; i++) back.Vertices[i].Normal.Z = -1;
        meshes[49] = back with { Indices = [0, 2, 1, 0, 3, 2] }; instances[49] = new("Glass exit", 49, Matrix4x4.Identity);
        return new("Glass reconstruction regression", meshes, instances, materials, [], [], new(-1.5f, -1, 0), new(1.5f, 1, 1), []);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
