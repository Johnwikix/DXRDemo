# DXR 实现剩余工作 — Vortice 3.8.3 API 对齐

项目骨架已就绪（13 个 .cs 文件 + 2 个 HLSL 文件）。当前**构建未通过**，主要是 Vortice 3.8.3 的自动生成 API 与本项目假设的 API 名称不一致。

## 主要不匹配的 API（按文件归类）

### DxrDevice.cs
- ✅ 已修：`Vortice.Direct3D12.Debug.D3D12` → `D3D12`（Debug Layer 类直接在 Vortice.Direct3D12 命名空间）
- ✅ 已修：`Feature.Raytracing` 改为 `Vortice.Direct3D12.Feature.Raytracing`（与 `Vortice.DXGI.Feature` 消歧义）
- 🔧 `AdapterFlags.Software` —— 应已 OK，但 `AdapterFlags` 在 `Vortice.DXGI` 命名空间
- 🔧 `Fence.SetEventOnCompletion(value, nint.Zero).Wait()` —— 该方法不返回 Wait handle；改为 `while (Fence.CompletedValue < value) Thread.Sleep(0)` 轮询

### DxrRootSignature.cs
- ✅ 已修：`RootParameterType.ShaderResource/UnorderedAccess/ConstantBuffer` → `...View`（完整名称）
- ✅ 已修：`new RootSignatureDescription1(flags, params, samplers:)` 参数名

### DxrShaderCompiler.cs
- ✅ 已修：`IDxcResult` + `DxcCompiler.Compiler.Compile()` API
- 🔧 `DxcResult.GetOutput(DxcOutKind.Object)` 返回 `IDxcBlob`

### DxrAccelerationStructure.cs
- 🔧 `Matrix3x4.Identity` 和 `UInt24` 在 `Vortice.Mathematics` 命名空间（已 using，但 Matrix3x4 类型可能不存在）
- 🔧 `ResourceUnorderedAccessViewBarrier.Resource` 不存在 → 用 `ResourceBarrierUnorderedAccessView(resource)` 静态方法
- 🔧 `ResourceBarrierTransition` 命名参数
- 🔧 `using System.Numerics;` 加上

### DxrPipelineState.cs
- 🔧 `ShaderConfigDescription`、`PipelineConfigDescription` —— Vortice 自动生成的名称可能不同；用 ILSpy 或 Vortice 源码确认
- ✅ 已修：`DxilLibraryDescription` 取 `byte[]`（不是 `IDxcBlob`）+ `ExportDescription[]`

### DxrSwapChain.cs
- 🔧 `IDXGIFactory2.CreateSwapChainForComposition<IUnknown>` 不是泛型方法 —— 改用非泛型重载，cast 结果
- 🔧 `SwapChain.GetBuffer<ID3D12Resource>(0)` —— 验证签名

### DxrResources.cs
- 🔧 `ResourceDescription` 命名参数：
  - `sampleDescription:` → `sampleDescription`（注意大小写，实际可能是 `SampleDescription`）
  - `clearValue:` → `ClearValue` 命名参数
- 🔧 `CreateCommittedResource` 命名参数

### DxrRenderer.cs
- 🔧 `DispatchRaysDescription` 构造函数需要 4 个参数（多了 `callableShaderTable`）
- 🔧 `_device.Fence.SetEventOnCompletion(...).Wait()` —— 同 DxrDevice
- 🔧 `[StructLayout(LayoutKind.Sequential, Pack = 4)]` 中的 `Vector2` 字段 —— `System.Numerics.Vector2` 有字段 `X`、`Y`（OK）

## 修复建议

针对每个 error：
1. 查 Vortice 源码：https://github.com/amerkoleci/Vortice.Windows/tree/main/src/Vortice.Direct3D12
2. 用 grep / Read tool 找到正确名称
3. 修改文件

最关键的两步：
- **A.** 用 `dotnet build` 反复迭代，每次修复看到的 error
- **B.** 找不到的类型就用 Vortice 源码里的 MarshalStruct + 手写一个轻量包装

## 已经完成的部分（与 spec 对齐）

| Spec 章节 | 状态 |
|---|---|
| §4.1-4.4 HLSL shader（payload 字段闭环 + bounce 循环） | ✅ 已写 |
| §4.5 AS 拓扑（单 BLAS 多 AABB + 单 TLAS instance） | ✅ 已写 |
| §5.2 DxrRenderer（命令录制 + DispatchRays + CopyTextureRegion + Present） | ✅ 结构完成，命名需调整 |
| §5.3 AS 构建（5 AABB + 1 instance + GeometryIndex 选择） | ✅ 已写 |
| §5.4 WinUI3 SwapChainPanel 集成 | ✅ 已写（ISwapChainPanelNative COM） |
| §6 DXC 运行时编译 | ✅ 已写 |
| §7 RT PSO（HitGroup + ShaderConfig + GlobalRS + PipelineConfig） | ✅ 已写 |
| §8 Shader Table（4 条记录） | ✅ 已写 |
| §9 Root Signature（5 个 root 槽位） | ✅ 已写 |
| §10 注意事项 | 📌 见 项目说明.md |
| §十三 性能对比 | 📌 见 项目说明.md |

## 进一步修复的入口

```bash
# 单独构建本工程（跳过 wapproj）
dotnet build D:\code\winui\DXRDemo\DXRDemo\DXRDemo.csproj -p:Platform=x64 -p:Configuration=Debug
```

剩余 30+ 错误逐一修复后即可运行。运行后预期能看到 5 个球体（绿色 Lambertian、银色金属、玻璃 + 内气泡、超大地面），鼠标左键轨道旋转，滚轮缩放。