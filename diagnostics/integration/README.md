# Offline RTXPT validation

`IntegrationProbe.exe --offline` renders exactly one 256×144, 4-SPP frame with
256-pixel textures, no window, warmup, photon pass, denoiser or SR. It saves PNG,
RGBA32F and JSON to `output/rtxpt-offline`. `--cpu-only` checks assets, the authored
camera and light PDFs without creating a graphics device; add `--convergence`
or `--bistro` to select another packaged RTXPT model. Bistro includes its Shanghai Bund HDR environment. See [commands and limits](../../docs/RTXPT_REWRITE.md).

`--contracts` runs the production HLSL's material, priority-medium, HDR PDF,
guided-light PDF, TIR, delta-lobe, mirrored/culling and near-delta numeric contracts in one 9×4 dispatch. `--environment`
accepts a Radiance `.hdr`; `--production` enables the real-time feature switches
without enabling photon passes. `--stable` checks three-plane recomposition;
`--nrd` additionally executes the three independent NRD contexts once, without
temporal warmup. These switches remain bounded single-frame commands. CPU checks
also exercise HDR decoding, alias-table normalization and glTF lighting metadata.

For the 30 FPS GPU budget, use `--offline --bistro --production --dlss-4k`:
3840×2160 output, 1920×1080 input, 1 SPP, 10 bounces, 1024 texture limit,
three NRD contexts and DLSS Super Resolution. Exactly one traced frame and one
reconstruction execution, with no temporal warmup. The actual 4K encoded output
is checked and saved as `*-4k.png`. `--spp`, `--bounces`, `--texture-size`,
`--uncompressed`, `--reorder` and `--no-ser` provide explicit comparisons.
Use `--rr-4k` instead of `--dlss-4k` for DLSS Ray Reconstruction without NRD.
The normal path uses SM 6.9 inline traversal without reordering; `--reorder`
enables material-coherent SER, and `--legacy-trace` (`--no-ser` alias) selects the TraceRay fallback.

## Temporal camera-motion validation

`--offline --motion` renders 64 deterministic frames: 16 stationary warmup, 16 orbit,
8 pan, 16 FOV changes and 8 stopped frames. It saves every display-resolution PNG
and `motion.json` under `output/rtxpt-motion`. The first orbit/pan/FOV frame is
checked against an independent CPU view-projection calculation for both primary
and stable-transmission motion; the maximum sampled error must be below 0.25
input pixels. Every frame also asserts the native NRD history length (8 while
the camera changes, 30 when stationary). `--motion-frames N` can shorten the
sequence to no fewer than 57 frames so every phase is still covered, and
`--motion-name LABEL` selects a separate `output/rtxpt-motion-LABEL` directory.

```powershell
# Native 4K input/output, primary NRD plus reflected/transmitted histories.
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --offline --motion --bistro --production --nrd --width 3840 --height 2160 --spp 1 --bounces 10 --texture-size 1024

# 1920x1080 input -> 3840x2160 output, NRD plus DLSS SR and nonzero jitter.
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --offline --motion --motion-name dlss-4k --bistro --production --dlss-4k --spp 1 --bounces 10
```

`--nrd-motion` is a separate noise-free 4K regression. It feeds a world-anchored
linear color field through the real NRD adapter for 32 frames of translation and
FOV changes, checks linear output RMSE below 0.01 and writes
`output/nrd-synthetic-motion`. `--width`, `--height` and `--frames` can reduce
its resource cost for iteration.

Agility is activated before device creation. JSON records the reported shader
model, actual SER setting, compressed texture count/bytes and GPU timestamps.
Both GPU stages run before CPU readbacks and validation; all reconstruction
pipeline creation occurs before the traced frame. The 33.33 ms budget covers
trace plus reconstruction GPU work, excluding asset load, shader compilation,
CPU checks, PNG encoding and presentation. It is not a sustained FPS measurement.

The older commands below run multiple frames and should not be used while gaming.

# Model / HDR / settings integration validation

`--sr-detail` measures texture contrast and linear error after 48 frames through
the actual FSR/XeSS/DLSS SDKs at 50% input, with None/Temporal/OptiX. `--noisy`
adds deterministic exponential Monte Carlo noise; `--emissive` tests current-frame
unfiltered detail. `--sparse` ensures rare bright paths retain their mean across
jitter, and `--impulse` rejects recursive spreading into adjacent pixels.
`--fsr-only` bounds iteration to one provider. JSON and PNG
artifacts are saved under `output/sr-detail[-noisy][-emissive]`.
`--sr-mips` independently compares actual production texture sampling at 50%
input with identical native-resolution rays. `--sr-bistro` captures 64 frames of
the actual application Bistro path at 1 SPP / 8 bounces, 67% input, 1280×720
output and the first available DLSS/XeSS/FSR provider. Add `--temporal` to compare
Temporal instead of OptiX; captures go to `output/sr-bistro`.

