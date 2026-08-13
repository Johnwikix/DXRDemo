# DXR 实现工作档案

> **当前状态：✅ 构建通过，可运行。**
> Debug 与 Release（x64）均能成功产出 `DXRDemo.exe`；启动后可见 5 个球体
> （绿色 Lambertian、银色金属、玻璃 + 内气泡、巨型地面），鼠标左键轨道旋转，滚轮缩放。
> 本文档保留历史修复记录与剩余工作清单。

## 历史：Vortice 3.8.3 API 对齐（已全部完成）

项目骨架初建时因 Vortice 3.8.3 自动生成 API 名称与假设不一致而构建失败（30+ 错误），
以下问题均已修复并通过构建：

| 文件 | 问题 | 修复 |
|---|---|---|
| DxrDevice.cs | Debug Layer / Feature 命名空间 | `Vortice.Direct3D12.Debug.D3D12`；`Feature.Raytracing` 用全限定名消除 DXGI 歧义 |
| DxrDevice.cs | `Fence.SetEventOnCompletion().Wait()` 不可用 | 改为 `while (Fence.CompletedValue < value) Thread.Sleep(0)` 轮询 |
| DxrRootSignature.cs | 参数类型 / 命名参数 | `...View` 完整名称；`RootSignatureDescription1(flags, parameters, samplers:)` |
| DxrShaderCompiler.cs | DXC API | `IDxcResult` + `DxcCompiler.Compiler.Compile()`，`GetOutput(DxcOutKind.Object)` 取 `IDxcBlob` |
| DxrAccelerationStructure.cs | 栅栏 / 类型 | `ResourceBarrierUnorderedAccessView(resource)` 静态方法；`using System.Numerics;`；`Matrix3x4` → 手写 3×4 矩阵写入 |
| DxrPipelineState.cs | 描述结构 | `DxilLibraryDescription` 取 `byte[]` + `ExportDescription[]`；`RaytracingShaderConfig`/`RaytracingPipelineConfig` 名称确认 |
| DxrSwapChain.cs | 泛型/签名 | `CreateSwapChainForComposition` 非泛型重载；`GetBuffer<ID3D12Resource>(CurrentBackBufferIndex)` |
| DxrResources.cs | 命名参数 | `SampleDescription`/`ClearValue` 大小写及命名参数对齐 |
| DxrRenderer.cs | DispatchRays | `DispatchRaysDescription` 4 参数（含 `callableShaderTable: default`） |

## 状态表（对照 spec）

| Spec 章节 | 状态 |
|---|---|
| §4.1-4.4 HLSL shader（payload 字段闭环 + bounce 循环） | ✅ 完成 |
| §4.5 AS 拓扑（单 BLAS 多 AABB + 单 TLAS instance） | ✅ 完成 |
| §5.2 DxrRenderer（命令录制 + DispatchRays + CopyTextureRegion + Present） | ✅ 完成 |
| §5.3 AS 构建（5 AABB + 1 instance + GeometryIndex 选择） | ✅ 完成 |
| §5.4 WinUI3 SwapChainPanel 集成（ISwapChainPanelNative COM） | ✅ 完成 |
| §6 DXC 运行时编译 | ✅ 完成 |
| §7 RT PSO（HitGroup + ShaderConfig + GlobalRS + PipelineConfig） | ✅ 完成 |
| §8 Shader Table（3 条记录） | ✅ 完成 |
| §9 Root Signature（4 个 root 槽位） | ✅ 完成 |
| 渐进式累积（prev-frame 混合，MaxWeight=100） | ✅ 完成 |
| 双缓冲流水线 + 围栏同步 | ✅ 完成 |

## 剩余工作 / 优化方向

- [ ] **性能**: Debug Layer 仅 Debug 开启（已做）；仍可考虑 FrameCount 2→3、减少 Descriptor 重建
- [ ] **Resize**: 交换链当前为销毁重建，窗口缩放瞬间会有顿挫；可改为 `ResizeBuffers`
- [ ] **重投影降噪**: 当前累积在相机移动时需重置帧号（已实现），可研究基于重投影的增量渲染
- [ ] **场景扩展**: 加入三角形网格几何（非 AABB）、动画 TLAS 实例、多实例实例化
- [ ] **UI 增强**: 材质/光源参数面板（当前为硬编码常量）、截图/录制导出、渲染分辨率缩放
- [ ] **发布**: 验证 `dotnet publish` 单目录 SelfContained 部署

## 构建入口

```bash
# 单独构建本工程（x64，跳过 wapproj）
dotnet build D:\code\winui\DXRDemo\DXRDemo\DXRDemo.csproj -p:Platform=x64 -p:Configuration=Debug
```