# 三角形模型实测：DXR 与 ComputeSharp 软件 BVH

2026-09-28，本机 NVIDIA GeForce RTX 5070 Ti，驱动 616.64。

**独立基准已完成，结果方向与原先五球场景相反：DXR 三角形路径在本组测试中快约 8–24 倍。** 两个应用的正式场景和 HDR 均未修改。

## 实测结果

统一 1280×720、4 SPP、最多 10 次反弹；关闭降噪、累积、HDR、窗口呈现。场景包含下载的三角形模型、解析地面、天空、太阳直接光与阴影、余弦加权漫反射路径延续。固定相机、随机种子和 Lambertian 材质。每个计时阶段至少预热 750 ms，再采集 60 帧。

| 模型 | 三角形数 | DXR GPU ms | ComputeSharp shader / 软件 BVH GPU ms | DXR 加速比 |
|---|---:|---:|---:|---:|
| Dragon LOD4 | 11,102 | 0.468 | 3.874 | 8.27× |
| Dragon LOD3 | 47,794 | 0.499 | 5.194 | 10.41× |
| Bunny | 69,451 | 0.544 | 6.163 | 11.33× |
| Dragon LOD2 | 202,520 | 0.552 | 7.007 | 12.70× |
| Armadillo | 345,944 | 0.505 | 4.492 | 8.90× |
| Dragon 完整版 | 871,414 | 0.622 | 14.926 | 23.98× |

表中 DXR 是软件阶段前后两轮中位数的平均值。最高精度 Dragon 的两轮分别为 0.629 和 0.616 ms。软件阶段的 p10/p90 为 12.96/17.16 ms，存在桌面 GPU 负载引入的抖动。完整原始统计见 `mesh-output/*-results.json`，汇总见 `mesh-output/summary.json`。

这些是 GPU 执行时间，不是两个原始窗口的显示 FPS。仅按渲染耗时换算，最高精度 Dragon 对应约 1,607 vs 67 帧/秒的着色器吞吐；完整应用还需要呈现、UI、同步等工作，不能直接承诺同等窗口 FPS。

## 两条路径如何保证可比

- **同源着色：** `MeshShader.cs` 由 ComputeSharp 3.2.0 生成 HLSL。DXR 版本只替换 `HitMesh` 和入口包装，继续使用同一份相机、PRNG、材质、采样和积分代码。
- **真实 ComputeSharp：** 基准实际调用 `GraphicsDevice.For`，不是只用另一份手写 HLSL冒充 ComputeSharp。为取得可比的 GPU 时间戳，还把该 ComputeSharp shader 内嵌的原始 DXIL 字节码原样交给 D3D12 测量；软件与 DXR 的 GPU 计时使用同一设备、DIRECT 队列和输出格式。
- **软件 BVH：** CPU 构建 12-bin SAH 二叉 BVH，每叶至多 8 个三角形，最大实测深度 23；GPU 使用父指针无栈回溯，按射线方向和分割轴近端优先访问，Möller–Trumbore 精确求交；阴影命中后提前返回。不会对每条光线线性扫描全模型。
- **DXR：** 一个静态三角形 BLAS、一个 TLAS 实例，Opaque、PreferFastTrace。使用硬件三角形求交，不再运行自定义球面 intersection shader。主/延续射线找最近命中，阴影使用 ACCEPT_FIRST_HIT_AND_END_SEARCH。Payload 只有 t 与 primitive id，共 8 字节。
- **相同数据：** 模型仅平移和统一缩放；两个后端使用同一份重排后的三角形，材质、背面处理、TMin 与起点偏移一致。双方静态几何数据均放在 GPU 默认堆，计时不含上传或 AS/BVH 构建。
- **单独校验封装开销：** 实际 ComputeSharp.For 的同步调用墙钟中位数依次为 3.68 / 5.67 / 5.77 / 6.65 / 7.76 / 13.06 ms。它使用 ComputeSharp 自己的队列，测量阶段和范围不同，不能与表中 GPU 时间相减来推算封装成本。其作用是确认真实库路径确实运行且有同数量级表现。

## 输出验证

已读回每个模型的完整浮点图像，验证所有值有限，并检查主射线命中掩码、深度和路径追踪结果：

- 实际 `ComputeSharp.For` 与计时用同一 DXIL 的输出，六个模型均逐浮点值一致（最大差 0）。
- DXR 与软件 BVH 的主射线命中掩码：五个模型完全一致；Dragon LOD2 仅 1/921,600 像素不同。
- 命中深度平均绝对差为约 2.0e-7 至 1.1e-5 世界单位；三角形浮点求交路径不同会产生微小差异。
- 完整路径追踪线性 RGB 平均绝对差为 2.2e-7 至 1.7e-6。速度优势不是漏画模型或减少光照产生的。
- 已检查并生成 `mesh-output/comparison.png`，左侧 DXR、右侧软件 BVH；两侧使用同一个 Reinhard+gamma 展示变换，变换不在 GPU 基准中计时。

## 结论的范围

原先 DXR Demo 使用五个 AABB 程序化球体，精确球面求交及大部分光照仍由 SM 完成；小场景的 DXR 路径开销超过了直接解析求交省下的工作。此基准使用大量真正的三角形，RT 硬件负责遍历和三角形求交，优势明显。原应用的 `Present(1)` / XAML 驱动导致的约 60 FPS 上限仍是独立问题，换模型并不会自动解除它。

这是一套明确实现的软件 BVH 基线，不代表 SM 路径追踪的最高性能。更宽的 BVH、压缩节点、波前重排、持久线程等优化可能缩小差距；本次没有对 RT 核/SM 内部耗时做 Nsight 归因。

不能只按不同模型的三角形数排序预测耗时：Bunny 的模型像素覆盖约 23.13%，Armadillo 约 12.84%，Dragon 各档约 13%；表面结构、遮挡和次级射线也不同。因此 Armadillo 虽然面数更多，仍比 Bunny 软件路径快。**同一 Dragon 的四档 LOD 更适合看规模趋势：从 11,102 增至 871,414 面，DXR 约 0.47→0.62 ms，软件 BVH 约 3.87→14.93 ms。**

## 来源与复现

模型来自 [Stanford 3D Scanning Repository](https://graphics.stanford.edu/data/3Dscanrep/)，研究使用条款、下载地址与 SHA-256 见 `models/SOURCES.md`。报告中的三角形数读取实际 PLY，不照抄网页摘要。

在本目录运行：

```powershell
.\run_mesh.ps1
```

要求 .NET 10+、Python（NumPy、Pillow）、DXR GPU 和本机已缓存的 NuGet 依赖。首次下载需要网络；DXC 默认复用 DXR Release 输出中的 dxc.exe，也可设置 `DXR_DXC_PATH`。模型与大图像/浮点数据保存在本地忽略目录，小型源码、结果摘要和研究记录可随项目保存。

只重测一项：

```powershell
dotnet bin/Release/net10.0-windows10.0.22621.0/PerfProbe.dll mesh dragon_vrip 1280 720 4 10
```

本次验证：探针 Release 构建成功（零警告/错误）；六档端到端实际执行成功；几何/深度/颜色一致性校验全部通过。没有替换应用窗口里的正式场景。
