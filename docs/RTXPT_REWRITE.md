# RTXPT 参考重写

实现位于 `master`，保持该分支不含 NRC。参考代码是 RTXPT 1.8.1，固定提交
`f08d1c739071e0faad0c7c274d861124c511abab`，资产固定在
`2a38b92df0fb43ebf6c6e6603893052dacf5a4c8`。

## 渲染实现

C# 继续负责 D3D12 资源、BLAS/TLAS、描述符、Shader Table 和命令提交。
路径追踪的材质采样与推进已经替换，不需要构建 RTXPT 的 C++/Donut 可执行文件。

| 部分 | 实现与作用 |
| --- | --- |
| `PathSampling.hlsli` | RTXPT Owen-scrambled Sobol；按像素、样本、反弹和效果分离序列，避免阴影遍历改变 BSDF/roulette 的随机序列 |
| `PathBsdf.hlsli` | RTXPT GGX VNDF、height-correlated Smith masking；匹配的 lobe 概率；零粗糙度使用 delta 反射/透射，NEE 不把离散质量误当作方向 PDF |
| `PathState.hlsli` | 按实例和优先级管理介质，跳过重叠中的虚假界面；Beer-Lambert 吸收、累计 ray cone、折射 eta 补偿的 roulette |
| `SceneLightDistribution.cs` | 按世界面积 × 发光亮度建立 O(1) alias table 和旧路径 CDF；NEE 和 BSDF 命中发光表面读取同一面积 PDF，纹理平均亮度参与选光 |
| `PathLightProposal.hlsli` | 用历史可见贡献反馈选择局部灯光建议；75% 局部建议与 25% 全局功率分布混合，保留完整采样支撑；NEE 与命中发光表面共用条件 PDF |
| `SceneEnvironment` / `EnvironmentDistribution` / `PathEnvironment.hlsli` | CPU Radiance RGBE 解码、线性 HDR、旋转、双线性读取；按精确 texel 立体角构建 alias table，与天空命中 MIS 匹配 |
| `PathFirefly.hlsli` | RTXPT 的 PDF/lobe 概率相关 firefly 阈值；实时默认 64，参考模式严格关闭 |
| `PathStablePlanes.hlsli` / `StablePlaneDenoiser` | 三个路径空间分支：主表面剩余贡献、首个平滑反射、透射；折叠的虚拟几何引导、独立 NRD 历史和合成 |
| `SceneTrace.hlsl` | 确定性引导、原始辐射、NRD diffuse/specular 信号和玻璃输出；在路径深度上限取消不存在的后续 BSDF 样本的 MIS 权重 |
| `DxrSceneBackend.ReferenceMode` | 独立子像素样本，关闭 ReSTIR 历史复用和光子估计，供单帧离线校验 |

材质和顶点布局保持 432/80 字节；实例布局增加面积 PDF，变为 160 字节。
NRD/SR 原生桥接 ABI 不变；PBR 信号新增六张纹理，供反射/透射层的辐射、虚拟位置和法线使用。
SM 6.5 和 SM 6.9/SER 均保留。
旧 Stanford/软件 BVH 路径保留作比较。

SM 6.9 默认使用 inline ray query：候选三角形的 alpha/透射判断与旧 any-hit 共用
同一函数，单面剔除交给硬件，阴影分别用不透明和透射 instance mask。材质提示
SER 仍可显式开启；目前这个包含多种引导和路径状态的 shader 在 RTX 5070 Ti 上
关闭重排更快，因此默认关闭重排。Shader Model 查询必须先填写请求版本，再检查
返回值；旧的零初始化查询会误关闭高级路径。主程序和离线入口现在都启用 Agility。

介质状态用 24-bit instance ID＋4-bit 优先级打包，材质 IOR/吸收从场景表读取。
接近 delta 的 GGX 使用避免相消的 NDF 形式；MIS 先归一化 PDF 再平方，防止溢出。
NRD 三层在同一命令列表中执行；其管线初始化移到追踪之前，避免首帧阶段之间编译。
资产新增 BC7 GPU mip 链，保留 PNG 回退；详见资产 `SOURCES.md` 和各场景 `bc7-manifest.json`。

金属/透射混合遵循 [glTF transmission 材质分层](https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_materials_transmission)：
`transmission` 在表面求值时已包含非金属比例，因此漫反射使用
`baseColor × (1 − metallic − transmission)`，避免重复衰减。介质栈给出的相对 IOR
同时用于 NEE 与路径采样的 Fresnel/PDF；全反射的额外采样概率计入反射 MIS。

