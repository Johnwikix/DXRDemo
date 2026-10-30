# Offline RTXPT validation

`--raster` 检查真实 LOD0 光栅：GBuffer/PBR、reverse-Z 与极端 near/far 精度、非零 mesh vertex/index offset、去抖 motion、视锥/镜像/单双面、材质贴图、MASK/BLEND、独立折射快照、CSM/点光/聚光阴影，以及 70 灯簇容量溢出时的能量守恒。

`--raster-lighting` 是低负载离屏照明回归：128×96、256/128 阴影，验证常量环境 E/π/GGX 能量、SSR 的真实发光面命中/禁用/物体移除/抖动/确定性/resize，以及 12 帧透明棋盘的 NativeTAA 稳定性、最近透明面 depth/motion 与零 alpha guides。无需 AS，D3D12 Error/Corruption 必须为 0。

`--raster-hardware` 要求真实 DXR 执行，在 64×48 的小场景中验证环境几何遮挡、屏幕外墙面的颜色反弹、发光面 MIS、相机后方且被裁剪物体的 Hit Lighting 反射、物体移除、共享 AS、resize，以及太阳/局部光/透明合成组合；D3D12 Error/Corruption 必须为 0。它与 `--raster-lighting` 的无 AS 降级检查分别验收，不把环境占位当作 GI。

`--raster-preview`（可加 `--bistro` 或 `--rainy`，默认 Kitchen）现在默认渲染 8 帧 **1280×720**：纹理上限 1024、阴影 1024/256、环境 1、曝光 0、太阳开启；支持 DXR/SM6.5 时使用一跳硬件光照，否则为 SSR/环境降级。`--no-hardware-gi`、`--no-sun`、`--sun-azimuth`、`--sun-elevation` 可用于固定输入消融；`--width`、`--height`、`--frames`、`--texture-size`、`--shadow-size` 有显式上限。Bistro 投影检查示例：`./IntegrationProbe.exe --raster-preview --bistro --sun-azimuth 135 --sun-elevation 65`。Kitchen 的模型灯检查可用 `./IntegrationProbe.exe --raster-preview --no-sun`。输出 PNG/JSON 到 ignored `output/raster-validation/`，文件名记录分辨率与照明模式；不启用厂商 SR 或持续呈现。

