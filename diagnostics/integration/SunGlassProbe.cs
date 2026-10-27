using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Float4 = System.Numerics.Vector4;

/// <summary>Exercises the real analytic-sun shadow query through both sides of a thin window.</summary>
internal static unsafe class SunGlassProbe
{
    internal static void Run(string[] args)
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native = device.Device.QueryInterface<ID3D12Device5>();
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend { CausticsEnabled = false, UseInlineTraversal = !args.Contains("--legacy-trace") };
        backend.Initialize(device);
        const int width = 64, height = 32;
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normal = device.CreateTexture2D<Rgba32>(width, height);
        using var surface = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        var original = GlassReconstructionProbe.Fixture();
        var materials = (SceneMaterial[])original.Materials.Clone();
        for (int i = 0; i < 2; i++) { materials[i].Flags.W = 0; materials[i].BaseColor = Vector4.One; }
        materials[2].Factors.Y = 0; materials[2].Transmission = new(1, 1.9f, 0, 0);
        materials[2].Flags.Z = 1; // Match Bistro's double-sided window, including the opaque control.
        var asset = original with { Materials = materials, Instances = original.Instances[..^1] };
        // The camera is inside the window. Sun shadow rays exit through its back face.
        var camera = CameraFrame.Look(new(0, 0, .5f), -Vector3.UnitZ, .4f, .001f, 100);
        var sun = new SunLightSettings(true, 48.5f, 0, 4, 0);
        Float4[,] Render(SceneAsset scene)
        {
            backend.SetScene(scene);
            backend.Trace(camera, 0, 1, 1, default, 0, raw, normal, surface, guide, signals, sun);
            return raw.ToArray();
        }
        var reference = Render(asset with { Instances = original.Instances[..^2] });
        var backFace = Render(asset);
        var flippedMeshes = (SceneMesh[])asset.Meshes.Clone();
        var pane = flippedMeshes[48]; var vertices = (SceneVertex[])pane.Vertices.Clone();
        for (int i = 0; i < vertices.Length; i++) vertices[i].Normal.Z *= -1;
        flippedMeshes[48] = pane with { Vertices = vertices, Indices = [0, 2, 1, 0, 3, 2] };
        var frontFace = Render(asset with { Meshes = flippedMeshes });
        double incident = 0, back = 0, front = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { incident += reference[y, x].X; back += backFace[y, x].X; front += frontFace[y, x].X; }
        Console.WriteLine($"Sun through 48.5-degree thin IOR=1.9 window: back={back / incident:F6}, flipped/front={front / incident:F6}, incident={incident:F6}");
        Require(incident > 100, "The control receiver was not illuminated by the directional sun");
        Require(back / incident > .5 && Math.Abs(back - front) / incident < 1e-5,
            "Thin-window shadow transmittance depends on geometric facing or incorrectly reports total internal reflection");
        // Replacing the window with an opaque sheet must still block the sun.
        var opaqueMaterials = (SceneMaterial[])materials.Clone(); opaqueMaterials[2].Transmission.X = 0;
        var opaque = Render(asset with { Materials = opaqueMaterials });
        double blocked = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) blocked += opaque[y, x].X;
        Require(blocked < 1e-5, "Opaque geometry no longer blocks directional sunlight");
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        Console.WriteLine("PASS thin-window sunlight transmits equally from both sides; opaque blockers remain effective.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
