# Super resolution and ray reconstruction

## Implemented behavior

Settings now offer Off, AMD FSR 3.1, Intel XeSS and NVIDIA DLSS, with a common
1–100% input-resolution control (67% initially). Off remains the default and
renders at native resolution. At 100%, a supported temporal algorithm operates
at native resolution for antialiasing. Unsupported algorithms are disabled in
the UI; programmatic requests and SDK failures report the fallback explicitly.
The GPU HUD shows the actual algorithm and input/output dimensions.

The scale is `max(1, floor(outputDimension * percent / 100))` on each axis.
Changes are applied by the render thread; NumberBox changes are debounced for
200 ms. Preset K is requested for DLSS, matching Spectrum's default. No frame
generation is included. The optional vendor SDK bridge currently targets x64.

## Frame contract

1. The production triangle tracer renders at the selected input size, using one
   Halton sample position per frame. SPP still controls independent lighting
   paths; it no longer changes the primary sample location within that SR frame.
2. A first-hit texture stores world position and ray distance, or sky direction.
   From it, the preparation shader produces conventional finite perspective
   depth (near 0.01, far 1000, vertical FOV `2*atan(0.5)`) and current-to-previous
   **unjittered input-pixel** motion. Sky motion accounts for camera rotation
   without false translation parallax. The opaque scene uses a zero reactive mask.
3. SR-specific denoising stays in linear Rec.709 floating point. Temporal mode
   reprojects history, rejects disocclusions by view depth, and clamps it to the
   current neighborhood. RELAX-style mode additionally applies five edge-aware
   A-trous passes, using the existing normal/material guides. This is a custom
   filter, not NVIDIA NRD or DLSS Ray Reconstruction.
4. The real vendor SDK consumes FP16 linear color, R32 depth, RG16F motion and
   R8 reactive masks. Its FP16 output is then encoded to SDR gamma or Rec.2020/PQ.
   The GPU HUD and XAML controls remain at output resolution.

Ray-sampling offsets are the inverse of projection offsets supplied to the SDK.
The preparation shader removes jitter from motion and compensates previous
jitter when looking up denoiser history. Camera dragging/zooming uses motion
reprojection. Explicit camera reset, model/settings discontinuities, size/mode
changes and long frame gaps reset history. HDR encoding happens after SR and
does not contaminate its linear history.

SR uses the same D3D12 device as ComputeSharp and the ray tracer. Queue completion
is required before SDK contexts, mapped constants or descriptor tables are
reused, and before the next queue reads shared output. SDK dispatch changes root
bindings, so encoding explicitly binds its own state again. A failed unsubmitted
command list is discarded before native fallback; a changed configuration can
retry initialization. Resources and descriptors are reused in steady state.
No whole-application zero-allocation or FPS claim is made.

The Off path keeps the previous encoded-space filters and stochastic primary
sampling. The extra temporal surface is allocated only when SR is active.
One loading frame is presented before cold shader/SDK initialization.

## Validation on Intel Arc 140T

Date: 2026-09-29; driver 32.0.101.8826. All checks below are offscreen; no new
application/UI automation, display-mode changes or monitor HDR validation was
performed for this feature.

- Native bridge and WinUI x64 Release build passed. The app retains its existing
  generated WinUIEx obsolete `Icon` warning.
- Actual capability mask `0x18`: FSR and XeSS available; DLSS unavailable.
- 60 actual SDK cases: FSR/XeSS × 67/50/100/33/37% × three denoiser modes × SDR/HDR.
  Every case requires the actual active algorithm to equal the requested one;
  fallback output does not count as SR success. RGB is nonempty and nonconstant.
- Odd resize (401×227 at 37%), return to the previous size, model changes,
  disabling SR, unavailable-DLSS fallback and rejected-configuration recovery.
- Both available SDKs executed with 16×9 input and 1600×900 output. This verifies
  dispatch and lifetime, not perceptual quality at extreme ratios.
- GPU readback checks depth against independent CPU view/projection matrices,
  verifies motion direction/magnitude, confirms changing jitter produces no
  static-camera motion, and confirms explicit reset discards old motion.
- FP16 linear output is finite; the D3D12 debug layer reports no Error/Corruption.
- The existing 18 hardware/software parity cases, resize and GPU HUD still pass
  with SR disabled. Maximum normalized RGBA RMSE is approximately 0.001193.
- FSR/XeSS SDR snapshots were inspected. Motion quality, sustained performance,
  settings-window interaction and physical HDR output remain user/device checks.
  DLSS execution/quality tests are intentionally skipped on this Intel device.

Reproduce from `diagnostics/integration`:

```powershell
dotnet build IntegrationProbe.csproj -c Release --configfile ../performance/NuGet.Config
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --sr
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe
```

Use the EXE: FidelityFX provider discovery follows the native process executable
directory. Starting the DLL with `dotnet.exe` instead can produce
`FFX_API_RETURN_NO_PROVIDER`. Logs and image captures are under ignored
`diagnostics/integration/output/`. SDK versions, byte hashes and deployment rules
are in `External/Upscalers/README.md`.

## DLSS Ray Reconstruction (DLSSD): feasible, not implemented here

DLSSD can replace this demo's custom denoising, but it is a separate integration
from DLSS Super Resolution. The NVIDIA guide states that enabling RR overrides
the SR path and consumes noisy radiance plus additional material/geometry guides.
It should not be run after RELAX or followed by another DLSS SR dispatch.

This change supplies the reusable low-resolution linear radiance, jitter, depth,
motion and camera history. The remaining work is:

- Add the pinned RR headers and release `nvngx_dlssd.dll`, its capability query,
  feature creation/evaluation ABI and resource lifecycle.
- Write linear diffuse and specular albedo, floating-point world/view normals
  and linear roughness at the same primary sample location. Existing RGBA8
  normals pack material ID in alpha, so they are not a drop-in RR normal/roughness
  input. The current diffuse meshes can supply their actual constant albedo,
  roughness 1 and zero specular reflectance; future glass/metal needs real guides.
- Supply specular hit distance plus the required camera transforms, or specular
  motion vectors. The current alpha `t/(t+1)` is averaged **primary** hit distance;
  it is not world-space **secondary specular** hit distance and must not be relabeled.
- When RR is active, pass noisy linear radiance directly to RR and bypass both
  custom denoisers and DLSS SR. Encode its reconstructed output once. Keep a
  capability-gated fallback to the selected conventional denoiser plus SR.
- On RTX hardware, validate guide normalization, matrix conventions, transparent
  materials, disocclusions, camera cuts, reconstruction sizes and motion quality.
  Lack of Intel execution support cannot validate this path.

No dummy RR toggle or claimed RR execution is included. The requested NVIDIA
device tests are deferred; the three requested SR adapters are implemented.

## Primary references

- [NVIDIA DLSS RR integration guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS_RR.md)
- [NVIDIA DLSS SR integration guide](https://github.com/NVIDIA/DLSS/blob/374959484e79a640feaba44c93ac8cfb0a03f5b5/doc/DLSS_Programming_Guide_Release.pdf)
- [Intel XeSS SR input/resource contract](https://github.com/intel/xess/blob/de0fb9c1c510661c571164e1418ceca8101dab69/doc/xess_sr_developer_guide_english.md)
- [AMD FSR upscaling integration](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/)
