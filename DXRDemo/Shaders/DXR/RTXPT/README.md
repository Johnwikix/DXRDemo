# RTXPT shader dependencies

Source: https://github.com/NVIDIA-RTX/RTXPT

Pinned revision: `f08d1c739071e0faad0c7c274d861124c511abab` (RTXPT 1.8.1).

This software contains source code provided by NVIDIA Corporation.
The original copyright notices are retained. See `Licenses/RTXPT.txt` in the
application output (source: `DXRDemo/Licenses/RTXPT.txt`). The Intel MIT notice
in `NoiseAndSequences.hlsli` is also retained.

| Local file | Upstream path under `Rtxpt/Shaders/PathTracer` | Integration change |
| --- | --- | --- |
| `Microfacet.hlsli` | `Rendering/Materials/Microfacet.hlsli` | Replace Donut configuration dependency with `K_PI` |
| `BxDFConfig.hlsli` | `Rendering/Materials/BxDFConfig.hlsli` | Unmodified |
| `NoiseAndSequences.hlsli` | `Utils/NoiseAndSequences.hlsli` | Remove unused `Utils.hlsli` dependency |

The surrounding `PathSampling`, `PathBsdf` and `PathState` files are the
renderer-owned bridge to the C# scene layout. This is not the complete RTXPT
renderer, and does not link Donut, NVRHI, RTXDI or a neural radiance cache.

`../PathFirefly.hlsli` adapts `ComputeNewScatterFireflyFilterK` and `FireflyFilter`
from upstream `PathTracerHelpers.hlsli`, retaining NVIDIA attribution and using
the same SDK license. The priority interior list, feedback proposals, environment
alias table and three-branch NRD integration are renderer-owned implementations
informed by the corresponding RTXPT core algorithms, not copies of its SDK integration.
