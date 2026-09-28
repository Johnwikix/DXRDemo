# DXR / ComputeSharp 性能诊断（2026-09-28）

结论：用户观察到的 DXR 约 60 FPS，首先受呈现方式限制；同时，在相同五球场景与工作量下，当前 DXR 程序化求交路径确实比直接解析求交慢。两者共同造成差距，不能只归因于 VSync，也不能据此认定 RTX 硬件普遍比 SM 慢。

本目录是隔离诊断程序。没有修改两个应用的渲染器、着色器或 UI。HDR 移植尚未开始。

后续已完成免费扫描模型的三角形基准，结果见 [MESH_RESULTS.md](MESH_RESULTS.md)：相同着色代码下，硬件三角形路径在本组模型中快约 8–24 倍。

## 已确认原因

1. **帧率上限不同。** `DXRDemo/MainWindow.xaml.cs:84` 从 `CompositionTarget.Rendering` 驱动渲染；`DXRDemo/DXR/DxrSwapChain.cs:124` 使用 `Present(1)`。ComputeSharp 的 `HdrSwapChainRenderer.cs:456` 使用独立线程循环，`:1019` 使用 `Present(0, DoNotWait...)`；队列满时处理 `WasStillDrawing` 并继续渲染。因此 60 对数百 FPS 并不是同一种呈现策略下的比较。ComputeSharp 的渲染吞吐、成功提交呈现次数与实际显示次数也应分开统计。
2. **默认工作量不同。** DXR `RayTrace.hlsl:35–36` 是 10 次最大反弹、4 SPP；ComputeSharp `RayTracePass.cs:52–53` 是 10 次最大反弹、2 SPP。关闭降噪不会自动对齐 SPP。两者都可能提前 miss，因此不能把 4×10 直接说成固定 40 条已执行路径射线。
3. **此场景没有发挥硬件遍历的规模优势。** DXR BLAS 只有五个 AABB；精确球面交点仍由 `IntersectionSphere` 的 HLSL 计算。`ShadowHit` 还在 SM 上遍历所有球体；光照、随机数、材质与散射也在 SM 执行。只有路径延续射线使用 DXR BVH。直接计算五个球体交点很便宜，而每次反弹进入硬件遍历有成本。下面的同场景消融测试确认该执行路径的净成本；没有使用 Nsight，不能进一步给 RT 核、调度、寄存器溢出各自分配耗时比例。
4. **折射射线有自相交问题。** `RayTrace.hlsl:408` 无条件使用 `hitPos + normal * 0.001`。折射方向朝法线负侧时，这会把起点放回入射侧，再次命中刚进入的表面。数值复现中重复交点 t=0.00114372，大于 TMin=0.001，因而不会被丢弃；按出射方向选择偏移符号后，下一个交点 t=1.74834。修正会改变图像和路径长度，属于正确性修复；不能把速度变化当作等画质优化。

## 本机测量

GPU：NVIDIA GeForce RTX 5070 Ti，驱动 616.64。Release 探针，1280×720，默认相机距离 6.5，固定时间与随机种子输入，最多 10 次反弹。每项预热 100 次、测量 300 次，串行运行。

DXR/派生 HLSL 使用同一个 D3D12 设备、根签名、五球数据、8-bit 输出格式和同一份着色逻辑。GPU 时间戳仅包围 DispatchRays/Dispatch，不包含编译、AS 构建、CPU 等待、复制或呈现。没有运行空间降噪；原始 DXR shader 保留累积代码，但 Frame=0、历史权重为零。

| 测试 | 采样数 | 耗时中位数 |
|---|---:|---:|
| 原始 DXR，独立多轮 | 4 | 约 2.9–3.1 ms |
| 原始 DXR，只将 SPP 改为 2 | 2 | 约 1.27 ms |
| 相同 RayGen/着色，只将 TraceRay 换成解析求交 | 4 | 约 0.73 ms |
| 同一份解析求交 HLSL 改为 compute dispatch | 4 | 约 0.485 ms |
| 相同着色、compute + inline RayQuery，仍用硬件 BVH | 4 | 约 2.89 ms |
| 原始 DXR，只按出射方向修正偏移 | 4 | 约 2.4–2.7 ms，输出改变 |
| 当前 ComputeSharp 原始路径追踪 shader，不运行降噪 | 4 | 约 0.49 ms，CPU 墙钟含提交/等待 |
| 当前 ComputeSharp 原始路径追踪 shader，不运行降噪 | 2 | 约 0.27 ms，CPU 墙钟含提交/等待 |

