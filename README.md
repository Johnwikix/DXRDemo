# DXR Demo — WinUI 3 + DirectX 12 Raytracing Path Tracer

DXR Demo 是一个基于 WinUI 3 的实时 DirectX 12 光线追踪（DXR）演示程序。它使用 [Vortice](https://github.com/amerkoleci/Vortice.Windows)（C# 的 DirectX 绑定，非 SharpDX）从零实现了完整的 DXR 渲染管线，渲染一个 Monte Carlo 路径追踪场景（5 个球体：绿色 Lambertian、银色金属、带内气泡的玻璃球、巨型地面）。

A real-time DirectX 12 Raytracing (DXR) demo built on WinUI 3, implementing the full DXR pipeline from scratch with [Vortice](https://github.com/amerkoleci/Vortice.Windows) (C# DirectX bindings, not SharpDX). It renders a Monte Carlo path-traced scene with 5 spheres: a green Lambertian sphere, a silver metal sphere, a glass sphere with an inner bubble, and a giant ground plane.

## 特性 Features

- **硬件光线追踪 DXR**: 通过 DirectX Raytracing Tier 1.0+ 硬件加速（`RayGenerationShader` / `ClosestHit` / `Miss` / `Intersection` 四类着色器）
- **程序化球体**: 单 BLAS 内含 5 个 AABB + 一个 intersection shader，通过 `GeometryIndex()`（SM 6.5）选择材质
- **实时路径追踪**: 每像素 4 次采样 × 最多 10 次弹射，太阳直接光照 + 环境光（半球采样），阴影为解析式判定
- **渐进式累积**: 输出纹理与上一帧混合（`MaxWeight=100`），帧数递增时噪声随帧累积收敛
- **轨道相机**: 鼠标左键拖动旋转，滚轮缩放，两帧缓冲流水线（CPU/GPU 双缓冲）
- **DXC 运行时编译**: 启动时用 DXC 将 HLSL（`sm_6_5`）编译为 DXIL，无需预先烘焙 shader
- **WinUI 3 集成**: `SwapChainPanel` 通过 `ISwapChainPanelNative` COM 接口绑定 DXGI 合成交换链，FPS/状态栏 + Mica 背景

- Hardware-accelerated raytracing (DXR Tier 1.0+) with RayGen / ClosestHit / Miss / Intersection shaders
- Procedural spheres: one BLAS with 5 AABBs + a single intersection shader, material selected via `GeometryIndex()` (SM 6.5)
- Real-time path tracing: 4 samples/pixel × up to 10 bounces, analytic sun + hemisphere environment lighting, analytic shadows
- Progressive accumulation: output blended with the previous frame (`MaxWeight=100`) to converge noise over time
- Orbit camera: left-drag to rotate, wheel to zoom; 2-frame CPU/GPU pipelining
- HLSL (`sm_6_5`) compiled to DXIL at startup via DXC — no pre-baked shaders
- `SwapChainPanel` bound to a DXGI composition swap chain through the `ISwapChainPanelNative` COM interface; FPS/status bar + Mica backdrop

## 技术栈 Tech Stack

| 组件 Component | 版本 |
|---|---|
| .NET | net10.0-windows10.0.22621.0 |
| WinUI 3 / Windows App SDK | 2.3.1（SelfContained） |
| Vortice.Direct3D12 / DXGI / Dxc | 3.8.3 |
| Vortice.Mathematics | 2.1.1 |
| HLSL 着色器模型 | 6.5（`lib_6_5`） |

## 环境要求 Requirements

- Windows 10 1809+（最低平台版本 10.0.17763.0）
- 支持 DXR（DirectX Raytracing Tier 1.0+）的 GPU（NVIDIA RTX / GTX 16 系及以上、AMD RX 6000+ 等）。启动时会检查 `FeatureDataD3D12Options5.RaytracingTier`，不满足时在状态栏提示
- 构建：Visual Studio 2022（WinUI 工作负载）+ .NET 10 SDK

## 构建与运行 Build & Run

```bash
# 单独构建工程（无需 wapproj）
dotnet build DXRDemo.csproj -p:Platform=x64 -p:Configuration=Debug

# 或 Release
dotnet build DXRDemo.csproj -p:Platform=x64 -p:Configuration=Release
```

然后运行 `bin\x64\{Debug|Release}\net10.0-windows10.0.22621.0\DXRDemo.exe`。

## 操作控制 Controls

| 操作 | 效果 |
|---|---|
| 左键拖动 | 轨道旋转相机（重置累积） |
| 鼠标滚轮 | 缩放相机距离（1.0 ~ 50.0，重置累积） |
| 窗口缩放 | 自动重建交换链与渲染纹理（重置累积） |

## 项目结构 Structure

```
DXRDemo/
├── App.xaml(.cs)              # 应用入口
├── MainWindow.xaml(.cs)       # UI：SwapChainPanel + FPS/状态栏；相机交互；渲染驱动（CompositionTarget.Rendering）
├── DXR/                       # DXR 引擎（纯原生 D3D12，经 Vortice）
│   ├── DxrDevice.cs           # 设备/队列/围栏/双缓冲分配器；DXR 支持检测
│   ├── DxrRootSignature.cs    # 全局根签名（4 槽：TLAS SRV、UAV 表、CBV、球体 SRV）
│   ├── DxrShaderCompiler.cs   # DXC 运行时编译 HLSL → DXIL
│   ├── DxrAccelerationStructure.cs  # 5-AABB BLAS + 单实例 TLAS 构建；球体数据缓冲
│   ├── DxrPipelineState.cs    # RT 管线状态对象（DxilLibrary + HitGroup + 根签名 + Payload 配置）
│   ├── DxrShaderTable.cs      # 着色器表（RayGen / Miss / HitGroup，3 条记录）
│   ├── DxrRenderer.cs         # 主渲染器：逐帧录制 + DispatchRays + 渐进累积 + 呈现
│   ├── DxrResources.cs        # 资源辅助（上传/默认缓冲、UAV 纹理、描述符堆）
│   ├── DxrSwapChain.cs        # DXGI 合成交换链 + ISwapChainPanelNative COM 绑定
│   └── REMAINING_WORK.md      # 开发手记/剩余工作
└── Shaders/DXR/
    ├── RayTrace.hlsl          # 主着色器：RayGen/ClosestHit/Intersection/Miss（sm_6_5）
    ├── SceneData.hlsli        # 共享结构（SceneConstants、SphereData、Schlick）
    └── Minimal.hlsl
```

## 渲染流程 How It Works

1. **初始化**: 创建 D3D12 设备 → 编译 HLSL（DXC）→ 构建 BLAS/TLAS → 创建 RT 管线与着色器表 → 创建帧资源（双缓冲 UAV 纹理 + 描述符堆）→ 创建并绑定交换链
2. **逐帧**: 更新常量（分辨率/鼠标/时间/相机距离/帧号）→ 重建 UAV 描述符（输出与上一帧纹理每帧互换角色）→ `DispatchRays`（1 次 TraceRay/弹射，阴影为解析式）→ 输出拷贝到后缓冲 → Present
3. **渐进累积**: 本帧结果 = 前一帧结果与当前帧结果按 `1/weight` 权重混合（`weight` 随帧数增加，上限 100），拖拽/缩放/缩放窗口时帧号归零重新收敛

## 已知问题与参考 Notes & References

- Debug 构建启用 D3D12 Debug Layer，每帧有显著 CPU 开销，仅用于调试
- 交换链 `Resize` 采用销毁重建方式（`CreateSwapChainForComposition`），缩放窗口时会短暂重建
- 性能优化、剩余工作与曾经的 Vortice API 对齐记录见 [`DXR/REMAINING_WORK.md`](DXR/REMAINING_WORK.md)