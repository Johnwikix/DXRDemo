# Official NVIDIA NRD integration

The default mesh denoiser is now **NVIDIA NRD 4.17.3 / RELAX_DIFFUSE**. Select it
in Settings → 降噪. PBR scenes use **RELAX_DIFFUSE_SPECULAR_SH**. Temporal accumulation
remains a comparison option; the custom RELAX implementation and its resources
have been removed. SDK absence or failure produces an explicit temporal
fallback in the status/HUD; the UI disables unavailable SDKs.

The current mesh materials implement diffuse reflection only. RELAX_DIFFUSE is
the complete official algorithm for that signal, including prepass, reprojection/
accumulation, history repair/clamping, anti-firefly and A-trous filtering. The
adapter consumes every dispatch returned by `nrd::GetComputeDispatches`; stable
frames currently contain 12 dispatches. This does not expose every NRD algorithm
in the UI: REBLUR, SIGMA and other variants require their corresponding signals
and integration. The PBR adapter supplies real diffuse/specular transport,
first-bounce directional moments and matching material factors to the SH variant.

## PBR glass and upscaling (2026-09-30)

The old integration denoised glass transmission as a specular lobe using the
glass pane's depth, normal and motion. That mixes unrelated surfaces behind
the pane and discards detail needed by temporal upscalers. Its fully reactive
glass mask also prevented useful SR history accumulation.

The corrected sequence follows the NRD README and its transparent sample:

1. Trace the primary **opaque** surface, skipping transmitting geometry only on
   the primary ray. Secondary transport, transparent shadows and caustics still
   use the actual materials. NRD guides describe the opaque surface being denoised.
2. Separate diffuse/specular irradiance and apply `NRD_MaterialFactors` to both
   lobes. Accumulate luminance-weighted moments from the actual light/first-bounce
   directions, pack with `RELAX_FrontEnd_PackSh`, and execute all official SH
   dispatches. Specular hit distances use the official averaging helpers.
3. Unpack the SG output, resolve diffuse/specular against the current normal,
   view and roughness, and apply `NRD_SG_ReJitter` when SR is active. Neighbor
   accesses clamp to the render rectangle. Restore the same material factors,
   then add unfiltered primary direct light and emission.
4. Compose glass **after** NRD. Trace one reflection and one refraction path per
   sample at fully transmitting primary surfaces. Look up lighting in the
   completed current opaque frame with bilinear reprojection and position,
   normal and outgoing-direction rejection. Cache misses continue the real
   path tracer, including absorption and nested media. No SHARC cache is used.
5. Resolve glass history explicitly. With SR, accumulate only lighting that did
   not come from the denoised opaque cache; cached, re-jittered detail stays in the
   current frame. At native resolution, accumulate the complete glass radiance.
   Bilinear history lookup validates the closest instance (and its material),
   normal and depth. Current-neighborhood bounds reject stale history without
   averaging neighboring colors. Static history is limited to 64 samples,
   reduced to 8 when motion exceeds 0.25 input pixels. Camera cuts, lighting/scene
   changes, sample/bounce changes and size/mode transitions use the existing reset.
6. Keep opaque depth for SR, but patch motion to the closest glass layer, as in
   the NRD transparent sample. Motion remains current-to-previous and unjittered.
   Glass uses a 0.1 reactive value so stable detail can retain temporal history.

NRD and SR have the same logical input/render resolution. SG resolve runs there
before the vendor upscaler; it does not claim to create display-resolution guides.
The first glass composition revision omitted the history for traced cache misses,
which left low-SPP reflection/refraction noise visible. The glass resolve now
stabilizes that contribution independently of the opaque NRD history. Curved/
refractive glass still follows the actual optical path;
the closest-glass motion is a heuristic rather than exact refractive scene flow.

Opaque and transparent guides are separate textures. The transparent guide stores
the closest glass position with `w = -(rayDistance + 1)`; sky remains `w = -1`.
Glass history stores instance identity in its FP32 position buffer, with a
separate normal buffer. It cannot reuse a neighboring pane's history merely
because the panes share a material. Reset prevents old lighting from surviving.
Queues finish before borrowed textures/descriptors are reused. Cache SRVs are
cleared after the glass fence, and linear denoised color remains a non-pixel SRV
after execution, including the glass output.

Old saved denoiser indices 2 (custom RELAX) and 3 (NRD) both migrate to the current
NRD selection. Off and Temporal keep their saved meanings. DLSSD continues to
consume raw noisy PBR radiance and uses its existing guides, independently of
this NRD glass composition path.

