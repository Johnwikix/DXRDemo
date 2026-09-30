# Super resolution and ray reconstruction

## Implemented behavior

Settings offer Off, AMD FSR 3.1, Intel XeSS, NVIDIA DLSS SR and DLSS Ray Reconstruction, with a common
1–100% input-resolution control (67% initially). Off remains the default and
renders at native resolution. At 100%, a supported temporal algorithm operates
at native resolution for antialiasing. Unsupported algorithms are disabled in
the UI; programmatic requests and SDK failures report the fallback explicitly.
The GPU HUD shows the actual algorithm and input/output dimensions.

The scale is `max(1, floor(outputDimension * percent / 100))` on each axis.
Changes are applied by the render thread; the 1%-step slider is debounced for
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
   current neighborhood. The default **NVIDIA NRD RELAX** mode uses the official SDK;
   PBR scenes use its SH variant with material demodulation, SG resolve and
   re-jittering before SR. Glass is composited after opaque denoising, using
   current-frame reprojection and traced cache misses; its closest-surface motion
   replaces background motion for SR. Traced fallback lighting has a separate
   glass history with an uncapped same-pixel running mean and 64-bit frame count.
   Every unjittered camera change resets it immediately; sampling jitter does
   not. Instance changes and explicit scene/lighting/settings resets also start
   fresh. Compensated updates retain small contributions in a long history,
   without neighborhood clipping or history resampling. Cached opaque detail
   stays in the current frame for SR; native output accumulates the complete
   glass radiance. The custom RELAX/A-trous implementation
   has been removed; see [NRD integration](NRD.md). None of these
   modes implements DLSS Ray Reconstruction. The separate DLSSD mode instead
   consumes the original noisy radiance and bypasses all conventional denoisers.
4. The real vendor SDK consumes FP16 linear color, R32 depth, RG16F motion and
   R8 reactive masks. Its FP16 output is then encoded to SDR gamma or Rec.2020/PQ.
   The GPU HUD and XAML controls remain at output resolution.

Ray-sampling offsets are the inverse of projection offsets supplied to the SDK.
The preparation shader removes jitter from motion and compensates previous
jitter when looking up denoiser history. History uses texel-center bilinear
sampling with per-tap depth rejection and weight renormalization. Rounding
`currentJitter - previousJitter` to a single history texel each frame can
accumulate a directional error even when the camera and motion vectors are
stationary; the subpixel position must survive the history lookup.
Camera dragging/zooming uses motion
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

With SR and NRD both off, the previous encoded-space filters and stochastic
primary sampling remain. Temporal surfaces are allocated when SR or NRD is active.
One loading frame is presented before cold shader/SDK initialization.

## Validation on Intel Arc 140T

Date: 2026-09-29; driver 32.0.101.8826. All checks below are offscreen; no new
application/UI automation, display-mode changes or monitor HDR validation was
performed for this feature.

- Native bridge and WinUI x64 Release build passed. The app retains its existing
  generated WinUIEx obsolete `Icon` warning.
- Actual capability mask `0x18`: FSR and XeSS available; DLSS unavailable.
- 80 actual SDK cases: FSR/XeSS × 67/50/100/33/37% × four denoiser modes × SDR/HDR.
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
- A 96-frame stationary analytic-plane regression reads back the denoiser history
  before vendor SR. The old nearest-texel lookup failed with 0.968769 input-pixel
  error; bilinear reprojection reduces the maximum to 0.000477 pixels on both
  FSR and XeSS. This catches history drift that the zero-motion-vector check
  alone cannot detect. Separate cases cover depth edges, disocclusions and
  image borders. These are numeric checks, not a claim that stochastic path
  tracing noise disappears or a substitute for visual motion-quality testing.
- All 24 hardware/software parity cases (including NRD), resize and GPU HUD pass
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

## DLSS Ray Reconstruction (DLSSD): implemented, execution deliberately deferred

The x64 native adapter now creates `NVSDK_NGX_Feature_RayReconstruction` through
`NGX_D3D12_CREATE_DLSSD_EXT` and evaluates it through `NGX_D3D12_EVALUATE_DLSSD_EXT`.
The pinned NVIDIA headers, release `nvngx_dlssd.dll`, restoration manifest and
SHA-256 verification are included in build/publish assets. This is a separate
feature from the existing DLSS SR context.

The glTF/GLB PBR tracer supplies raw linear radiance, linear diffuse albedo,
integrated specular reflectance (split-sum approximation), unit world normals
with linear roughness in alpha, hardware depth, input-pixel motion, jitter and
reset state. A separate mirror ray supplies world-space secondary specular hit
distance only while RR is active. The 240-byte C ABI also supplies world-to-view
and view-to-clip matrices. Resources and descriptor tables are reused; resize or
mode changes release the feature only after queue completion.

RR uses unified denoising and reconstruction. It receives no temporal, custom or
NRD prefilter and is not followed by a DLSS SR dispatch. The selected regular
denoiser is retained for switching back. Display exposure and encoding follow RR.
The NGX availability query gates the settings option. Unsupported devices, the
legacy Stanford mesh tracer, feature-creation failures and evaluation failures
report an explicit fallback to conventional denoising plus DLSS SR when available,
otherwise native resolution. NGX errors are included in the status text.

**At the user's request, no DLSSD feature has been created or evaluated for
verification.** Native/C# compilation and resource-plumbing review are complete;
RTX execution, model/driver compatibility, matrix interpretation, motion quality,
disocclusion and glass quality remain user checks. Previous Intel FSR/XeSS results
above do not validate RR. `--glass` tests DXR/NRD and never executes DLSSD.

To exercise it manually, open the packaged convenience store on a supported RTX
device and select “NVIDIA DLSS 光线重建（DLSSD）” under “超分辨率 / 光线重建”.
Check the status reports DLSSD active, rather than a fallback. It also supports
requesting 100% input resolution; actual SDK acceptance remains authoritative.

## Primary references

- [NVIDIA DLSS RR integration guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS_RR.md)
- [NVIDIA DLSS SR integration guide](https://github.com/NVIDIA/DLSS/blob/374959484e79a640feaba44c93ac8cfb0a03f5b5/doc/DLSS_Programming_Guide_Release.pdf)
- [Intel XeSS SR input/resource contract](https://github.com/intel/xess/blob/de0fb9c1c510661c571164e1418ceca8101dab69/doc/xess_sr_developer_guide_english.md)
- [AMD FSR upscaling integration](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/)