**关键对照：同场景、4 SPP，原始 DXR 约 3 ms vs compute 约 0.49 ms，约六倍。** 原始 DXR 的约 3 ms 又远小于 60 FPS 对应的 16.7 ms，因此呈现节奏同时遮蔽了它已有的吞吐能力。`1000 / GPU_ms` 只是着色器吞吐上限估算，不是应用最终 FPS。

原始 ComputeSharp 项目现在是四球加玻璃棱柱，DXR 是五球（含负半径内气泡），而且 RNG 状态更新规则、射线归一化和起点偏移不同。因此表中真实 ComputeSharp 数据仅供参考；证明 DXR 路径额外成本，应使用同场景 HLSL 的前几行。

桌面其他 GPU 工作没有被关闭，存在耗时尖峰；日志保留 p10、p90 和均值，不把小幅差异解释为确定收益。原始 DXR 重复测量中位数稳定在上述区间。

## 输出校验和未成立的假设

- 已读回完整 RGBA 图像。解析 RayGen 对原始 DXR 的 RGB 平均绝对差约 0.0073/255，99.93% 通道差不超过 1/255；compute 解析版本约 0.1075/255，98.20% 通道差不超过 1/255。不是黑屏或漏算场景造成的提速；不同执行路径的浮点舍入仍会改变少量随机路径。
- inline RayQuery 的正确性要求应用主动保留最近候选交点。最终生成器已经实现该条件，最终结果见 `measurements-inline.txt` 和 `validation.json`；早期未过滤更远候选的试验被废弃。
- 球体数据改为编译期常量约 3.02 ms，阴影循环显式展开约 2.87 ms；未消除主要差距。
- 将实际命中 payload 改为 id+t，材质着色移到 RayGen，约 3.10 ms；该试验的 PSO 最大 payload 声明仍为 64 字节，不能据此排除进一步调整最大栈/寄存器配置的收益。
- inline RayQuery 也没有明显改善这个极小程序化场景，不能把“换成 RayQuery”当成已经验证的解决方案。
- AS 仅初始化时构建，并非每帧重建。Release 探针没有 D3D12 debug layer，说明观测到的 GPU 差距无需 Debug 开销也会出现。
- `WaitForFenceValue` 的 `Thread.Sleep(0)` 忙等、每帧描述符重建、UI 线程阻塞值得整理，但本次证据不支持将它们当成六倍 GPU 执行差距的原因。
- DXR 的 FPS 文本用固定 0.5 秒除数，且共享会因相机变化归零的 `_frame`。它在交互或 UI 延迟时不够准确，后续应使用独立计数器和实际经过时间。

## 建议顺序

1. 统一独立渲染循环和可切换 VSync，分开显示 GPU 毫秒、渲染 FPS、成功呈现与丢帧数量。仅修改 Present 的参数仍保留 XAML 事件驱动，可能继续受刷新节奏限制。
2. 统一分辨率、SPP、反弹上限、相机、场景与 RNG，修复折射起点偏移，然后比较画质与速度。
3. 保留 RT 路线时，针对规模更大的三角形场景重新测量；当前五球解析测试不能代表网格场景的 DXR 优势。PREFER_FAST_TRACE、负载布局和更精确的 GPU profiler 分析是后续验证项，不承诺提速比例。
4. HDR 属于色彩与呈现链路。移植 ComputeSharp HDR 时，适合一并统一渲染调度和帧率统计；单独增加 HDR 不会解决目前的求交开销。

## 复现

要求当前两个项目仍保持相邻目录、本机已有 .NET 10+ 和缓存的项目 NuGet 包。`NuGet.Config` 禁用远程源以复用本机缓存。设置 `DXR_DXC_PATH` 指向有效 dxc.exe；未设置时脚本使用 DXR Release 输出附带的编译器。

在本目录运行：

```powershell
.\run.ps1
# 可选：另一分辨率
.\run.ps1 -Width 1920 -Height 1080
```

`Program.cs` 链接应用 DXR 支撑类和 ComputeSharp 原始 shader；生成器将应用 HLSL 的副本及单变量试验放入被忽略的 `variants/`。应用源文件保持不变。`validation.json` 保存自相交复现和读回比较；`measurements-*.txt` 保存各轮原始统计。

官方背景资料：[DXGI Present 的 SyncInterval](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present)、[DXGI_PRESENT / DO_NOT_WAIT](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-present)、[NVIDIA 的硬件求交与场景组织建议](https://developer.nvidia.com/blog/best-practices-using-nvidia-rtx-ray-tracing/)。上述性能数字来自本机实测，不是官方文档提供的估算。