## Data and resource contract

- Each frame uses one primary sample location for all SPP, with independent
  cosine-weighted diffuse continuation paths. A separate FP32 guide stores
  world-space normal and linear roughness 1, packed exactly as encoding 4.
- In NRD mode raw alpha contains the average **first diffuse bounce hit distance**
  in world units. A miss is 65504. It excludes the camera-to-primary distance and
  any PDF weighting. A one-bounce/direct-light-only render still traces the guide
  ray. The old encoded primary distance remains unchanged outside NRD mode.
- Before NRD, the shader removes the primary material albedo from linear color
  and uses the matching official `RELAX_FrontEnd_PackRadianceAndHitDist` helper.
  Current mesh/ground albedos are the tracer's constants. Future materials must
  extend the guide contract instead of retaining these fixed values.
- `IN_VIEWZ` is signed linear view depth matching the right-handed camera, not
  the normalized perspective depth sent to SR. Sky uses -1,000,000 and stays
  outside the 1,000-unit denoising range. Composition restores sky/out-of-range
  pixels directly from the tracer because NRD does not write them.
- Motion is current-to-previous, without jitter, scaled from input pixels into
  UVs for NRD. Non-jittered current/previous view matrices and projection are
  supplied in NRD's column-major convention. NRD receives ray sample jitter;
  vendor SR SDKs continue to receive inverse projection jitter.
- NRD output is unpacked with the official helper and multiplied by the same
  primary albedo. It then enters FSR/XeSS/DLSS, or the HDR/SDR encoder when SR is
  off. The custom temporal reference is bypassed when NRD is active.
- NRD works independently of SR. At native resolution this path uses unjittered
  pixel centers; NRD itself is a denoiser, not an added TAA pass. Vendor SR at
  100% can provide temporal antialiasing where supported.
- The native adapter creates the SDK-declared permanent/transient pools, exact
  per-pipeline root signatures, sampler states and descriptor tables. Constants
  and resources are reused, with SRV/UAV transitions and UAV ordering barriers
  for every dispatch. Imported textures enter and leave as non-pixel SRVs.
  Existing queue fences protect reuse and disposal. Reset uses the official
  `CLEAR_AND_RESTART`; resize/mode changes rebuild the size-dependent instance.

## Current glass and pipeline verification on Intel Arc 140T (2026-09-30)

`IntegrationProbe.exe --glass-sr` uses a unit-IOR glass slab in front of fine
emissive stripes (linear values 1.0 and 0.65). This isolates filtering from optical
distortion. Contrast is normalized to the glass-free reference; temporal RMS is
measured in linear red-channel units over the last 12 of 48 stationary frames,
on stripe interiors. It deliberately also runs the incorrect pane-guide path as
a negative control, which reproduces the loss of detail.

| Pipeline | Normalized contrast | Frame RMS |
|---|---:|---:|
| Incorrect pane guides, NRD | 0.02734 | — |
| Corrected NRD, native 100% | 0.99929 | 0.00077 |
| Corrected NRD + FSR 67% | 0.99790 | 0.00092 |
| Corrected NRD + FSR 100% | 1.00296 | 0.00121 |
| Corrected NRD + XeSS 67% | 0.99250 | 0.00561 |
| Corrected NRD + XeSS 100% | 0.99391 | 0.00534 |

- All positive glass cases execute 13 official NRD dispatches. Guide readback
  verifies opaque depth and closest-glass motion separately; independent camera
  projection checks unjittered motion. History reset and SR resize also pass,
  with no D3D12 Error/Corruption messages. A split transmission texture checks
  that opaque texels remain in the NRD guides; metallic non-transmitting areas
  are also retained. That check fails before the material-aware primary query.
- `--glass ../../Samples/RainyCorner/RainyCorner.glb` checks the actual refractive
  scene against an independently sampled 512-SPP reference. Across 2,991 glass
  pixels, RGB RMSE after `c / (1 + c)` compression is **0.217002** for raw 2 SPP
  and **0.083975** for the composed NRD result (previously **0.100277** without
  glass history). Non-unit IOR changes refraction;
  transmission/volume import and stable primary guides pass.
- The actual-scene noise regression measures the last 16 of 48 frames, after
  the same `c / (1 + c)` RGB mapping. At native 320×240, across 2,991 glass pixels,
  per-pixel temporal RMS falls from **0.051364** in the single-frame composition
  to **0.006472** after glass resolve; mapped mean ratio is **1.00644**. This
  regression fails on the previous composition, whose input/output RMS are equal.
  FSR and XeSS at 67% both reduce input-space RMS from **0.067893** to **0.052579**
  over 897 glass-interior pixels (214×160 input, before vendor SR); mapped mean
  ratio is **0.97425**. Reset matches the current raw glass composition. These
  SR figures measure the fallback resolve at input size, not display-space RMS.
