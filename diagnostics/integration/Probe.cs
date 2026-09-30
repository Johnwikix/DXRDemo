using ComputeSharp;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DXR_DXC_PATH")))
    Environment.SetEnvironmentVariable("DXR_DXC_PATH", Path.GetFullPath("../../DXRDemo/bin/x64/Release/net10.0-windows10.0.22621.0/dxc.exe"));
Directory.CreateDirectory("output");
if (args.Contains("--lighting-scene")) { await LightingProbe.SceneAblation(args.SkipWhile(a => a != "--lighting-scene").Skip(1).FirstOrDefault() ?? "baseline"); return; }
if (args.Contains("--lighting")) { LightingProbe.Run(); return; }
if (args.Contains("--glass-sr")) { GlassReconstructionProbe.Run(); return; }
if (args.Contains("--scene")) { await SceneProbe.Run(args.SkipWhile(a => a != "--scene").Skip(1).FirstOrDefault()); return; }
if (args.Contains("--scene-preview")) { await SceneProbe.Preview(args.SkipWhile(a => a != "--scene-preview").Skip(1).First()); return; }
if (args.Contains("--glass")) { await SceneProbe.Glass(args.SkipWhile(a => a != "--glass").Skip(1).First()); return; }
if (args.Contains("--sr")) { ReconstructionProbe.Run(); return; }
if (args.Contains("--nrd-missing")) { ReconstructionProbe.Run(true); return; }
using var device = GraphicsDevice.GetDefault();
using var hardware = new RayTracePass(new DxrMeshBackend());
using var software = new RayTracePass();
hardware.Initialize(device, default); software.Initialize(device, default);
using var target = device.AllocateReadWriteTexture2D<Rgba64, Float4>(480, 270);
var results = new List<object>();
for (int scene = 0; scene < 3; scene++)
{
    await MeshData.LoadAsync(scene);
    foreach (var mode in Enum.GetValues<RayTraceDenoiserMode>())
    foreach (bool hdr in new[] { false, true })
    {
        ushort[] Render(RayTracePass pass)
        {
            pass.SceneIndex = scene; pass.DenoiserMode = mode; pass.Samples = 2; pass.MaxBounces = 10;
            for (int i = 0; i < 12; i++) pass.TryExecute(target, target.Width, target.Height, TimeSpan.Zero, new HdrRenderParameters(hdr, 200, 1000));
            if (mode == RayTraceDenoiserMode.NrdRelax && (pass.ReconstructionStatus?.NrdActive != true || pass.NrdDispatchCount <= 5))
                throw new Exception("NRD was unavailable during parity check");
            var pixels = target.ToArray();
            return MemoryMarshal.Cast<Rgba64, ushort>(MemoryMarshal.CreateReadOnlySpan(ref pixels[0, 0], pixels.Length)).ToArray();
        }
        var h = Render(hardware); var s = Render(software);
        double error = 0, mean = 0; int max = 0;
        for (int i = 0; i < h.Length; i++)
        {
            int diff = Math.Abs((int)h[i] - s[i]); error += (double)diff * diff; max = Math.Max(max, diff); mean += h[i];
        }
        double rmse = Math.Sqrt(error / h.Length) / 65535;
        if (rmse > 0.025 || mean / h.Length < 100) throw new Exception($"Image regression: scene={scene} mode={mode} hdr={hdr} RMSE={rmse}");
        var item = new { model = MeshData.Choices[scene].Name, mode = mode.ToString(), hdr, rmse, maxError = max / 65535.0 };
        results.Add(item); Console.WriteLine(JsonSerializer.Serialize(item));
    }
}
// Verify the GPU HUD writes pixels, and a resized render target resets accumulation safely.
using var resized = device.AllocateReadWriteTexture2D<Rgba64, Float4>(320, 180);
hardware.TryExecute(resized, 320, 180, TimeSpan.Zero, HdrRenderParameters.Default);
using var hud = new GpuDiagnostics(device);
hud.Draw(resized, hardware, HdrRenderParameters.Default, 1, 2);
var screenshot = resized.ToArray();
File.WriteAllBytes("output/hud.rgba16", MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref screenshot[0, 0], screenshot.Length)).ToArray());
File.WriteAllText("output/results.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {results.Count} model/denoiser/HDR parity cases, resize, GPU HUD.");
