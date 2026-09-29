# Official NVIDIA NRD integration

The default mesh denoiser is now **NVIDIA NRD 4.17.3 / RELAX_DIFFUSE**. Select it
in Settings → 降噪. The existing temporal and RELAX-style filters remain separate
comparison options. SDK absence or failure produces an explicit legacy-filter
fallback in the status/HUD; the UI disables unavailable SDKs.

The current mesh materials implement diffuse reflection only. RELAX_DIFFUSE is
the complete official algorithm for that signal, including prepass, reprojection/
accumulation, history repair/clamping, anti-firefly and A-trous filtering. The
adapter consumes every dispatch returned by `nrd::GetComputeDispatches`; stable
frames currently contain 12 dispatches. This does not expose every NRD algorithm
in the UI: REBLUR, SIGMA, specular and SH variants require their corresponding
signals and integration. Specular transport is not fabricated by filling a
second channel with the same diffuse image.

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
  off. The custom temporal/A-trous filters are bypassed when NRD is active.
- NRD works independently of SR. At native resolution this path uses unjittered
  pixel centers; NRD itself is a denoiser, not an added TAA pass. Vendor SR at
  100% can provide temporal antialiasing where supported.
- The native adapter creates the SDK-declared permanent/transient pools, exact
  per-pipeline root signatures, sampler states and descriptor tables. Constants
  and resources are reused, with SRV/UAV transitions and UAV ordering barriers
  for every dispatch. Imported textures enter and leave as non-pixel SRVs.
  Existing queue fences protect reuse and disposal. Reset uses the official
  `CLEAR_AND_RESTART`; resize/mode changes rebuild the size-dependent instance.

## Verification on Intel Arc 140T (2026-09-29)

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
- NVIDIA DLSS execution is not tested on this Intel device. NRD is independent
  of DLSSD; DLSS Ray Reconstruction remains a separate, unimplemented feature.

Run `IntegrationProbe.exe --sr` from `diagnostics/integration` for the contract and
quality checks; run without arguments for model/backend parity. `--nrd-missing`
is a negative deployment check and expects NRD.dll to be absent from the probe's
output directory. Do not remove the vendored SDK file to run it.

Settings also use a **1–100% slider**, step 1%, for SR scale. The value is shown
beside it; arrow/PageUp/PageDown input is supported. Changes are applied after
200 ms without further input. SR Off keeps native size and disables the slider.

## Sources and build provenance

- [Pinned official NRD guide](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/README.md)
- [Pinned resource/API contract](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/Include/NRDDescs.h)
- [Camera/settings contract](https://github.com/NVIDIA-RTX/NRD/blob/792eff196afdd350fd9c3f862119017ccb438a0e/Include/NRDSettings.h)
- [Rebuild and checksum instructions](../External/NRD/README.md)