- `--scene`, `--sr` and `--lighting` pass: transactional scene loading, packaged
  startup, camera/reset, hard shadows, ReSTIR and photon caustics, plus 60
  FSR/XeSS × five-scale × three-denoiser × SDR/HDR combinations. Odd dimensions,
  tiny input execution, history/disocclusion checks and mode transitions pass.
- The default probe passes all **18** model/denoiser/SDR-HDR hardware/software
  parity cases (maximum normalized RGBA RMSE **0.0007662**), resize and GPU HUD.
- With only the probe's NRD.dll temporarily renamed, `--nrd-missing` reports
  **Temporal** fallback and FSR still executes. The DLL was restored afterward.
- Native bridge, integration probe and WinUI Release builds pass. WinUI retains
  its existing generated WinUIEx obsolete `Icon` warning.
- DLSS SR and DLSSD execution were not tested on this Intel adapter. These are
  regression fixtures, not a general glass-quality or performance benchmark.

Run the probe from `diagnostics/integration`; captures and logs are in its ignored
`output/` directory. `--glass-sr` captures are under `output/glass-sr/`, while the
actual scene's raw, NRD and reference images are under `output/scenes/`.

## Historical mesh verification on Intel Arc 140T (2026-09-29)

- Real SDK available and active at native resolution; stable-frame dispatch
  count is 12. D3D12 debug Error/Corruption messages fail the probe.
- 80 SR cases: FSR/XeSS × five scales × four denoisers × SDR/HDR. An NRD selection
  must report an actual NRD context and dispatches, so fallback cannot pass.
- Odd sizes, 16×9 input, model switching, mode transitions and existing jitter/
  motion/history regressions remain covered. Extreme ratios test execution,
  not useful reconstruction quality.
- NRD guide readback checks signed view depth, sky exclusion, normal/roughness
  packing and real secondary-hit/miss distances.
- All 24 model/denoiser/SDR-HDR comparisons pass between hardware DXR and software
  BVH tracing (maximum normalized RGBA RMSE 0.001193), including leaving NRD for
  another denoiser. Resize and the GPU HUD pass in that probe.
- With NRD.dll temporarily absent from the probe output, the app explicitly
  falls back to its legacy filter and still executes FSR. The source SDK and
  normal deployment files are restored/retained.
- At 320×180, Bunny, 2 SPP, 10 bounces, after 64 frames: linear RGB RMSE against an
  independently sampled 512-SPP reference is **0.055809** for the last raw frame
  and **0.038540** for NRD; mean RGB energy ratio is **0.98931**. This is a regression
  check for one scene and settings, not a general quality/performance benchmark.
- Moving/zooming the camera produces finite output. Clearing history at a camera
  cut matches a fresh NRD context, including the one-bounce tracing case.
- Native and NRD+SR image captures were inspected. Release build passes with the
  pre-existing generated WinUIEx Icon warning. Sustained performance, subjective
  motion quality, physical HDR and slider interaction were not UI-tested.
- NVIDIA DLSS execution was not tested on this Intel device in that historical
  probe. DLSS Ray Reconstruction uses a separate pipeline.

Run `IntegrationProbe.exe --sr` from `diagnostics/integration` for the contract and
quality checks; run without arguments for model/backend parity. `--nrd-missing`
is a negative deployment check and expects NRD.dll to be absent from the probe's
output directory. Do not remove the vendored SDK file to run it.

Settings also use a **1–100% slider**, step 1%, for SR scale. The value is shown
beside it; arrow/PageUp/PageDown input is supported. Changes are applied after
200 ms without further input. SR Off keeps native size and disables the slider.

## Sources and build provenance

- [NRD upscaling, PSR, material demodulation and resolution guidance](https://github.com/NVIDIA-RTX/NRD#interaction-with-upscaling-dlssfsrxesstaau)
- [Official transparent composition sample](https://github.com/NVIDIA-RTX/NRD-Sample/blob/simplex/Shaders/TraceTransparent.cs.hlsl)
- [Pinned official NRD guide](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/README.md)
- [Pinned resource/API contract](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/Include/NRDDescs.h)
- [Camera/settings contract](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/Include/NRDSettings.h)
- [Rebuild and checksum instructions](../External/NRD/README.md)
