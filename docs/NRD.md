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

## Camera-motion history (2026-10-24)

The native ABI now carries both current and previous unjittered projection matrices.
Previously `viewToClipMatrixPrev` incorrectly reused the current projection; the
main and stable-plane motion shaders likewise projected the previous camera with
the current FOV. Stable-plane motion now follows the same contract as primary
motion: project the virtual surface through both unjittered cameras and subtract
current from previous, without adding pixel-center or previous-jitter offsets.
The C# and C++ frame layouts are both checked at 384 bytes.

RELAX uses 8 accumulated frames while the unjittered camera changes and restores
30 frames when it stops. This applies to primary, reflected and transmitted NRD
contexts; fast-history limits remain below the selected main-history limit. The
shorter moving history limits visible lag at 1 SPP without reducing stationary
accumulation. It is separate from the projection fix and from explicit cuts,
which still use `CLEAR_AND_RESTART`.

`--offline --motion` covers native 4K NRD and 1080p→4K NRD+DLSS with a fixed
warmup/orbit/pan/FOV/stop trajectory. It writes every frame and checks motion
vectors against independent CPU matrices, NRD activity, history-length changes
and D3D12 errors. `--nrd-motion` adds a noise-free world-anchored 4K signal so
reprojection correctness can be distinguished from low-SPP temporal response.
See [offline probe commands](../diagnostics/integration/README.md).
On the RTX 5070 Ti, the 64-frame native 4K run reports 0.1251 pixels maximum
sampled motion error, and the NRD+DLSS run reports 0.0628 pixels at its 1080p
input resolution. The corrected noise-free 4K sequence's maximum linear RMSE is
0.00204. Native readback confirms 8-frame histories during motion and restoration
of 30-frame accumulation after the camera stops.

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
   A stationary camera uses a same-pixel running mean with no history-length cap,
   bilinear history resampling or neighborhood clipping. Each frame receives
   weight `1 / frameCount`; two integer words retain the exact 64-bit count and
   compensated FP32 updates preserve small late contributions. Any change to the
   unjittered camera position, basis, FOV or clipping planes resets glass on that
   frame. SR sampling jitter does not reset it. A pixel that changes glass instance
   also starts fresh. Lighting/scene changes, sample/bounce changes and size/mode
   transitions retain the existing explicit reset.
6. Keep opaque depth for SR, but patch motion to the closest glass layer, as in
   the NRD transparent sample. Motion remains current-to-previous and unjittered.
   Glass uses a 0.1 reactive value so stable detail can retain temporal history.

NRD and SR have the same logical input/render resolution. SG resolve runs there
before the vendor upscaler; it does not claim to create display-resolution guides.
The first glass composition revision omitted the history for traced cache misses,
which left low-SPP reflection/refraction noise visible. The glass resolve now
stabilizes that contribution independently of the opaque NRD history. The later
64/8-frame capped revision still had a noise floor: resampling and clipping the
mean every frame prevented progressive convergence. The current accumulator
keeps the stationary mean intact and discards it immediately when the camera
changes. Curved/
refractive glass still follows the actual optical path;
the closest-glass motion is a heuristic rather than exact refractive scene flow.

Opaque and transparent guides are separate textures. The transparent guide stores
the closest glass position with `w = -(rayDistance + 1)`; sky remains `w = -1`.
Glass history stores a linear mean, integer frame count and FP32 compensation
with instance identity. It cannot reuse another pane's history merely because
the panes share a material. Reset prevents old lighting from surviving. Replacing
the old position/normal histories uses the same total texture bytes per pixel;
the render loop does not allocate managed buffers.
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
| Corrected NRD, native 100% | 0.99980 | 0.00071 |
| Corrected NRD + FSR 67% | 0.99790 | 0.00092 |
| Corrected NRD + FSR 100% | 1.00296 | 0.00121 |
| Corrected NRD + XeSS 67% | 0.99250 | 0.00561 |
| Corrected NRD + XeSS 100% | 0.99391 | 0.00534 |

- All positive glass cases execute 13 official NRD dispatches. Guide readback
  verifies opaque depth and closest-glass motion separately; independent camera
  projection checks unjittered motion. Translation (including a tiny change),
  rotation, roll, FOV and clipping-plane changes all reset glass on the first
  changed frame; stationary frames resume accumulation. SR jitter preserves the
  count. Explicit history reset and SR resize also pass,
  with no D3D12 Error/Corruption messages. A split transmission texture checks
  that opaque texels remain in the NRD guides; metallic non-transmitting areas
  are also retained. That check fails before the material-aware primary query.
