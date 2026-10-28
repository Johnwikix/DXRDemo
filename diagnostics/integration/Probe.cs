using DXRDemo.Scene;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.Shaders.RayTrace;
using DXRDemo.SuperResolution;
using System.Runtime.InteropServices;
using System.Text.Json;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DXR_DXC_PATH")))
    Environment.SetEnvironmentVariable("DXR_DXC_PATH", Path.GetFullPath("../../DXRDemo/bin/x64/Release/net10.0-windows10.0.22621.0/dxc.exe"));
Directory.CreateDirectory("output");
DxrDevice.TryActivateAgilityRuntime();
if (args.Contains("--hud")) { HudProbe.Run(); return; }
if (args.Contains("--scene-session")) { await SceneSessionProbe.Run(); return; }
if (args.Contains("--scene-session-gpu")) { await SceneSessionProbe.RunGpu(); return; }
if (args.Contains("--gpu-scene-cache")) { GpuSceneCacheProbe.Run(); return; }
if (args.Contains("--scene-core")) { DxrDevice.TryActivateAgilityRuntime(); await SceneProbe.Run(materialOnly: true); return; }
if (args.Contains("--raster")) { RasterProbe.Run(); return; }
if (args.Contains("--common-frame")) { CommonFrameProbe.Run(); return; }
if (args.Contains("--raster-scenes")) { await RasterSceneProbe.Run(); return; }
if (args.Contains("--nrd-motion")) { DxrDevice.TryActivateAgilityRuntime(); NrdMotionProbe.Run(args); return; }
if (args.Contains("--temporal-convergence")) { TemporalConvergenceProbe.Run(); return; }
if (args.Contains("--environment-sampling")) { DxrDevice.TryActivateAgilityRuntime(); EnvironmentSamplingProbe.Run(); return; }
if (args.Contains("--optix") && !args.Contains("--temporal-scene")) { DxrDevice.TryActivateAgilityRuntime(); OptixProbe.Run(); return; }
if (args.Contains("--sun-glass")) { DxrDevice.TryActivateAgilityRuntime(); SunGlassProbe.Run(args); return; }
if (args.Contains("--sun-shadow")) { DxrDevice.TryActivateAgilityRuntime(); SunShadowProbe.Run(args); return; }
if (args.Contains("--sr-detail") || args.Contains("--sr-mips") || args.Contains("--sr-bistro")) { DxrDevice.TryActivateAgilityRuntime(); DenoiserSrProbe.Run(args); return; }
if (args.Contains("--temporal-scene")) { DxrDevice.TryActivateAgilityRuntime(); await TemporalSceneProbe.Run(args); return; }
if (args.Contains("--offline")) { DxrDevice.TryActivateAgilityRuntime(); await OfflineProbe.Run(args); return; }
if (args.Contains("--lighting-scene")) { await LightingProbe.SceneAblation(args.SkipWhile(a => a != "--lighting-scene").Skip(1).FirstOrDefault() ?? "baseline"); return; }
if (args.Contains("--lighting")) { LightingProbe.Run(); return; }
if (args.Contains("--glass-sr")) { GlassReconstructionProbe.Run(); return; }
if (args.Contains("--glass-temporal")) { DxrDevice.TryActivateAgilityRuntime(); GlassTemporalProbe.Run(args); return; }
if (args.Contains("--scene")) { await SceneProbe.Run(args.SkipWhile(a => a != "--scene").Skip(1).FirstOrDefault()); return; }
if (args.Contains("--scene-preview")) { await SceneProbe.Preview(args.SkipWhile(a => a != "--scene-preview").Skip(1).First()); return; }
if (args.Contains("--glass")) { await SceneProbe.Glass(args.SkipWhile(a => a != "--glass").Skip(1).First()); return; }
if (args.Contains("--sr")) { ReconstructionProbe.Run(); return; }
if (args.Contains("--nrd-missing")) { ReconstructionProbe.Run(true); return; }

using var device = DxrDevice.CreateDefault();
using var pass = new RayTracePass();
pass.Initialize(device, default);
pass.SceneIndex = SceneCatalog.Kitchen;
pass.ReconstructionMode = ReconstructionMode.Off;
pass.DenoiserMode = RayTraceDenoiserMode.TemporalOnly;
pass.Samples = 1;
pass.MaxBounces = 4;
using var target = device.CreateTexture2D<Rgba64>(480, 270);
for (int i = 0; i < 12; i++)
    pass.TryExecute(target, target.Width, target.Height, TimeSpan.Zero, HdrRenderParameters.Default);

var pixels = target.ToArray();
var channels = MemoryMarshal.Cast<Rgba64, ushort>(MemoryMarshal.CreateReadOnlySpan(ref pixels[0, 0], pixels.Length));
double mean = 0;
foreach (ushort value in channels) mean += value;
mean /= channels.Length;
if (mean < 100) throw new InvalidOperationException($"Current SceneAsset render is unexpectedly dark: mean={mean}");

using var resized = device.CreateTexture2D<Rgba64>(320, 180);
pass.TryExecute(resized, resized.Width, resized.Height, TimeSpan.Zero, HdrRenderParameters.Default);
using var hud = new GpuDiagnostics(device);
hud.Draw(resized, pass, HdrRenderParameters.Default, 1, 2);
var screenshot = resized.ToArray();
File.WriteAllBytes("output/hud.rgba16", MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref screenshot[0, 0], screenshot.Length)).ToArray());
var result = new { scene = "Kitchen", mean, resize = "320x180", hud = "native" };
File.WriteAllText("output/results.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS: current SceneAsset smoke, resize, native GPU HUD.");
