# Model / HDR / settings integration validation

The `--sr` option now exercises the real FSR/XeSS/DLSS adapters, temporal guides,
scale/denoiser/HDR transitions and fallback. Run the built **IntegrationProbe.exe**
so the FidelityFX loader discovers providers beside the executable. The native
bridge builds automatically; `SkipReconstructionNativeBuild=true` reuses it.
Intel results and the DLSSD feasibility assessment are in
[`docs/SUPER_RESOLUTION.md`](../../docs/SUPER_RESOLUTION.md).

Both apps now load the same packaged Stanford Bunny (69,451 triangles), Armadillo
(345,944) and Dragon (871,414). Each app has its own settings window; the main
window's **设置** button or **F1** opens it. Closing settings hides that window and
keeps rendering. Closing the main window closes settings and releases the renderer.

The GPU HUD is a compute pass writing a bitmap font into the frame UAV after
denoising. Text changes twice per second. There is no XAML frame counter. It shows
the backend, model, triangle count, SPP, bounce limit, denoiser, resolution and HDR
mode. RENDER counts completed render-loop frames; SUBMIT counts successful
`Present` calls, which is not the monitor's display rate. FRAME includes rendering,
denoising, synchronization, HUD and submission; it is not a GPU timestamp result.

DXR uses a triangle BLAS/TLAS on the **same D3D12 device** as ComputeSharp's
postprocessing. The renderer obtains the generated mesh HLSL, replaces only the
software BVH traversal with `TraceRay`, and packs constants through ComputeSharp's
generated descriptor. Its fence completes before the denoiser reads those UAVs.
The ported HDR renderer uses a dedicated render thread and `Present(0, DoNotWait)`
(plus `AllowTearing` when available); it does not wait for XAML rendering or vsync.

The denoiser is the existing custom temporal / SVGF/RELAX-inspired implementation,
not NVIDIA's NRD SDK. The mesh HDR encoder transforms linear Rec.709 to Rec.2020
and encodes ST.2084 into the HDR10 swap chain. The inherited filters operate on
encoded normalized radiance. Material ids are packed into UNORM8 and decoded
before edge filtering; camera, scene, size, samples, bounces, denoiser and HDR
changes invalidate temporal history.

## Verification (2026-09-28, RTX 5070 Ti)

- Both WinUI projects built in Release. DXR has one generated WinUIEx obsolete
  `Icon` warning; ComputeSharp additionally reports trimming warnings for buffer
  allocation (the existing project enables trimming in Release).
- The integration probe ran the actual production backend and filter sources:
  **18 / 18** combinations (three models, three denoisers, SDR/HDR), twelve frames
  per combination at 480×270, 2 SPP, 10 maximum bounces. Normalized RGBA RMSE between
  DXR and software outputs was at most **0.000553**. Local differences at edge rays
  are expected from hardware/software intersection arithmetic.
- Render-target resize and GPU HUD dispatch passed in the same probe.
- lvt verified real windows: opening/hiding settings repeatedly, switching all
  three models, switching denoisers, HDR/SDR, and maximizing/restoring the
  ComputeSharp window. Screenshots and machine-readable parity results are in
  `output/` (ignored local artifacts).
- ComputeSharp's existing clouds / Apple Music / path-trace switches, numeric
  settings, and hiding/showing the GPU HUD were exercised with lvt as well.
- Observed whole-app HUD readings with one app running at a time, 1280×720,
  2 SPP and 10 bounces:

| Scene / settings | DXR render FPS | ComputeSharp render FPS |
|---|---:|---:|
| Bunny, SDR, no denoiser | ~1070 | ~258 |
| Dragon, SDR, no denoiser | ~1109 | ~178 |
| Bunny, HDR10, RELAX-style | ~710 | ~250 |

These are live half-second HUD samples, not benchmark medians. They include
encoding, postprocessing and presentation overhead and use different settings
from `../performance/MESH_RESULTS.md`'s isolated 4-SPP GPU timestamp benchmark.

## Run the verification probe

From this directory:

```powershell
dotnet build IntegrationProbe.csproj -c Release --configfile ../performance/NuGet.Config
dotnet bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.dll
```

Build DXR first so its bundled DXC exists. Model assets are copied into the probe's
output directory by MSBuild. `ui_settings.py MAIN_HWND SETTINGS_HWND MODEL DENOISER
HDR` drives actual lvt controls using a fresh UIA tree before each action.

For direct executable testing the GUI projects were built with
`-p:Platform=x64 -p:WindowsSdkPackageVersion=10.0.22621.57 -p:WindowsPackageType=None`
and `-p:WindowsAppSDKSelfContained=true`. These are build overrides; normal MSIX
project configuration is preserved. The SDK version override uses this machine's
cached Windows SDK package. Model attribution and terms are bundled in
`Assets/Models/SOURCES.md` in both applications.

The final DXR build used `-p:IntermediateOutputPath=obj/validation-final/`
and `-p:OutputPath=bin/validation-final/` because a separate Visual Studio instance
was building/running the normal Release output. That running instance was left
alone; the final isolated build succeeded.