先构建主应用，再用 `dotnet build diagnostics/integration/IntegrationProbe.csproj -c Release --configfile diagnostics/NuGet.Config` 构建探针；**切到探针 exe 所在目录运行**，保证 Agility `./D3D12/` 有效。缩小的阴影预算与有限帧不是生产性能基准；逐帧时间戳、当前一跳 GI 的噪声/缓存/透明限制见[设计第 16 节](../../docs/RASTER_PIPELINE_DESIGN.md#16-2026-10-30720p-验证与一跳硬件光照基线)。

`--common-frame` 检查共享 LinearFrame 与 NativeTAA 的历史/响应/遮挡失效/cut，SDR/HDR 对照独立 CPU 编码，真实 FSR/XeSS/DLSS SR 与路径 DLSSD（不允许回退），跨管线共享相机/GPU 资源、失败候选、尺寸/HDR 及 WARP 无 AS 出图。WARP 是否报告 DXR 由运行日志给出，不把它当作无 DXR 物理设备的覆盖。所有新增 GPU 模式均要求 D3D12 Error/Corruption=0。

`--raster-scenes` 在 160×100、无 jitter、固定相机下，逐一对照四个封装资产的光栅深度与既有 DxrSceneBackend 主射线。材质副本关闭 alpha/透射以隔离几何；透明由 `--raster` 单独覆盖。深度断言计入 1/256 像素取整误差，薄片遮挡变化使用独立 CPU 三角形固定网格投影/插值复核（相对 4e-6 加绝对 1e-10），不把连续射线深度当成逐位相同的硬件光栅结果。CPU 参考只处理当前回查样本的未跨近面三角形；缺少可验证参考仍触发失败。覆盖率必须超过 .98，各场景有效深度比较超过 100。

`--scene-session` 是 2026-10-28 新增的无 GPU 事务回归：解析/上传失败、取消、旧解析与旧上传迟到、失败导入恢复目录选择、关闭与空闲帧复用；还检查多材质逻辑网格、同名同变换但不同节点的身份、重新导入的稳定性与未知身份回退。

`--scene-session-gpu` 用 64×48 原生帧执行真实 GPU 上传、非法候选失败恢复、导入目录提交及共享会话借用生命周期；要求 D3D12 Error/Corruption 为零。该模式固定关闭 SR/降噪以隔离场景事务，不代表厂商 SDK 或画质回归。

`--gpu-scene-cache` 验证一个共享设备上的公共 GPU 场景租约、按需 LOD0 BLAS/TLAS、两个真实 DXR 后端的独立可写描述符、交错渲染像素完全一致、缩放/消费者销毁隔离、取消/失败/放弃候选、缓存先关闭和最后租约释放。空闲项必须为 0；不同设备用 WARP 身份验证拒绝共享，不用 WARP 渲染。要求 D3D12 Error/Corruption 为零，不作性能或异步退休结论。

`--scene-core` 执行 `--scene` 前半段现有的完整资源/材质/灯光断言（glTF/GLB、实例与 mip、MASK/BLEND、金属/自发光、点/聚光/方向光、太阳阴影、三种降噪及真实 NRD），末尾核对 debug queue。它不执行后半启动/导入/厂商模式检查，不能计作完整 `--scene` 通过；默认入口和阈值不变。

`--hud` 验证原生 GPU 诊断信息的字体/字符常量缓冲布局，逐像素对照已知字形，覆盖非四元素对齐的字符、跨文本行、1×/2× 尺寸、SDR/HDR 编码白值及生产 HUD 绘制；要求存在实际亮字形像素，且 D3D12 Error/Corruption 为零。它验证显示契约，不把 HUD 的 FPS 计数当作基准。

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

For single-frame 4K validation, use `--offline --bistro --production --dlss-4k`:
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
pipeline creation occurs before the traced frame. GPU timestamps cover trace plus
reconstruction work, excluding asset load, shader compilation, CPU checks, PNG
encoding and presentation. They are not a sustained FPS measurement.

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
resets, resize, NRD transitions and an explicit unavailable adapter.
`--temporal-scene --optix`
captures actual Bistro checkpoints at 1/4/8/16/32/64/... frames and reports CUDA
inference time separately from the complete reconstruction GPU stage.
Use `--output <directory>` to avoid overwriting other ablation captures.

Known baseline failures on the current renderer: `--glass-sr` fails the rough
glass detail contrast assertion (0.02541), and `--lighting` fails the old ReSTIR
convergence assertion. Both reproduce with the pre-OptiX production shader files.
The targeted OptiX/thin-sheet transmission checks do not count these as passing.
`--sun-shadow` and `--sun-glass` are independent entry points.

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
`IntegrationProbe.exe --temporal-convergence` feeds deterministic noisy samples
through the production linear Temporal shader at native resolution. It checks
RMSE at 32 and 256 frames against both the known reference and an independent CPU
arithmetic mean, then verifies per-pixel counts and the two-sample mean immediately
after a depth discontinuity. It rejects fixed-frame-count noise floors, stationary
history clipping and global-frame-count reuse after history rejection. On the
RTX 5070 Ti, linear RMSE falls from 0.024807 to 0.008759 (CPU-mean RMSE
1.269e-6), and the post-disocclusion two-sample error is 1.376e-8.

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
official NRD.

`IntegrationProbe.exe --lighting` exercises ReSTIR DI and refractive photon caustics
with synthetic fixtures and the D3D12 debug layer. It checks many-light and emissive
area-light energy, history/camera cuts, lobe reconstruction, glass focusing, IOR=1,
light removal and resize. See [implementation and results](../../docs/RESTIR_CAUSTICS.md).
The `DXR_DXC_PATH` environment override is honored by all probe modes.

The lighting suite includes raw-radiance regressions for a wet floor behind
parallel glass (including mirrored/scaled and densely tessellated variants),
plus static photon reuse and light-change invalidation. These fail on the original
photon-blotch implementation; the glass-sphere caustic focusing fixture must
continue to pass.

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

The main window's **设置** button or **F1** opens settings. Closing settings hides
that window and keeps rendering. Closing the main window closes settings and
releases the renderer.

The GPU HUD is a compute pass writing a bitmap font into the frame UAV after
denoising. Text changes twice per second. There is no XAML frame counter. It shows
the backend, model, triangle count, SPP, bounce limit, denoiser, resolution and HDR
mode. RENDER counts completed render-loop frames; SUBMIT counts successful
`Present` calls, which is not the monitor's display rate. FRAME includes rendering,
denoising, synchronization, HUD and submission; it is not a GPU timestamp result.

## Run the verification probe

From this directory:

```powershell
dotnet build IntegrationProbe.csproj -c Release --configfile ../NuGet.Config
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe
```

Build DXR first so its bundled DXC exists. Model assets are copied into the probe's
output directory by MSBuild. `ui_settings.py MAIN_HWND SETTINGS_HWND MODEL DENOISER
HDR` drives actual lvt controls using a fresh UIA tree before each action.

Model attribution and terms are bundled with the assets in `SOURCES.md`.
