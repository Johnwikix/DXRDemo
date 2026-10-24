# Cycles OptiX 与 Temporal 颗粒诊断

2026-10-24。已检查 Blender `main` 的 Cycles 源码，并新增独立 OptiX AOV 模式。
在 RTX 5070 Ti / NVIDIA 617.14 上执行了实际推理与 D3D12/CUDA 互操作验证。

## 使用与实现

设置 → 降噪 → **NVIDIA OptiX · AI 降噪（渐进累积）**。先关闭 DLSS 光线重建，
它有自己的降噪流程。原生分辨率下，静止画面持续累计原始线性 HDR，再运行 OptiX；
相机、场景、光照、采样参数或 raster jitter 改变时重置相关累计。AI 输出从不写回
原始历史。开启 FSR/XeSS/DLSS SR 时使用逐帧 OptiX 重建，再交给 SR；变化的 jitter
会重置原始渐进输入，但不会清空 SR 所需的相机运动向量。

新选项的索引为 3。`DenoiserCatalogVersion=1` 区分旧配置中曾表示 NRD 的索引 3，
避免旧用户设置被误迁移成 OptiX。NVIDIA 驱动/适配器不可用或创建、执行失败时，
状态栏明确报告时间累积回退；编码空间的旧球体示例不提供 OptiX 输入。

`Native/Reconstruction/Optix.cpp` 动态加载 CUDA driver API 与 OptiX 9.1 AOV 模型。
共享 D3D12 线性缓冲包含 beauty、albedo、normal、output 四个 float4 平面；
D3D12/CUDA 通过共享 fence 交换所有权。pack/unpack 各有独立 command allocator，
CPU 只在原生阶段的最终输出处等待，不下载/上传图像。1080p 共享缓冲约 126.6 MiB，
另需 denoiser 的 state/scratch 与原始累计纹理。CUDA events 记录推理时间，D3D12
timestamps 记录包含互操作的阶段耗时，后者计入应用的重建 GPU 诊断。

输入 feature 与 NRD 数据约定分开：OptiX 路径生成 unlit reflectance，包含金属的
反射颜色；法线归一化并转换到相机空间。光滑玻璃/镜面沿主导理想路径寻找可见表面，
最多 8 次引导界面，并折叠法线。这个引导是近似实现，不等同于 Cycles 完整的
多 closure 加权；粗糙透射、覆盖率混合及复杂多层反射仍需要单独评估画质。

SDK 头文件从官方固定提交恢复并验证 SHA-256，不随源码或应用分发；构建开启
OptiX 适配器需要 `CUDA_PATH/include/cuda.h`。没有 CUDA 开发头文件时可构建不可用
适配器，已单独编译验证。运行应用不需要安装 CUDA Toolkit，也不链接 cudart。
详见 [构建依赖](../External/OptiX/README.md)。

实际 GPU 合成测试（128×96，8 帧）中，原始均值 RMSE 为 0.139481，OptiX 输出
为 0.008517；原始历史相对独立 CPU 均值的 RMSE 为 2.002e-8。尺寸变化、相机与
jitter 重置、原始历史隔离、真实 dispatch 计数和不可用回退均有回归。
这些结果不代表所有场景或分辨率的固定性能/画质收益。

Bistro 同输入实景对照（在下述背面阴影 Fresnel 修复前采集）：1920×1080、每帧 1 SPP、8 bounces、64 帧，对同一张
生产路径 256-SPP 独立种子参考使用 `x/(1+x)` 误差：

| 项目 | Temporal | OptiX |
| --- | ---: | ---: |
| 参考 RMSE | 0.014692 | 0.009870 |
| 最后 16 帧输出 RMS | 0.002877 | 0.000614 |

第 64 帧 CUDA 推理为 6.55 ms，包含 pack/unpack 与编码的重建 GPU 阶段为
7.37 ms；同帧追踪为 19.75 ms。这里只报告该配置的采样，不能外推所有设备或
连续呈现帧率。256-SPP 参考仍有噪声，32.8% 的误差下降不等于相对无噪声真值。
结果位于 `diagnostics/integration/output/optix/bistro-final-optix` 与
`bistro-final-temporal`，原始输入的 checkpoint 统计相同。