`IntegrationProbe.exe --glass-temporal` executes the actual FSR/XeSS/DLSS SDKs
with Temporal and OptiX on a 384×240 striped-background glass fixture, at 50%
input and 1 SPP. It measures 16 of 48 stationary frames and eight camera-motion
frames against independent optical depth, motion and radiance references.
Assertions cover contrast, frame RMS, optical motion/depth, moving detail,
energy, actual OptiX execution and D3D12 Error/Corruption messages. Add one of
`--volume` (closed 0.01-thick slab), `--partial` (60% transmission), `--reflective`
(reflection-dominant IOR), `--rough` (0.02 roughness) or `--layered` (two thin
sheets). `--hdr-detail` adds bright emission with production firefly clipping;
`--scale 67` / `--scale 100` check other input ratios. `--fsr-only` bounds
iteration. JSON/PNG results go to `output/glass-temporal[-variant][-hdr][-scale]`.
Opaque stripe reference classification, volume instance identity and analytic
moving-image checks keep the feedback independent of the reconstruction shader.
The partial-transmission energy check allows its additional foreground diffuse
contribution. These tests do not replace noisy-scene or curved-glass evaluation.

`IntegrationProbe.exe --optix` requires actual OptiX execution and checks noise
reduction against known radiance, an independent raw arithmetic mean, oblique
thin-glass background transmission, moving-camera motion vectors, camera/jitter
resets, resize, NRD transitions and an explicit unavailable adapter. The sphere
example's encoded-only fallback is also checked. `--temporal-scene --optix`
captures actual Bistro checkpoints at 1/4/8/16/32/64/... frames and reports CUDA
inference time separately from the complete reconstruction GPU stage.
Use `--output <directory>` to avoid overwriting other ablation captures.

Known baseline failures on the current renderer: `--glass-sr` fails the rough
glass detail contrast assertion (0.02541), and `--lighting` fails the old ReSTIR
convergence assertion. Both reproduce with the pre-OptiX production shader files.
The targeted OptiX/thin-sheet transmission checks do not count these as passing.
The full `--scene` suite also has a stale startup assertion requiring the night
demo while the application now defaults to Kitchen. The scene sun toggle,
intensity, hard-shadow and NRD checks run before that assertion and pass;
`--sun-shadow`, `--sun-glass` and `--builtin-shadow` are independent entry points.

`--sun-glass` isolates a directional sun shadow ray through the back of a thin
IOR=1.9 window at 48.5 degrees. It first reproduced zero transmittance from one
side versus 0.886646 when the same pane was flipped. The regression requires
facing-independent thin-sheet Fresnel and an opaque-sheet negative control.
Add `--legacy-trace` to exercise the fallback traversal.

`--sun-shadow` traces a diffuse receiver and a knife edge at 1 m and 8 m using
256 samples per pixel. It requires distance-dependent penumbrae for the default
0.526-degree solar diameter, double width when the diameter doubles, and hard
shadows at zero diameter. It also checks normalized unoccluded irradiance at
0.526 and 20 degrees, exact lobe recomposition, and that stochastic solar light
does not bypass denoising in Unfiltered. `output/sun-shadow/profiles.json` records
the measured radiance profiles. Add `--legacy-trace` for the fallback traversal.
`--builtin-shadow` separately checks the old Bunny DXR/compute tracers: delta-sun
NRD bypass and finite-sun soft shadows, noisy-light routing and backend parity.

`IntegrationProbe.exe --temporal-convergence` feeds deterministic noisy samples
through the production linear and encoded Temporal shaders at native resolution.
It checks RMSE at 32 and 256 frames against both the known reference and an
independent CPU arithmetic mean, then verifies per-pixel counts and the two-sample
mean immediately after a depth discontinuity. It rejects the former 32/64-frame
noise floors, stationary history clipping and global-frame-count reuse after
history rejection. On the RTX 5070 Ti, linear RMSE falls from 0.024807 to 0.008759
(CPU-mean RMSE 1.269e-6), and the post-disocclusion two-sample error is 1.376e-8.

`IntegrationProbe.exe --temporal-scene --bistro --width 1920 --height 1080 --frames 256 --reference-frames 16 --reference-spp 32 --spp 1 --bounces 8`
renders the actual Bistro camera with native Temporal accumulation and a production
512-SPP reference. Raw/checkpoint/reference PNGs and `metrics.json` are written to
`output/temporal-bistro-1920x1080`. Every pixel reaches 256 samples; late-frame RMS
falls from 0.024385 to 0.000819 and reference RMSE from 0.027784 (raw) to 0.009533
(Temporal). The production 512-SPP reference still contains heavy-tailed path noise,
so this is not evidence that 1-SPP Bistro becomes clean within a few hundred frames.
Add `--nrd` for the separate `output/temporal-bistro-1920x1080-nrd` comparison; it is
cleaner but more blurred and is not an unbiased reference. `--reference-independent`
selects the independent transport/subpixel sampler instead of the production sampler.

