using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using DXRDemo.Assets.Importers;
using System.Buffers.Binary;

internal static class OfflineFeatureChecks
{
    internal static SceneAsset Fixture()
    {
        var a = SceneMaterial.Default; a.Factors.X = 0; a.Emissive = new(1, 1, 1, 0); a.Transmission = new(1, 1.5f, 1, 5);
        var b = a; b.Emissive = new(9, 9, 9, 0); b.Transmission = new(1, 1.33f, 1, 10);
        var opaque = SceneMaterial.Default;
        var doubleSided = opaque; doubleSided.Flags.Z = 1;
        SceneVertex[] vertices = [new() { Position = new(0, 0, 0, 1), Normal = new(0, 0, 1, 0) },
            new() { Position = new(1, 0, 0, 1), Normal = new(0, 0, 1, 0) }, new() { Position = new(0, 1, 0, 1), Normal = new(0, 0, 1, 0) }];
        return new("RTXPT core contracts", [new("dim", vertices, [0, 1, 2], 0), new("bright", vertices, [0, 1, 2], 1),
                new("opaque", vertices, [0, 1, 2], 2), new("double sided", vertices, [0, 1, 2], 3)],
            [new("dim", 0, Matrix4x4.Identity), new("bright", 1, Matrix4x4.CreateTranslation(0, 0, -1)),
                new("single sided", 2, Matrix4x4.CreateTranslation(3, 0, 0)),
                new("mirrored", 2, Matrix4x4.CreateScale(-1, 1, 1) * Matrix4x4.CreateTranslation(6, 0, 0)),
                new("double sided", 3, Matrix4x4.CreateTranslation(9, 0, 0))],
            [a, b, opaque, doubleSided], [], [], new(-1), new(10), [])
        {
            DefaultCamera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .7f, .01f, 100),
            EnvironmentMap = new(8, 4, Enumerable.Repeat(Vector4.One, 32).ToArray())
        };
    }
    internal static async Task RunCpu()
    {
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        foreach (bool black in new[] { false, true })
        {
            const int width = 16, height = 8;
            var pixels = new Vector4[width * height];
            if (!black) { pixels[0] = new(10000, 10000, 10000, 0); pixels[55] = new(1, 1, 1, 0); }
            var distribution = new EnvironmentDistribution(new(width, height, pixels));
            var reconstructed = new double[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Vector4 alias = distribution.Texels[i].Sampling;
                Check(alias.X >= 0 && alias.X <= 1 && alias.Y >= 0 && alias.Y < pixels.Length, "Invalid environment alias.");
                reconstructed[i] += alias.X / pixels.Length;
                reconstructed[(int)alias.Y] += (1 - alias.X) / pixels.Length;
            }
            double integral = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int y = i / width;
                double omega = 2 * Math.PI / width * (Math.Cos(Math.PI * y / height) - Math.Cos(Math.PI * (y + 1) / height));
                double pmf = distribution.Texels[i].Radiance.W * omega;
                Check(pmf > 0 && Math.Abs(pmf - reconstructed[i]) < 2e-7, "Environment sampling and MIS densities disagree.");
                integral += pmf;
            }
            Check(Math.Abs(integral - 1) < 2e-7, "Environment PDF does not integrate to one.");
        }
        string directory = Path.GetFullPath("output/rtxpt-offline/fixtures"); Directory.CreateDirectory(directory);
        string bc7Path = Path.Combine(directory, "bc7.dds");
        var dds = new byte[148 + 48];
        void Header(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
        Header(0, 0x20534444); Header(4, 124); Header(12, 4); Header(16, 4); Header(28, 3);
        Header(76, 32); Header(84, 0x30315844); Header(128, 99); Header(132, 3); Header(140, 1);
        File.WriteAllBytes(bc7Path, dds);
        Check(Bc7Texture.Read(bc7Path, 4, 4, 3, true).All(m => m.Length == 16), "BC7 mip tail block sizes are invalid.");
        foreach (int failure in new[] { 0, 1, 2 })
        {
            if (failure == 2) File.WriteAllBytes(bc7Path, dds[..^1]);
            bool invalid = false;
            try { Bc7Texture.Read(bc7Path, failure == 0 ? 8 : 4, 4, 3, failure != 1); }
            catch (InvalidDataException) { invalid = true; }
            Check(invalid, "BC7 dimensions, transfer function or truncated data were accepted.");
        }
        string path = Path.Combine(directory, "rgbe.hdr");
        using (var file = File.Create(path))
        {
            file.Write(System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 8\n"));
            for (int y = 0; y < 2; y++)
            {
                file.Write([2, 2, 0, 8]);
                foreach (byte c in new byte[] { 128, 64, 32, 129 }) file.Write([136, c]);
            }
        }
        var decoded = SceneEnvironment.LoadRadiance(path, 8);
        Check(decoded.Width == 8 && decoded.Height == 2 && decoded.Pixels.All(p => p == new Vector4(1, .5f, .25f, 0)), "RGBE decode lost linear HDR energy.");
        File.WriteAllBytes(Path.Combine(directory, "truncated.hdr"), File.ReadAllBytes(path)[..^1]);
        bool rejected = false;
        try { SceneEnvironment.LoadRadiance(Path.Combine(directory, "truncated.hdr")); } catch (EndOfStreamException) { rejected = true; }
        Check(rejected, "Truncated RGBE was accepted.");
        string scenePath = Path.Combine(directory, "lighting.gltf");
        File.WriteAllText(scenePath, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{}],
             "materials":[{"extras":{"nestedPriority":6}}],
             "extras":{"environmentMap":"rgbe.hdr","environmentRotation":0.5}}
            """);
        SceneDocument imported = await GltfImporter.LoadAsync(scenePath);
        Check(imported.Scenes[0].Materials[0].Transmission.W == 6 &&
            imported.Scenes[0].EnvironmentMap is { Width: 8, Height: 2, Rotation: .5f }, "Scene lighting/priority metadata was not imported.");
        Console.WriteLine("PASS: BC7 layout and rejection checks; HDR decode, malformed input, alias/PDF conservation including black environment.");
    }
}