- `--glass ../../Samples/RainyCorner/RainyCorner.glb` checks the actual refractive
  scene against an independently sampled 512-SPP reference. Across 2,991 glass
  pixels, RGB RMSE after `c / (1 + c)` compression is **0.217002** for raw 2 SPP
  and **0.076389** for the composed NRD result after 256 frames (previously
  **0.083975** with capped glass history and **0.100277** without it).
  Non-unit IOR changes refraction;
  transmission/volume import and stable primary guides pass.
- The actual-scene noise regression compares frames 33–48 and 241–256, after
  the same `c / (1 + c)` RGB mapping. At native 320×240, across 2,991 glass pixels,
  resolved temporal RMS continues falling from **0.005891** to **0.001617**;
  late single-frame raw RMS is **0.051466**, with mapped mean ratio **1.05396**.
  Every tested glass pixel reaches 256 accumulated frames. Against an independent
  CPU average of the raw glass compositions, maximum linear mean error is
  **7.963e-8**. Counts and this arithmetic-mean check fail with the old capped,
  clipped or repeatedly reprojected history.
- FSR and XeSS at 67% both continue reducing fallback-history RMS from
  **0.010725** at 48 frames to **0.005235** at 256. Across 878 pixels with an
  unchanged glass instance, counts reach 256 despite changing SR jitter;
  maximum error against the CPU fallback mean is **3.983e-8**. The complete
  input-space color, which retains current jittered cache detail, has late RMS
  **0.051422** versus raw **0.067169** over 897 glass-interior pixels; mapped mean
  ratio is **1.04996**. These figures are before vendor SR at 214×160 input,
  not display-space noise. Reset matches the current raw glass composition.
- `--scene`, `--sr` and `--lighting` pass: transactional scene loading, packaged
  startup, camera/reset, hard shadows, ReSTIR and photon caustics, plus 60
  FSR/XeSS × five-scale × three-denoiser × SDR/HDR combinations. Odd dimensions,
  tiny input execution, history/disocclusion checks and mode transitions pass.
- With only the probe's NRD.dll temporarily renamed, `--nrd-missing` reports
  **Temporal** fallback and FSR still executes. The DLL was restored afterward.
- Native bridge, integration probe and WinUI Release builds pass. WinUI retains
  its existing generated WinUIEx obsolete `Icon` warning.
- DLSS SR and DLSSD execution were not tested on this Intel adapter. These are
  regression fixtures, not a general glass-quality or performance benchmark.

Run the probe from `diagnostics/integration`; captures and logs are in its ignored
`output/` directory. `--glass-sr` captures are under `output/glass-sr/`, while the
actual scene's raw, NRD and reference images are under `output/scenes/`.

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


## 2026-10-30：光栅一跳 GI 的独立 RELAX 上下文

光栅硬件 GI 现在默认使用固定 **NRD 4.17.3 / RELAX_DIFFUSE_SPECULAR**（非 SH），由 `RasterHardwareDenoiser` 持有独立上下文。它复用现有 `NativeNrd` 与原生 `GetComputeDispatches` 执行器，不借用路径追踪/稳定面的历史，也不升级或重建厂商 SDK。选择普通 radiance 模式是因为当前一跳管线直接输出双 lobe 辐亮度，尚未提供 SH 方向矩；不把普通 RGB 强行声明为 SH。未来的 SG resolve/re-jitter 可以另行按输入契约接入。

流程为 `opaque GBuffer → 硬件 GI 双信号 → 材质去调制/官方打包/opaque guides → 官方 RELAX 全 dispatch → 材质还原 → 加回直接光与 emission → 前向透明 → 公共 TAA/SR/显示`。公共 TAA 仍负责抗锯齿；NRD 有效时跳过此前 5×5 双边 GI resolve，避免两套空间过滤叠加。