当前适配与 RTXPT 的 SDK 集成仍有边界：反馈选光采用本项目的重投影局部建议混合，
没有搬入上游 NEE-AT 的全局/局部 proxy baker；没有集成 RTXDI GI/PT、OMM、RTXTF、
完整场景图/动画系统。稳定分层是固定三分支适配，不是上游完整的递归分支分配器。
它沿首个平滑反射/透射分支追踪理想引导，随后沿主导理想方向继续，最多 12 个界面；
粗糙度大于 0.05 的表面仍由主层处理。曲面及折射的虚拟运动是局部旋转近似。
这些界限不能作为 RTXPT 全功能或逐像素等价的宣称。

实时模式默认使用新的反馈建议选光。旧有偏 ReSTIR DI 保留为显式后端开关，关闭
`AdaptiveLightingEnabled` 后才走旧路径；原有光子焦散仍是可选的有偏估计。
ReferenceMode 有有限最大深度和直线透射阴影近似；它关闭历史选光、光子估计和 firefly
过滤。环境光不再额外乘 AO 贴图：路径追踪已计算几何遮挡，重复乘 AO 会破坏 MIS 两侧的一致性。

## 材质、HDR 与分层

glTF 材质的 `extras.nestedPriority` 可取 0–15；遵循 RTXPT 约定，数值越大优先级越高，
0 表示最高优先级。未指定时保持普通嵌套介质行为。列表最多容纳七个实体，优先级相同
时后进入者生效；被覆盖的低优先级界面更新列表但不折射、不增加反弹次数，拒绝次数有界。

glTF 根对象可添加以下 renderer metadata（旋转单位为弧度，文件相对 glTF 定位）：

```json
{ "extras": { "environmentMap": "studio.hdr", "environmentRotation": 0.5 } }
```

也可通过 `SceneAsset.EnvironmentMap` 直接传入线性 HDR 数据。RGBE 支持现代扫描线 RLE
与平铺像素；旧式 chained-run、EXR 和 DDS cubemap 不由此解码器处理。alias 分布含 5%
均匀立体角支撑，避免黑 texel 与亮 texel 的双线性边缘失去采样概率。默认 Kitchen
仍使用其原作程序化天空；另打包 RTXPT 的 Wells HDR，来源见资产说明。

NRD 模式下，主层与反射/透射层使用三个独立 NRD context，逐层降噪后相加；不再用
旧的屏幕空间玻璃颜色缓存替代这些层。没有分层条件时，贡献留在主层，能量不丢失。
无引导命中的天空层保留原始辐射。DLSS RR 继续接收完整原始信号，未套用这套 NRD 分层。
新增六张 RGBA32F 信号纹理需要每输入像素 96 字节，另有两层 NRD 的内部历史成本。

## 默认资产

启动默认是 **RTXPT Kitchen**，1,474,751 个三角形、317 个实例、102 个纹理视图。
同时提供 **RTXPT Bistro 街角**（4,209,006 个三角形）和 **Convergence Test**；Rainy Corner 与 Stanford 三个模型仍可选择。
Kitchen 使用原作相机和固定曝光 +1.3 EV，允许程序化环境光。
新的实时设置默认 **1 SPP、10 次反弹、DLSS 光线重建、50% 输入比例**；已保存的
用户设置仍优先。光线重建不可用时沿原有能力检查回退，NRD＋DLSS 超分仍可选择。
Bistro 的原场景启用自动曝光；本项目为其设置固定初始曝光 +6 EV，可在曝光控件中调整。
切换 RTXPT 场景时应用该场景的初始曝光；启动恢复已保存设置时仍尊重用户曝光值。
旧版本默认 Rainy Corner 的已保存选择自动迁移到 Kitchen；新的场景目录版本会被
保存，因此之后手动选择 Rainy Corner 不会再次被迁移。

资产位于 `DXRDemo/Assets/Models/RTXPT`，随 build/publish 复制，不依赖开发者机器
上的 `G:` 路径。转换脚本、输入 SHA-256、原始署名和许可证随源码保留。
Kitchen 的 DDS 转为 PNG、纹理上限 1024；修复原始 glTF 的非法切线/法线与遗漏的
扩展声明，保留 SharpGLTF 严格校验。缺失原始图像的可选 specular 扩展明确采用
metallic-roughness 回退。Bistro 对应上游 README 的 Le Petit Coin 咖啡馆街区，包含街道、室内与车辆；采用 `bistro-programmer-art` 的 Outside 相机、254 份材质覆盖以及同款 Shanghai Bund HDR。旧 specular/glossiness 材质离线烘焙为近似 metallic/roughness；植被漫透射以及上游额外测试道具、动画尚未包含。完整说明见资产目录的 `SOURCES.md`。

## 单帧离线校验

此入口不创建窗口、不调用交互渲染循环、不预热，默认不执行 NRD/SR，也不运行多帧
benchmark。默认 256×144、4 SPP、8 次反弹；离线导入的纹理上限为 256，以降低
游戏同时运行时的显存占用。它只调用一次 `Trace`，并断言帧数为 1、光子 pass 为 0。
构建加速结构和着色器编译仍是首次执行必需的初始化工作。

