# DXRDemo — WinUI 3 + DirectX 12 Raytracing Demo

一个基于 **WinUI 3 / .NET 10** 的实时 **DirectX 12 光线追踪（DXR）** 演示程序。

从零实现完整 DXR 管线（使用 [Vortice](https://github.com/amerkoleci/Vortice.Windows) 而非 SharpDX），支持：

- **glTF 2.0 / GLB** 静态场景加载与 PBR 渲染（节点层级、Metallic-Roughness、`KHR_lights_punctual` 等）
- **NVIDIA NRD 4.17.3 RELAX**（漫反射 + 镜面反射）降噪
- **超分辨率**：AMD FSR 3.1、Intel XeSS、NVIDIA DLSS SR、DLSS Ray Reconstruction（DLSSD）
- **HDR10** 输出与 SDR 回退
- **轨道 / 自由** 两种相机、轨道 + 拖动 glTF/GLB 打开
- **实体玻璃**（Snell 折射 / 全反射 / Beer-Lambert 吸收）

启动默认加载随程序打包的 **雨后便利店 · Rainy Corner**（Blender MCP 制作，2,019,650 个三角形，24 个材质，8 盏灯）。详细操作与支持项见 [场景渲染器说明](docs/SCENE_RENDERER.md)，降噪见 [NRD](docs/NRD.md)，超分/光线重建见 [超分说明](docs/SUPER_RESOLUTION.md)，玻璃与便利店验证见 [GLASS_RR_VALIDATION](docs/GLASS_RR_VALIDATION.md)。

A real-time DirectX 12 raytracing demo built on WinUI 3. The full DXR pipeline is implemented from scratch with [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — no SharpDX. Loads glTF 2.0 / GLB scenes, runs hardware-accelerated path tracing with official NVIDIA NRD denoising, vendor super resolution, HDR10 output and a swappable orbit / fly camera.

---

## 默认场景 Default Scene

启动后自动加载随构建打包的 **雨后便利店 · Rainy Corner / Koyomi Mart**（原创 Blender 几何，无第三方模型与材质），启用「仅使用模型光照」、曝光 −2 EV。

- 233 个共享网格、3,199 个网格实例、**2,019,650 个三角形**（含实例）、24 个导出材质（渲染器加默认后 25）、8 盏内嵌灯。
- 商店室内（货架、饮料柜、便当、收银台、咖啡机、关东煮柜台、杂志架、冰柜等）、街角设施（售货机、自行车、伞架、垃圾桶、路灯、电线杆、护栏、斑马线、积水等）。
- 玻璃使用 `KHR_materials_transmission` / `IOR` / `volume`，alpha=1，粗糙度 0.06；金属和湿地反射使用 Metallic-Roughness。
- 设置中的场景列表保留 Stanford **Bunny / Armadillo / Dragon** 与便利店；导入其他 GLB 后仍可一键切回。
- 源 `.blend`、参考图、实际 DXR 输出和复现脚本见 [`Samples/RainyCorner`](Samples/RainyCorner/README.md)。

---

## 特性 Features

### 渲染

- **硬件 DXR 管线**：`RayGeneration / ClosestHit / Miss / Intersection` 四类着色器，启动时由 **DXC** 运行时编译 HLSL（优先 `lib_6_9`，旧运行时回退 `lib_6_5`）为 DXIL，无需预烘焙
- **glTF 2.0 / GLB**：JSON、外部资源、GLB、Data URI；多场景、节点层级、矩阵/TRS、重复网格实例、负缩放 / 非均匀缩放、包围盒自动取景
- **网格**：`TRIANGLES / TRIANGLE_STRIP / TRIANGLE_FAN`；索引、法线、切线、顶点颜色、`TEXCOORD_0/1`
- **材质（Metallic-Roughness）**：基础色 / 金属-粗糙度 / 法线 / 遮蔽 / 自发光贴图及因子；双面
- **PNG / JPEG** 贴图；按用途区分 sRGB / 线性；生成 mip 链；显式 ray-cone LOD；读取 wrap / filter
- **透明度 / 玻璃**：`OPAQUE / MASK / BLEND`；玻璃使用 `KHR_materials_transmission` / `IOR` / `volume` 独立路径，含 Snell 折射、Fresnel 反射、全反射、粗糙微表面、Beer-Lambert 吸收；最多七层正确嵌套的实体介质；封闭几何提供实际穿行厚度
- **灯光**：`KHR_lights_punctual`（方向、点、聚光），颜色、强度、范围、锥角和节点变换；自发光三角形采样
- **常用扩展**：`KHR_texture_transform`、`KHR_materials_unlit`、`KHR_materials_emissive_strength`、`KHR_mesh_quantization`
- **光照**：GGX 镜面反射、漫反射、直接光、环境照明、自发光三角形采样、MIS、路径继续与 Russian roulette
- **轨迹后端**：`DxrMeshBackend`（硬件 DXR，`DxrSceneBackend` 提供导入场景；旧 5-球体程序化 `DxrRenderer` 与 ComputeSharp `SoftwareMeshBackend` 保留为对照路径）
- **SM 6.9 / DXR 1.2**：设备启动时查询 `D3D12_FEATURE_SHADER_MODEL`；支持时使用 `lib_6_9` 和 Shader Execution Reordering（`HitObject::TraceRay` + `MaybeReorderThread`），不支持时自动使用 `lib_6_5`，保持旧版 DXR 驱动可运行。阴影查询只保留可见性所需的命中状态，跳过不必要的 closest-hit/miss payload 回传。
- **神经辐射缓存（NRC）**：可选的 GPU 在线 32→32→3 MLP，用二次命中的局部辐射样本做反向传播与 Adam 更新，覆盖度达到阈值后提前终止漫反射二次路径。场景或开关变化会重置网络，光照变化继续在线适应；玻璃、镜面和主光照仍走完整 DXR 路径。

### 降噪 / 超分

- **降噪**：关闭、时间累积、**NVIDIA NRD · RELAX** 三档；PBR 使用官方 RELAX_DIFFUSE_SPECULAR_SH、材质去调制、SG resolve 和 SR 前的 re-jittering。玻璃在不透明表面降噪后合成，使用当前帧重投影和独立运动矢量；静止镜头下玻璃持续累积，不设帧数上限，镜头变化立即重置，SR 采样抖动不触发重置。旧 RELAX 风格实现已移除，旧设置自动迁移到 NRD。
- **超分辨率 / 光线重建**：原生分辨率、AMD FSR 3.1、Intel XeSS、NVIDIA DLSS SR、**NVIDIA DLSS Ray Reconstruction（DLSSD，已在 RTX 上完成运行验证）**；统一 **1–100% 渲染比例**滑块（默认 67%）；不支持的算法在 UI 中禁用；初始化失败显示具体回退原因
- NR 与 NRD 正交：可单独开 NRD、可与 FSR/XeSS/DLSS SR 组合；启用 DLSSD 时旁路 NRD 和常规降噪

### UI 与相机

- **WinUI 3**：`WindowEx`（[WinUIEx](https://github.com/dotMorten/WinUIEx)） + `SwapChainPanel` 通过 `ISwapChainPanelNative` COM 接口绑定 DXGI 合成交换链；FPS / 状态栏 + Mica 背景
- **相机模式**：
  - 轨道：左键拖动旋转、中键平移、滚轮缩放
  - 自由：先点击视口，W/S 前后、A/D 左右、Q/E 升降，右键观察，Shift 加速，滚轮调速，R 重置
- **拖拽打开**：把 `.gltf` / `.glb` 拖到视口，或点工具栏「打开模型」 / `Ctrl+O`
- **HDR10 输出**：实时检测显示能力并切换 SDR / HDR；HDR 失败回退 SDR
- **GPU 诊断信息** HUD：RENDER / SUBMIT / FRAME 计时
- **设置持久化**：`%LOCALAPPDATA%\DXRDemo\settings.json`（unpackaged，使用 `System.Text.Json` source generator）
  - 恢复时校验下拉索引、数值范围与有限性；无效字段保留当前默认值，导入场景的临时索引不跨会话保存。
  - 保存到同目录临时文件并原子替换，上一份完整设置保留为 `settings.json.bak`；主文件损坏时读取备份，两份都不可用时使用默认设置。

---

## 技术栈 Tech Stack

| 组件 Component | 版本 / 说明 |
|---|---|
| .NET | `net10.0-windows10.0.22621.0` |
| WinUI 3 / Windows App SDK | 2.3.1（SelfContained） |
| Vortice.Direct3D12 / DXGI / Dxc | 3.8.3 |
| Vortice.Mathematics | 2.1.1 |
| Vortice.D3DCompiler | 3.6.2 |
| ComputeSharp | 3.2.0 |
| SharpGLTF.Core | 1.0.6（glTF 解析） |
| Microsoft.Direct3D.DXC | 1.9.2609.5（DXC NuGet + 本机复制） |
| Microsoft.Direct3D.D3D12 | 1.619.6（Agility runtime，SM 6.9 / DXR 1.2） |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 |
| WinUIEx | 2.9.2 |
| HLSL Shader Model | `lib_6_9`（DXR 路径，SM 6.5 回退） |
| 原生桥接 | x64，C++ v145 工具集，PowerShell 7 |
| 厂商 SDK | NVIDIA NRD 4.17.3、AMD FSR 3.1、Intel XeSS、NVIDIA DLSS / DLSSD（固定版本与 SHA-256 校验，见 `External/NRD/` 与 `External/Upscalers/`） |

---

## 环境要求 Requirements

- Windows 10 1809+（最低平台版本 10.0.17763.0）
- 支持 DXR 的 GPU（NVIDIA RTX / GTX 16 系及以上、AMD RX 6000+ 等）。启动时检查 `D3D12_FEATURE_DATA_D3D12_OPTIONS5.RaytracingTier` 和 `D3D12_FEATURE_SHADER_MODEL`；SM 6.9 运行时不可用时自动回退 `lib_6_5`
- HDR10（可选）：需要 Windows HDR 开启的显示器
- 构建：Visual Studio 2022 + .NET 10 SDK；x64 还需 PowerShell 7 与 C++ v145 工具集

---

## 构建与运行 Build & Run

```bash
# Debug
dotnet build DXRDemo/DXRDemo.csproj -p:Platform=x64 -p:Configuration=Debug

# Release
dotnet build DXRDemo/DXRDemo.csproj -p:Platform=x64 -p:Configuration=Release
```

生成产物：`bin\x64\{Debug|Release}\net10.0-windows10.0.22621.0\DXRDemo.exe`，同级还有 `dxc.exe / dxcompiler.dll / dxil.dll` 和 `Assets\Models\`（含预烘焙 Stanford 模型与 `RainyCorner.glb`）。

仓库的 `global.json` 选择已安装的 .NET 10 稳定 SDK。Debug 和 Release 必须分别构建；确认构建成功后再运行对应目录的程序，避免运行残留的旧产物。

> **Publish 提示**：csproj 已包含 `CopyAppPriAndXbfToPublish` 目标，把 `*.pri / *.xbf` 复制到 publish 目录。部署时保留整个输出目录；`0xc000027b` 也可能由托管异常触发，例如恢复已经失效的场景索引。

---

## 操作控制 Controls

| 操作 | 效果 |
|---|---|
| 视口左键拖动 | 轨道旋转相机 |
| 视口中键拖动 | 轨道平移 |
| 滚轮 | 轨道缩放 / 自由相机的移动速度 |
| 视口焦点 + WASD | 自由相机前后左右 |
| Q / E | 自由相机下降 / 上升 |
| Shift（WASD） | 自由相机加速 |
| R | 重置当前场景的初始取景 |
| Esc | 释放所有键鼠输入 |
| 拖入 `.gltf` / `.glb` | 打开模型 |
| Ctrl+O / 工具栏「打开模型」 | 文件选择器 |
| F1 | 打开设置窗口 |
| F11 | 切换全屏 |
| 窗口缩放 | 自动重建交换链 / 渲染纹理 |

---

## 设置窗口 Settings（`F1`）

- **渲染程序**（`ShaderSelector`）：当前唯一条目 `RT Demo`（DXR Path Trace，Hailuo-3 / DXR）
- **场景模型**：内置 Bunny / Armadillo / Dragon / 雨后便利店 / 当前已导入的 GLB
- **降噪**：关闭 / 时间累积 / NRD · RELAX
- **超分辨率 / 光线重建**：关闭 / FSR 3.1 / XeSS / DLSS / DLSSD；不支持的算法自动禁用
- **SR 渲染比例**：1–100% 滑块（默认 67%，200 ms 防抖）
- **每像素采样 SPP**：1–16（默认 2）
- **最大反弹次数**：1–32（默认 10）
- **重置相机**、**自由相机速度**、**环境光强度**、**仅使用模型光照**（关外加天光/太阳，留模型自发光）
- **全局太阳光**：开关 + 方位角（0°=+Z，90°=+X）+ 高度角（0°=地平线，90°=正上）+ 强度；方向光硬阴影
- **曝光补偿 EV**：−16 ~ +16（默认 −2）
- **HDR10 输出**（自动检测显示能力）
- **GPU 诊断信息** HUD
- **场景分辨率设为 1920 × 1080**

修改即时生效；变更会写入 `%LOCALAPPDATA%\DXRDemo\settings.json`。

---

## 项目结构 Structure

```
DXRDemo/                                  解决方案根
├── DXRDemo/                              主工程（WinUI 3 应用）
│   ├── DXRDemo.csproj
│   ├── App.xaml(.cs)                     # 应用入口
│   ├── MainWindow.xaml(.cs)              # UI：SwapChainPanel + 工具栏 + 设置窗口调度 + 渲染驱动
│   ├── SettingsWindow.xaml(.cs)          # 设置面板（独立 WindowEx）
│   ├── AppSettings.cs                    # 持久化设置（System.Text.Json source generator）
│   ├── Assets/
│   │   ├── Models/                       # 预烘焙的 Stanford Bunny / Armadillo / Dragon (.mesh.gz)
│   │   └── Importers/GltfImporter.cs     # SharpGLTF → SceneAsset
│   ├── Camera/CameraController.cs        # 轨道 / 自由相机、值类型帧、零分配
│   ├── DXR/                              # 原始 5-球体 DXR 引擎（保留）
│   │   ├── DxrDevice.cs                  # 设备 / 队列 / 围栏 / 双缓冲分配器
│   │   ├── DxrRootSignature.cs           # 全局根签名（4 槽）
│   │   ├── DxrShaderCompiler.cs          # DXC 运行时编译 HLSL → DXIL
│   │   ├── DxrAccelerationStructure.cs   # 5-AABB BLAS + 单实例 TLAS
│   │   ├── DxrPipelineState.cs           # RT PSO（DxilLibrary + HitGroup + 配置）
│   │   ├── DxrShaderTable.cs             # 着色器表
│   │   ├── DxrRenderer.cs                # 命令录制 + DispatchRays + 累积 + 呈现
│   │   ├── DxrResources.cs               # 上传 / 默认缓冲、UAV 纹理、描述符堆
│   │   ├── DxrSwapChain.cs               # DXGI 合成交换链 + ISwapChainPanelNative
│   │   ├── DxrMeshBackend.cs             # 新版硬件 DXR 三角形追踪后端
│   │   ├── DxrSceneBackend.cs            # 导入场景 BLAS / TLAS / 材质 / 灯光缓冲
│   │   └── REMAINING_WORK.md             # 历史工作档案
│   ├── Hdr/                              # HDR10 输出与全屏通道
│   │   ├── HdrDisplayInfo.cs             # 显示器 HDR 能力检测
│   │   ├── HdrSwapChainRenderer.cs       # SDR/HDR 编码
│   │   ├── HdrShaderPanel.cs             # 持有 GPU 表面 / dispatch 的画板
│   │   └── HdrFullScreenPass.cs
│   ├── Scene/                            # 场景资源布局
│   │   ├── SceneAsset.cs                 # 顶点 / 材质 / 纹理 / 灯光 CPU 结构
│   │   ├── PbrSignals.cs                 # NRD 信号契约
│   │   └── SunLightSettings.cs
│   ├── Shaders/
│   │   ├── ShaderFactory / Catalog       # 选择 / 缓存 pass 实例
│   │   ├── DXR/                          # 原始 5-球体 HLSL
│   │   │   ├── RayTrace.hlsl             # RayGen / ClosestHit / Intersection / Miss
│   │   │   ├── SceneData.hlsli           # 共享结构
│   │   │   ├── Reconstruction.hlsl       # 时间累积、NRD SH resolve、SR 引导与编码
│   │   │   ├── SceneTrace.hlsl           # glTF/GLB PBR 路径追踪
│   │   │   └── Minimal.hlsl
│   │   └── RayTrace/                     # ComputeSharp 着色器
│   │       ├── RayTracePass.cs           # 主 pass（场景 + 后端选择）
│   │       ├── RayTraceShader.cs         # 5-球体 ComputeSharp 实现
│   │       ├── MeshPathTraceShader.cs    # 软件 BVH 三角形追踪
│   │       ├── MeshEncodeShader.cs       # mesh 编码
│   │       ├── MeshScene.cs              # 内置 Stanford 模型选择与加载
│   │       └── NaiveTemporalAccumulationShader.cs
│   ├── SuperResolution/                  # 降噪 + 厂商 SR 路径
│   │   ├── SuperResolutionRenderer.cs    # NRD + FSR / XeSS / DLSS / DLSSD 接线
│   │   ├── NativeNrd.cs / NativeReconstruction.cs   # C# → C++/CLI / 原生 DLL 绑定
│   │   ├── ReconstructionMode.cs         # Off / Fsr / XeSS / Dlss / DlssRayReconstruction
│   │   └── ReconstructionAssets.targets  # MSBuild：复制 NRD / FSR / XeSS / DLSS DLL 与校验
│   └── app.manifest
├── Native/Reconstruction/                # 原生桥接（NRD / Vendor SR）
│   ├── Reconstruction.cpp                # FSR / XeSS / DLSS / DLSSD 入口
│   ├── Nrd.cpp                           # NRD 初始化 / dispatch 包装
│   ├── Reconstruction.vcxproj
│   └── Build.ps1
├── External/
│   ├── NRD/                              # NVIDIA NRD 4.17.3（Include / Shaders / Runtime + 校验）
│   └── Upscalers/                        # FSR / XeSS / DLSS / DLSSD（checksums.json + 校验脚本）
├── Samples/RainyCorner/                  # 雨后便利店 Blender 场景与复现脚本
│   ├── RainyCorner.blend / .glb          # 2,019,650 三角形 / 8 盏灯
│   ├── RainyCorner-preview.png           # Blender Cycles 参考（非 DXR 截图）
│   ├── RainyCorner-dxr.png               # DXRDemo + NRD 实际离屏输出
│   ├── build_scene.py / refine_scene.py  # 可复现建模 / 细化脚本
│   └── README.md
├── docs/
│   ├── SCENE_RENDERER.md                 # glTF/GLB 渲染器完整说明
│   ├── NRD.md                            # NVIDIA NRD 集成与契约
│   ├── SUPER_RESOLUTION.md               # FSR / XeSS / DLSS / DLSSD 与 Intel Arc 140T 验证
│   └── GLASS_RR_VALIDATION.md            # 玻璃 / DLSSD / 便利店细化验证总结
├── diagnostics/
│   ├── integration/                      # IntegrationProbe：资源契约、SR、NRD、玻璃、场景回归
│   │   ├── Probe.cs / SceneProbe.cs / ReconstructionProbe.cs
│   │   ├── IntegrationProbe.csproj
│   │   ├── ui_regression.py / ui_settings.py
│   │   └── README.md
│   └── performance/                      # PerfProbe：CPU/GPU 计时、mesh throughput
├── DXRDemo.slnx                          # 解决方案文件
├── DXRDemo (Package)/                    # （可选）MSIX 打包项目
├── .gitattributes / .gitignore
└── README.md                             # 本文件
```

---

## 渲染流程 Pipeline（高层）

1. **初始化**
   - 创建 D3D12 设备（`DxrDevice`）→ 检查 DXR Tier
   - DXC 运行时编译 HLSL（`RayTrace.hlsl` / `SceneTrace.hlsl`）→ DXIL
   - 加载 glTF / GLB：`SharpGLTF` → `SceneAsset`（CPU）；后台解码 PNG/JPEG、生成 mip；GPU 上传 BLAS / TLAS / 材质 / 灯光缓冲 / 纹理
   - 创建 RT PSO、Shader Table、根签名、SR/NRD 描述符
   - 创建并绑定 DXGI 合成交换链（`ISwapChainPanelNative`）
2. **逐帧（渲染线程）**
   - 更新常量（分辨率、相机、帧号、SPP / bounces、SR jitter、太阳参数）
   - 轨迹：`DxrSceneBackend`（硬件 DXR，`DispatchRays`）或 `SoftwareMeshBackend`（ComputeSharp 软件 BVH）二选一
   - 深度 / 运动 / 法线粗糙度 / 反应遮罩准备 → 厂商 SR / 自定义 / NRD → 线性输出
   - SDR 编码（sRGB）或 HDR 编码（Rec.2020 / PQ）→ Present
3. **渐进累积**：时间模式对历史帧 reprojection，相机拖动 / 缩放 / 模式切换 / 长时间间隔清空历史

---

## 验证入口 Diagnostics

仓库根目录下：

```powershell
# 主应用 Release 构建（覆盖参数兼容本机 Windows SDK）
dotnet build DXRDemo/DXRDemo.csproj -c Release -p:Platform=x64 -p:WindowsSdkPackageVersion=10.0.22621.57

# 设置文件回归（损坏 JSON、备份恢复、序列化/IO 失败、原子替换）
dotnet build diagnostics/settings/SettingsProbe.csproj -c Release --configfile diagnostics/performance/NuGet.Config
dotnet run --project diagnostics/settings/SettingsProbe.csproj -c Release --no-build --no-restore

# 集成探针（含场景、玻璃、SR、NRD、模型回归）
dotnet build diagnostics/integration/IntegrationProbe.csproj -c Release --configfile diagnostics/performance/NuGet.Config
cd diagnostics/integration
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene C:/Models/example.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --glass ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene-preview ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --sr
```

`SceneProbe` 用 `SceneProbe` 自动生成可重现的 glTF / GLB、3×3 贴图、重复实例样例；检查相机帧率独立性、模式连续性、重置、导入取消 / 失败、材质参数、灯光、alpha、纯金属信号、lobe 能量重组、模型尺度与 mip、真实 NRD dispatch、可用 SR。太阳测试验证开关 / 角度 / 强度线性关系以及双面遮挡片的 225 个可见阴影像素。`--glass` 对 2,888 个可见玻璃像素比较 512 SPP 参考与 2 SPP 输出：`x/(1+x)` 压缩后 RGB RMSE 从 **0.351406 → 0.131150（约 −63%）**。`--sr` 跑 80 组 FSR/XeSS × 比例 × 降噪 × SDR/HDR 组合；包含静止相机引导稳定性的 96 帧回归（最大输入像素误差 **0.000477**）和运动矢量正确性。D3D12 Error / Corruption 消息使测试失败。日志与截图写入 ignored `diagnostics/integration/output/`。

详细分别见各 `docs/` 子文档与 [`diagnostics/integration/README.md`](diagnostics/integration/README.md)。

> **DLSSD 验证**：原生/C# 编译、资源接线与 RTX 设备上的运行验证均已完成。状态文本应显示 DLSSD 激活而非回退；任何 NGX 错误都会以 `[DLSSD …]` 前缀回退到 DLSS SR + 常规降噪，或在 DLSS 也不可用时回退到原生分辨率。

---

## 已知限制与未来工作 Limits & Future Work

- **算法与 GPU**：DXR 是最低门槛（Tier 1.0+）；不宣称任意模型在任意 GPU 上达到固定 FPS
- **glTF**：静态场景；蒙皮 / Morph Target 明确拒绝，节点动画显示静态姿态并提示；点 / 线图元、UV 集 ≥ 2 暂不支持
- **压缩**：Draco、Meshopt、KTX2 / BasisU、WebP 尚未接入；缺 PNG / JPEG 回退时明确报错
- **资源预算**：当前每 GPU 场景最多 1024 个纹理视图
- **DLSSD**：已在 RTX 设备上完成运行验证；矩阵解释、运动矢量、disocclusion 与玻璃材质由 [GLASS_RR_VALIDATION](docs/GLASS_RR_VALIDATION.md) 跟踪
- **更高级材质**：Clearcoat、sheen、各向异性尚未接入
- **环境照明**：当前为可调程序化天空，未提供 HDRI 文件导入
- **多光源**：尚未实现 ReSTIR 或光源树；直射阴影使用透射率与吸收近似，未实现折射焦散

---

## 参考 References

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — C# DirectX 绑定
- [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) — C# → DXIL
- [WinUIEx](https://github.com/dotMorten/WinUIEx) — WinUI 3 WindowEx
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) — glTF 2.0 解析
- [NVIDIA NRD](https://github.com/NVIDIA-RTX/NRD) — 4.17.3 RELAX
- [NVIDIA DLSS RR](https://github.com/NVIDIA-RTX/Streamline) — DLSS Ray Reconstruction 集成指南
- [NVIDIA DLSS](https://github.com/NVIDIA/DLSS) — DLSS SR 编程指南
- [Intel XeSS](https://github.com/intel/xess) — XeSS SR SDK
- [AMD FSR](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-upscaler/) — FidelityFX SDK FSR 3.1
- [glTF 2.0](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html) — 规范
- [KHR_lights_punctual](https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_lights_punctual/README.md) — 灯光扩展
- 真实资产验证：[Khronos DamagedHelmet](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/DamagedHelmet)（仅保存在本地测试输出目录）

---

## 许可 Licenses

第三方 SDK / 模型 / 资源的许可证：

- Stanford 模型：见 [`Assets/Models/SOURCES.md`](DXRDemo/Assets/Models/SOURCES.md)
- NVIDIA NRD：见 [`External/NRD/LICENSE.txt`](DXRDemo/External/NRD/LICENSE.txt)、`External/NRD/MathLib-LICENSE.txt`、`External/NRD/ShaderMake-LICENSE.txt`
- AMD FSR / Intel XeSS / NVIDIA DLSS / DLSSD：见 [`External/Upscalers/NOTICE.txt`](DXRDemo/External/Upscalers/NOTICE.txt)
- Vortice、ComputeSharp、WinUIEx、SharpGLTF、Microsoft.Direct3D.DXC 等：见各自仓库