## Bistro 薄片玻璃修复

Bistro 的多种橱窗材质使用零厚度、零粗糙度透射。旧 `Scatter` 在该分支使用
`-reflect(-view, micro)`，斜视时会翻转切向分量，令实际 beauty 射线采样错误的
室内位置，同时与直通的降噪引导不一致。现在光滑薄片沿 `-view` 直通，保留原有
Fresnel/颜色权重；有厚度的体积玻璃仍按 Snell 折射。薄壁与体积的区分见
[glTF volume 规范](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_materials_volume)。

新增斜视薄片 GPU contract 在旧代码上首先失败（方向误差 0.436436），修复后
在 inline 与 legacy traversal 均通过。单位 IOR 的实际薄片/条纹背景对照中，
原始累计相对无玻璃参考 RMSE 为 0.003368，OptiX 输出为 0.001838，平均透射亮度
比例为 0.9998；另断言生成 OptiX 引导不会改变原始玻璃辐射。

后续用户报告“可透过窗户看见室内，但阳光没有照进去”。这涉及另一条阴影路径：
`Visibility` 对薄片背面使用 `IOR` 而不是 `1/IOR`，误认为射线从玻璃内部向空气
传播。Bistro 的 IOR 1.9/2.0 窗片在常见太阳入射角下因此出现虚假的全反射。
现在使用 `Evaluate` 已计算的 `s.eta`，零厚度窗片的两侧均为空气→玻璃界面，
有厚度玻璃仍保留入射/出射区分。这也修正环境光阴影查询中的同一问题。

`--sun-glass` 使用实际方向太阳和阴影射线，在 48.5°、IOR=1.9 的薄片对照中，
修复前背面透光为 0、翻面为 0.886646；修复后两侧均为 0.886646，不透明遮挡
保持完全挡光。inline 与 legacy 两种遍历均通过。
太阳本身仍是无限远方向光的硬阴影近似，没有太阳位置或距离衰减；有限角尺寸的
太阳光盘是另一项模型扩展，不应以近距离球灯代替。

补充检查发现两项基线问题：`--glass-sr` 的 NRD 粗糙玻璃条纹对比度检查失败
（对比度 0.02541），`--lighting` 的旧 ReSTIR 收敛检查失败。用修改前的
`SceneTrace.hlsl` / `PathBsdf.hlsli` 重跑，两项均以相同结果失败。平板玻璃直射
对照的三组几何检查通过。这些既有失败没有作为 OptiX 测试通过来计数。

## Blender 的快速视觉收敛来自哪里