在仓库根目录：

```powershell
dotnet build DXRDemo/DXRDemo.csproj -c Release --no-restore -p:Platform=x64 -p:WindowsSdkPackageVersion=10.0.22621.57 -p:WindowsPackageType=None -p:SkipReconstructionNativeBuild=true -p:OutputPath=bin/rtxpt/ -p:IntermediateOutputPath=obj/rtxpt/ -m:1
dotnet build diagnostics/integration/IntegrationProbe.csproj -c Release --no-restore -p:SkipReconstructionNativeBuild=true -p:OutputPath=bin/rtxpt/ -p:IntermediateOutputPath=obj/rtxpt/ -m:1
$env:DXR_DXC_PATH = (Resolve-Path 'DXRDemo/bin/rtxpt/dxc.exe').Path
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --cpu-only
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --cpu-only --convergence
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline
# 数值契约只做一次 9×4 dispatch；使用真实的生产 HLSL helper。
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --contracts
# 一帧完整的 HDR + 实时选光 + 三层 NRD 合成检查，无时间预热。
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --convergence --production --nrd --width 160 --height 90 --environment DXRDemo/Assets/Models/RTXPT/EnvironmentMaps/20060807_wells6_hd.hdr
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --bistro --cpu-only
./diagnostics/integration/bin/rtxpt/IntegrationProbe.exe --offline --bistro --width 160 --height 90
```

以上构建命令复用已经还原的 NuGet 包和已构建的原生桥接。首次安装需要先正常
还原和构建；不要在游戏运行时改用已有的 `--scene`、`--lighting` 或 `--sr` 多帧套件。

输出在运行目录的 `output/rtxpt-offline/`：PNG 预览、线性 RGBA32F 和 JSON 指标。
检查包括资产导入、相机恢复、按功率选光的比例、CDF/PDF 归一化与一致性、有效
辐射、主表面命中、NRD 信号重组、D3D12 Error/Corruption 消息。
CPU 重组逐分量读取回传值；ComputeSharp 的向量算术是 GPU intrinsic，不能在
CPU 验证代码里使用。此陷阱已有纯 CPU 已知值回归检查。

此次验证单帧路径、CPU/GPU 数值契约和单帧 NRD 合成；没有验证运动、时间稳定性、
多帧 NRD/DLSS 视觉质量或实时帧率。4 SPP 原始预览有明显采样噪声，不能作为收敛图。

### 本次验证记录（2026-10-23）

- Kitchen 和 Convergence Test 的 CPU 导入、相机恢复及选光 PDF 检查通过。
- DXC 的 SM 6.5 与 SM 6.9/SER 两种库编译通过。
- Release 主程序与离线探针构建通过；主程序仅有既有的 WinUIEx `Icon` 过时警告。
  设置持久化的 11 项 CPU 检查通过。
- RTX 5070 Ti，Kitchen，256×144、4 SPP、8 次反弹、纹理上限 256。
  单次 `Trace`，0 次光子 pass，0 个 D3D12 错误，0 个无效信号；
  NRD 通道重组最大相对误差 `1.1863429e-7`。
- 单帧命令提交及等待约 15.9 ms，初始化约 24 秒。这是游戏同时运行时的
  一次离线检查，不是 GPU 性能基准或实时帧率测量。

### 核心功能补齐后的验证（同日）

- Release 主程序、离线探针、SM 6.5、SM 6.9/SER、分层准备/合成 compute 编译通过。
- CPU：RGBE 解码、截断文件拒绝、黑环境和高动态范围环境的 alias/PDF 归一化、
  glTF HDR/介质优先级 metadata 导入、两套打包场景与选光 PDF 检查通过。
- 单次 7×4 dispatch：28 个实际 HLSL 契约通过，覆盖混合金属/透射、重叠介质与
  重复进入、环境方向 PDF、局部/全局选光混合、全反射与 delta 反射。
- Convergence Test，160×90、4 SPP、8 反弹，Wells HDR、实时选光开关、三层 NRD，
  一次 `Trace`、零光子 pass、零预热。687 个反射像素、377 个透射像素、704 个虚拟引导像素；
  最大信号重组相对误差 `1.66815695e-7`，无无效信号和 D3D12 Error/Corruption。
  输出 `convergence-production-hdr-planes.png`、对应的 `-nrd.png` 与 JSON。
- 默认 Kitchen 的 256×144、4 SPP 单帧参考回归通过：102 个纹理视图，最大信号
  重组相对误差 `1.1920929e-7`，0 个无效信号，0 个 D3D12 错误。
- 单帧只能验证冷启动、数学契约和通道合成。反馈收敛速度、相机运动中的虚拟表面
  稳定性、长时间 NRD 历史、实时性能和显存峰值仍未实测。