The temporal suite also checks a sparse first-frame impulse with an off-origin
stationary camera: subsequent black samples must never gain energy from adjacent
pixels through repeated reprojection. This failed before the same-pixel history
path (neighbor leakage 0.002126) and passes with zero leakage. A changed-jitter
case verifies that SR footprints still reproject. `--environment-sampling` tests
the production HDR sampler against an independent CPU spherical integral.
For scene ablations, `--output <directory>` separates runs and `--capture-linear`
exports RGBA float32 snapshots. The probe accepts `--no-emission`,
`--no-environment`, `--no-normal-maps`, `--uncompressed`, `--no-adaptive-lighting`,
`--no-caustics` and `--firefly-threshold <value>`.
See [OptiX investigation and remaining input noise](../../docs/OPTIX_DENOISING.md).

`IntegrationProbe.exe --glass-sr` checks the actual NRD/SR glass composition with
a fine stripe fixture: detail contrast, frame-to-frame RMS over the jitter
sequence, opaque/transparent guide separation, closest-glass motion, camera
translation/rotation/roll/FOV/projection resets, resumed accumulation and
resize, plus opaque transmission-map texels and metallic materials. A negative
control deliberately uses pane guides and must reproduce
blur. Each available FSR/XeSS/DLSS provider runs at 67% and 100%; unsupported
providers are explicitly skipped. Images are saved under `output/glass-sr/`.
`--glass ../../Samples/RainyCorner/RainyCorner.glb` separately checks non-unit IOR
and noise against a 512-SPP reference in the actual refractive scene. It also
measures frame-to-frame glass noise before/after resolve at native size and at
67% FSR/XeSS input size over 256 frames, checks continued convergence after the
old 64-frame limit, mapped brightness and history reset. GPU means are compared
with independent CPU averages; changing jitter must preserve counts at stable
glass instances. These regressions reject capped, clipped or reprojected means.
Current metrics and limitations are recorded in [NRD verification](../../docs/NRD.md).
The custom RELAX mode has been removed; current denoisers are Off, Temporal and
official NRD. Old verification counts below are historical.

`IntegrationProbe.exe --lighting` exercises ReSTIR DI and refractive photon caustics
with synthetic fixtures and the D3D12 debug layer. It checks many-light and emissive
area-light energy, history/camera cuts, lobe reconstruction, glass focusing, IOR=1,
light removal and resize. See [implementation and results](../../docs/RESTIR_CAUSTICS.md).
The `DXR_DXC_PATH` environment override is honored by all probe modes.

The lighting suite includes raw-radiance regressions for a wet floor behind
parallel glass (including mirrored/scaled and densely tessellated variants),
plus static photon reuse and light-change invalidation. These fail on the original
photon-blotch implementation; sphere focusing must continue to pass.

`--lighting-scene baseline|no-restir|no-caustics|neither` captures the actual
Rainy Corner scene at a fixed camera, frame sequence, 640×400, 2 SPP and 10 bounces,
without a denoiser or SR. It saves exposure −2 SDR PPMs and linear RGBA32F buffers
under `output/lighting/scene/`. This is an attribution probe; it does not assert
that the image quality is acceptable.

`--lighting-scene nrd-fixed` and `--lighting-scene dlssd-fixed` execute the actual
requested reconstruction for 48 frames and save display PNGs alongside raw buffers.
They fail if NRD/DLSSD falls back. Run outside a restricted sandbox if vendor
capability initialization cannot access its driver/SDK cache.

The `--sr` option now exercises the real NRD/FSR/XeSS/DLSS adapters, temporal guides,
scale/denoiser/HDR transitions, a 512-SPP quality reference and fallback. NRD must
execute its official dispatches for its cases to pass. Run the built **IntegrationProbe.exe**
so the FidelityFX loader discovers providers beside the executable. The native
bridge builds automatically; `SkipReconstructionNativeBuild=true` reuses it.
Intel results and the DLSSD feasibility assessment are in
[`docs/SUPER_RESOLUTION.md`](../../docs/SUPER_RESOLUTION.md).
The new NRD contract and results are in [`docs/NRD.md`](../../docs/NRD.md).
The notes below describe the original integration before the SR/NRD extensions.

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

The historical verification below used the former custom SVGF/RELAX-inspired
implementation. Current rendering uses NVIDIA NRD; the old implementation is
removed. The mesh HDR encoder transforms linear Rec.709 to Rec.2020
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
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe
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