- Diffuse 保存原 E/π 估计，再按当前 diffuse 因子还原为出射光后用 `NRD_MaterialFactors` 去调制；specular 为 BRDF/PDF 加权的出射光，除以匹配的 specular 因子。两者使用 `RELAX_FrontEnd_PackRadianceAndHitDist` 打包，输出经 `RELAX_BackEnd_UnpackRadiance` 解包并乘回同一因子；禁止对最终合成颜色直接模糊。
- 双信号 alpha 已从占位常数 1 改为真实首个次级 hit distance（世界单位，无 PDF 权重、不含相机距离）；miss 为 65504，几何法线拒绝或无有效镜面方向时为 0。面积光与环境 NEE 的方向不冒充 hit-distance 采样方向；现阶段使用对应余弦/VNDF 样本作距离 guide。SDK 的 AREA_3X3 重建处理无有效方向的像素。
- Guides 来自 opaque GBuffer：官方 normal/roughness 打包、右手系负线性 viewZ（sky=-1000000 或更远）、当前→上一帧的输入像素 motion（无 jitter）。NRD 原生桥缩放为 UV；current/previous 的 view 与 projection 独立提交，NRD 使用当前/上一 ray jitter。前向玻璃尚未覆盖这些 guides。
- 场景/灯光/camera cut/配置显式 reset 清历史；尺寸变化重建上下文。静止历史 30 帧、相机变化 8 帧，继续复用原生设置策略。所有命令录制在 RasterBackend 当前列表上，沿原同步 fence 保护 SDK 池、描述符、导入纹理与销毁；不添加新队列或逐 pass CPU 等待。
- 输入/输出纹理进出 NRD 均为 NonPixelShaderResource，转换/打包阶段显式配对 UAV barrier。C++/C# `NativeNrd.Frame` 仍为 384 字节，新增导出 `NrdCreateRadiance` 不改变原 `NrdCreate` 与 `NrdCreatePbr` 的算法选择。
- NRD DLL 缺失、架构/入口不匹配或初始化失败时，硬件 GI 使用现有双边过滤+公共 TAA/SR 回退；HUD 与共享 ReconstructionStatus 报告实际 NRD 状态及原因。状态快照仅在变化时创建。`--require-nrd` 与 `--raster-nrd` 必须有真实 SDK dispatch，回退即失败。
- SDK 池之外，NRD 适配器拥有 48 bytes/pixel 的打包/normal/viewZ/motion/双输出纹理，720p 为 44236800 字节（不含驱动对齐与 SDK permanent/transient pools）；随尺寸归还。原始 GI 双纹理另计。未测量热路径 GC 分配，不宣称零分配。

### 本次离屏证据（Intel Arc 140T，报告 15GB）

`--raster-nrd` 在 64×48 跑 32 帧，不接 TAA；同像素、同随机帧序号对照旧双边 GI resolve。舍弃前 16 帧，统计接收面内部 24×18 区域的红通道帧间 RMS 与平均亮度：

| 指标 | 原始结果 |
| --- | --- |
| 双边基线帧间 RMS | `0.19084694496942395` |
| NRD 帧间 RMS | `0.001620510324862552` |
| 双边基线平均值 | `1.247356485437464` |
| NRD 平均值 | `1.2340340790925202` |
| 平移 motion 对独立 CPU 投影的误差 | `0.00085258484` 输入像素 |
| reset/resize | 清除旧照明，重建实际上下文；resize 首帧 32 个含清理的 SDK dispatch |

上述小场景数据不是任意资产/运动的收益比例，也不是图像相对于高 SPP 参考的 RMSE。静止 jitter 的 motion、负 viewZ、移动时 8 帧历史、回到静止 30 帧与显式 reset 均有断言，D3D12 Error/Corruption=0。

Kitchen（额外太阳关闭）与 Bistro（太阳方位 135°/仰角 65°）各用 1280×720、8 帧、1024 上限纹理、1024/256 阴影、环境 1、曝光 0 验证；`--require-nrd` 强制真实 NRD，稳态均为每帧 13 个官方 dispatch，D3D12 Error/Corruption=0。PNG/JSON 在 ignored `output/raster-validation/`，含 `-nrd` 后缀；`--no-nrd` 可保留同输入的双边基线。8 帧只是有限负载的冷启动预览，仍可能有低频斑块、偏软的粗糙镜面和未收敛区，复杂运动/消遮挡/多显示器未在本次遍历。

`--raster-nrd-missing` 临时移走的仅是探针输出 NRD.dll：验证 GI 回退且真实 FSR 继续执行，之后恢复并核对 SHA-256 完全一致。固定 External 目录未修改。原有 `--nrd-motion --width 128 --height 96 --frames 25` 的单 diffuse NRD 路径通过，最大线性 RMSE=`0.005412440793496404`；`--common-frame` 的真实 FSR/XeSS、切换、resize/HDR、WARP 与 D3D12 检查通过。本机不支持 NVIDIA DLSS，未宣称 DLSS RR 或完整 PT SH/稳定面回归已执行。

### 本机原生桥构建记录

新增 C++ 导出触发重链接后，本机 VS 18/v145 报 `LNK1101 MSPDB140.DLL` 版本错误。使用已安装 VS2022 的 MSBuild，单次输入 `/p:PlatformToolset=v143 /p:VCToolsVersion=14.44.35207 /p:PreferredToolArchitecture=x64` 成功构建 **应用自己的 Reconstruction 桥**，未改工程 toolset、SDK 版本或厂商文件。随后主应用 Debug/Release 与探针使用 `-p:SkipReconstructionNativeBuild=true` 复用这份刚构建的桥，均构建成功；已有生成 XAML 的 Icon CS0618 警告仍在。健康 v145 环境仍走标准自动构建入口。