OptiX 是训练好的 AI 图像重建器。它能利用图像的空间结构，在 Monte Carlo 原始信号仍有噪声时输出更平滑的估计；这与增加样本、使原始估计收敛是两件事。低样本下仍可能丢细节或产生重建偏差，不能承诺固定 SPP 的普遍画质等价。[NVIDIA OptiX](https://developer.nvidia.com/rtx/ray-tracing/optix)

Cycles 的调用链是：

```text
累计路径样本与 denoising passes
  → 按实际样本数读取 noisy color，准备引导数据
  → OptiX 推理
  → 写入独立 denoised pass
  → 显示转换
```

`DenoiserGPU::denoise_color_read` 使用 `PassAccessorGPU` 读取 noisy pass，并把结果写入 denoised offset；OptiX 随后在这个输出区域处理。不能把“在 denoised offset 原地运行”误解为把 AI 输出回灌到原始采样累加器。[denoiser_gpu.cpp](https://github.com/blender/blender/blob/main/intern/cycles/integrator/denoiser_gpu.cpp)

`OptiXDenoiser::denoise_create_if_needed` 在普通非时域、非放大分支选择 `OPTIX_DENOISER_MODEL_KIND_AOV`。引导输入可选 Albedo、Normal；时域分支还需 motion/flow 与之前的输出。`DenoiseParams` 的 `temporally_stable` 默认是 `false`，不能把 SDK 支持时域模型等同于默认通过更长历史去噪。调用时 `OptixDenoiserParams` 零初始化，`blendFactor=0` 对应使用完整降噪结果。[denoiser_optix.cpp](https://github.com/blender/blender/blob/main/intern/cycles/integrator/denoiser_optix.cpp)、[denoise.h](https://github.com/blender/blender/blob/main/intern/cycles/device/denoise.h)、[参数定义](https://raytracing-docs.nvidia.com/optix8/api/struct_optix_denoiser_params.html)

Blender 中开放的是上述集成与特征生成代码；本项目不能靠复制这几段包装代码，在 HLSL 中得到同一个训练模型。

## 高质量依赖的输入与调度

- **专用引导特征。** Cycles 按 BSDF closure、透明度和粗糙度生成 denoising features；法线转换到相机空间。启用 follow reflections 时，部分特征沿反射/透射继续传递，而不是总停在玻璃表面。它们不是直接照抄一个 baseColor 纹理。[denoising_passes.h](https://github.com/blender/blender/blob/main/intern/cycles/kernel/film/denoising_passes.h)
- **生命周期复用。** denoiser handle 只在模型/引导配置变化时重建，state/scratch 按尺寸配置并复用；大图以不超过 4096 的 tile 和 overlap 运行。整帧自动强度处理、HDR 输入约束及 tile 边界应遵循 SDK 契约。[OptiX 集成](https://github.com/blender/blender/blob/main/intern/cycles/integrator/denoiser_optix.cpp)
- **显示更新有预算。** Cycles 区分采样工作与降噪/显示工作；达到 Start Sample 后启用降噪，静止后期会节流重复降噪。导航时的代码明确优先考虑更高分辨率、较少样本，因为这对空间降噪的视觉结果更好。[render_scheduler.cpp](https://github.com/blender/blender/blob/main/intern/cycles/integrator/render_scheduler.cpp)
- **采样端也降低方差。** Cycles 有位置相关的 light tree；可配置的 direct/indirect contribution clamp 在贡献写入前执行。这些是输入质量措施，不是 OptiX 网络的内部实现，也不表示默认会启用任意强度的裁剪。[tree.h](https://github.com/blender/blender/blob/main/intern/cycles/kernel/light/tree.h)、[light_passes.h](https://github.com/blender/blender/blob/main/intern/cycles/kernel/film/light_passes.h)

## 本项目复现与修复

截图为 Bistro、native、TemporalOnly、1 SPP、8 bounces。固定默认相机的 640×360 对照也出现了明显颗粒，但没有复现用户移动后的完全相同视角。

已区分出两个因素：

1. **静止历史发生空间扩散。** 原来的 `Prepare` 在相机与 jitter 都不变时仍双线性重投影。世界坐标到栅格坐标的舍入误差被每帧重复应用，亮样本会渗到本来没有贡献的邻居。帧数计数正确不能排除这个错误。
2. **输入有高方差的稀疏亮贡献。** 关闭发光材质、只保留 HDR 环境、限制一次反弹后，单帧约 0.8% 的像素有非零贡献；少数贡献的亮度很高。新位置不断遇到亮样本，旧样本在均值中又消退很慢。纯 Temporal 没有空间重建能力，修正历史扩散后仍然保留这种输入噪声。

`Reconstruction.hlsl` 现已在相机和 jitter 均不变时直接读取同像素历史，并保留深度/天空兼容性检查。相机或 jitter 改变时继续走原来的重投影路径。没有降低全局 firefly 阈值，没有添加空间模糊。

真正触及历史错误的 GPU 回归：64×64、偏离原点的相机、首帧稀疏亮点，之后 31 帧输入全黑。

| 检查 | 修复前 | 修复后 |
| --- | ---: | ---: |
| 原本为黑的邻居最大泄漏 | 0.002126 | 0 |
| 亮点与独立算术均值的最大误差 | 0.003688 | 0 |

既有 32→256 帧线性测试仍从 RMSE 0.024807 降至 0.008759；与 CPU 均值的 RMSE 从 1.269e-6 改善至 1.025e-7。另检查了深度断层后重新计数，以及相机不动但 jitter 改变时仍正确重投影。

真实 Bistro 256 帧对照仍有明显输入噪声；本次修复不是“256 SPP 已经干净”的证据。只保留环境的一次反弹诊断中，修复前 256 帧出现了 491 个亮度大于 1e-4、但精确同像素均值低于 1e-5 的额外泄漏像素。这个统计只用于同输入对照，不是一般画质指标。

排查还包括关闭自适应选光、关闭法线贴图、使用未压缩纹理，以及仅在诊断输出副本中将射线起点偏移放大 64 倍；这些均未消除主要输入颗粒。把 firefly 阈值从 64 改成 1 会使这组 8-bounce 测试平均线性能量下降超过一半，因此未将它作为生产修复。

独立的 GPU 环境采样检查跳过 BSDF 与遮挡，比较 `Sky(direction)/pdf` 的均值与 CPU 球面积分。Shanghai HDR 的 RGB 积分分别为 GPU (11.837221, 9.561598, 9.268904)、CPU (11.774305, 9.510217, 9.151509)，约 0.5%–1.3% 的差异。它排除了明显的环境采样能量错误，不能证明真实场景的可见性估计没有高方差。

## 接入采用的数据流

保留 Temporal 作为可核对算术均值的模式，OptiX 使用独立降噪输出：

```text
DXR 原始线性 HDR 样本
  → FP32 原始累计均值 + 对齐的累计 Albedo/Normal
  → OptiX AOV 模型
  → 独立显示输出
  → 曝光 / tone mapping / PQ 或 sRGB
```

本次采用单帧 AOV 模型；之后可按交互需求评估时域模型。AI 输出不写回原始累计历史。

现有 NRD 的 diffuse demodulation factors、打包的 normal/roughness 与 diffuse-only albedo 不能直接当成 OptiX 引导。应提供满足 OptiX 契约的线性反射率、法线和一致的像素采样；玻璃和镜面需要专门检查特征归属。

D3D12 渲染器可以保留。通过原生桥把数据写入可共享的线性 buffer，使用 CUDA external memory 导入，并用共享 D3D12 fence / CUDA external semaphore 串联两侧 GPU 工作。OptiX 使用 CUDA 可访问的图像内存，不能直接把当前 `ID3D12Resource*` 当图像指针传入。避免每帧 CPU 下载再上传；复用 denoiser、buffer 和 scratch。[CUDA 外部资源互操作](https://docs.nvidia.com/cuda/cuda-programming-guide/04-special-topics/graphics-interop.html)

验收应按同一相机、种子、分辨率与光照比较 1/4/8/16/32/64/256 SPP 的达到目标画质时间，同时观察高光、暗部、细线、玻璃和纹理细节。参考图需要独立、高样本且已检查残余噪声；现有 128/256/512-SPP production 均值及 NRD 输出均不能直接充当无噪声真值。

## 复跑

从 `diagnostics/integration` 运行 Release 的 `IntegrationProbe.exe`：

```powershell
IntegrationProbe.exe --temporal-convergence
IntegrationProbe.exe --environment-sampling
IntegrationProbe.exe --optix
IntegrationProbe.exe --sun-glass
IntegrationProbe.exe --sun-glass --legacy-trace
IntegrationProbe.exe --temporal-scene --optix --width 1920 --height 1080 --frames 64 --reference-frames 8 --reference-spp 32 --spp 1 --bounces 8 --output output/optix/bistro-final-optix
IntegrationProbe.exe --temporal-scene --width 640 --height 360 --frames 256 --reference-frames 8 --reference-spp 32 --spp 1 --bounces 8 --capture-linear --output output/firefly-diagnosis/fixed-baseline
```

场景探针另支持 `--no-emission`、`--no-environment`、`--no-normal-maps`、`--uncompressed`、`--no-adaptive-lighting`、`--no-caustics`、`--firefly-threshold`。`--output` 隔离每组产物；`--capture-linear` 写出 width×height×RGBA 的 little-endian float32 样本，便于排除 PNG/tone mapping 对统计的影响。