### Bistro 街角接入后的验证（同日）

- 原始 4,209,006 个实例化三角形保留；2909 个渲染实例、551 个网格、254 个源材质，
  529 张 PNG（530 个颜色空间纹理视图）。打包约 402 MiB，默认相机为上游 Outside。
- CPU 导入、相机恢复、76,248 个发光三角形的 CDF/PDF 检查通过；材质转换的
  介电体、导体、零镜面输入与 sRGB 线性往返检查通过。
- 160×90、4 SPP 参考单帧，以及 256×144、16 SPP 实时路径 + 三层 NRD 单帧均通过。
  两次命令均只调用一次 Trace，零光子 pass、零历史预热，离线纹理上限 256。
- 后者使用 Shanghai Bund HDR、固定曝光 +6 EV；1230 个反射像素、1125 个透射像素，
  1494 个虚拟引导像素，信号重组最大相对误差 `1.31787573e-7`，无无效信号或 D3D12 错误。
  预览为 `output/rtxpt-offline/bistro-production-hdr-planes-nrd.png`，已目视核对街角、咖啡馆与车辆。
  这是低采样冷启动验收；仍有噪点，不能当作多帧收敛画质或上游逐像素等价结果。

### 30 FPS 预算检查（2026-10-23/24）

用户允许 1 SPP；固定 RTX 5070 Ti、3840×2160 输出、1920×1080 输入、完整打包
1024 纹理和 10 次反弹。每个命令严格只追踪一帧，不运行窗口、连续基准或历史预热。

```powershell
IntegrationProbe.exe --offline --bistro --production --rr-4k --spp 1 --bounces 10
IntegrationProbe.exe --offline --bistro --production --dlss-4k --spp 1 --bounces 10
IntegrationProbe.exe --offline --contracts
```

`--rr-4k` 使用 DLSS 光线重建，`--dlss-4k` 使用三层 NRD＋DLSS 超分。
`--texture-size 256` 仅用于与早期低纹理预算对照；4K 默认使用 1024。
`--uncompressed` 禁用 BC7，`--reorder` 启用材质 SER，`--legacy-trace` 使用 TraceRay。
JSON 记录实际 shader model、inline/SER 状态、纹理驻留字节和 GPU timestamp。
初始化在追踪前完成，两段 GPU 工作之间没有像素读回或 CPU 图像校验；这些检查
随后进行。输出既包含输入分辨率的原始信号，也包含真正的 `*-4k.png`。

早期 2 SPP/10 反弹/256 纹理对照中，单独追踪从 378.4 ms 改善到 68.8 ms；
后者仍启用重排。最终目标配置改为 1 SPP/1024 纹理，并关闭重排，不能把两组
数值当作相同画质设置的直接 FPS 倍率。NRD＋DLSS 超分单帧 GPU 总耗时仍处于
约 33.6–40.2 ms；6 次反弹检查也没有证明稳定收益，因此保持 10 次默认值。
DLSS 光线重建的两次独立单帧检查为 30.66 ms 与 31.97 ms；最后一次包含完整的
4K 线性输出有限值检查，追踪 25.47 ms＋重建 6.50 ms，进入 33.33 ms GPU 预算。
结果见 `output/rtxpt-offline/bistro-production-hdr-rr.json` 与对应的 `-4k.png`。
该预算不包含呈现和 CPU 调度，**不是已实测稳定 30 FPS 的宣称**。
单帧无法验证运动稳定性、时间累积画质和长时间帧率。

36 项单次 GPU 契约覆盖原有材质/介质/PDF，加上单面、双面、镜像实例和低粗糙度
NDF／大 PDF 的 MIS。BC7 reader 拒绝错误尺寸、颜色空间和截断数据。

最终回归使用相同的 4K 输出／50% 输入、1 SPP、10 次反弹：

| 场景 | 重建模式 | 追踪 GPU | 重建 GPU | GPU 合计 |
| --- | --- | ---: | ---: | ---: |
| Bistro | DLSS 光线重建 | 25.47 ms | 6.50 ms | 31.97 ms |
| Kitchen | DLSS 光线重建 | 10.30 ms | 6.50 ms | 16.80 ms |
| Convergence Test | 三层 NRD＋DLSS 超分 | 15.59 ms | 12.67 ms | 28.26 ms |

三者均只有一帧、零光子 pass、零无效信号、零 D3D12 错误，通道重组最大相对
误差低于 `2.4e-7`。光线重建额外检查完整 4K 线性输出有限值；Kitchen 与 Bistro
的 4K 输出已目视检查。36 项 GPU 契约分别通过 inline、legacy TraceRay、材质 SER
三个单次 dispatch。SM 6.5 编译和 Release 主程序构建通过；后者仅有既有 Icon 过时警告。
