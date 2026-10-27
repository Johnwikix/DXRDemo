# 项目约定

## 文件格式与语言

- 自有源码（C#、XAML、HLSL、`.targets`、文档）统一使用 Windows CRLF（\r\n）行尾，不要混用 LF；避免提交纯行尾变更的噪音 diff。
- `External/NRD/**` 与 `External/Upscalers/**` 在 `.gitattributes` 中标记 `-text`，按**原始字节**保存：`Verify.ps1` / `Restore.ps1` 对这些文件做 SHA-256 校验，任何行尾归一化、重编码或“顺手格式化”都会破坏校验。禁止改动这两个目录的既有文件字节。
- 文档、注释与交付说明以中文为主（与 README / docs 现状一致）；新文档放 `docs/` 并从 README 链接，不要另开平行的说明位置。

## 构建与运行

- 标准入口：`dotnet build DXRDemo/DXRDemo.csproj -c Release -p:Platform=x64`。本机 SDK 不匹配时才加 `-p:WindowsSdkPackageVersion=...` 覆盖；覆盖参数属于单次构建输入，不要写死进工程文件。
- Debug 与 Release 分别构建；运行前确认运行的是刚构建的对应目录产物，不要拿旧 exe 验证新代码。
- 先构建主应用再构建/运行探针：探针依赖主工程输出附带的 `dxc.exe / dxcompiler.dll / dxil.dll`（`DXR_DXC_PATH` 可覆盖）。探针统一 `--configfile diagnostics/performance/NuGet.Config` 走离线 NuGet 缓存，不引入远程源。
- 若 Visual Studio 正在构建/运行常规输出，用 `-p:OutputPath=... -p:IntermediateOutputPath=...` 隔离目录（仓库中 `bin/renderer-*`、`obj/validation-*` 即此用法），不要抢占 `bin\x64\Release`。
- 仅 x64 启用厂商重构（`EnableVendorReconstruction`），ARM64 自动排除 NRD/FSR/XeSS/DLSS；原生桥由 `Native/Reconstruction/Build.ps1` 在 PrepareForBuild 自动构建，仅需复用已构建 DLL 时传 `-p:SkipReconstructionNativeBuild=true`。
- `dotnet publish` 部署必须保留整个输出目录（PRI/XBF 由 `CopyAppPriAndXbfToPublish` 补齐），不能只拷 exe。

## External 厂商 SDK（NRD / OptiX / FSR / XeSS / DLSS）

- 版本与哈希固定：NRD 4.17.3（vendored `Runtime/NRD.dll` 含官方 DXIL），超分 DLL 由 `External/Upscalers/Restore.ps1` 校验恢复。常规构建只校验、不重建、不下载。
- 重建 NRD 需 PowerShell 7 + VS C++ + CMake，仅在用户明确要求时执行 `External/NRD/Rebuild.ps1`；重建会刷新校验和，属预期行为，需在交付说明中注明。
- 新增或更换厂商二进制时，同步更新 `SuperResolution/ReconstructionAssets.targets` 的复制项、校验脚本与 `Licenses/` 输出条目；上游许可证不得被应用许可覆盖。
- 保持“缺 `NRD.dll` → 旧降噪回退但不禁用 SR/超分”的降级行为；删除或绕过该回退是行为变更，需验证并记录。

## HLSL 与 C# 双端契约

- Shader 由 DXC 运行时编译，目标 `lib_6_9`，旧运行时回退 `lib_6_5`；修改 HLSL 后须确认两种目标都能编译通过，不能只看当前机器。
- 根签名槽位、payload 布局、常量缓冲与 `SceneData.hlsli`、`PbrSignals.cs` 等 C# 侧结构必须同步修改。构建成功不代表双端布局一致，需跑对应探针（如 `--contracts`）。
- 新增 `.hlsl/.hlsli` 必须放在 `Shaders/DXR/` 下才会被 csproj 通配复制进输出；放到其他目录不会生效。
- NRD 只走官方 dispatch，不得用自定义 pass 替换 NRD shader；玻璃在 opaque 降噪后合成、三层路径空间等信号契约见 `docs/NRD.md`、`docs/RTXPT_REWRITE.md`，改动前先读对应文档。

## 渲染架构

- 轨迹后端三选一：`DxrMeshBackend`（硬件 DXR，生产路径）、`SoftwareMeshBackend`（ComputeSharp 对照）、旧 5 球 `DxrRenderer`（历史对照）。重构不得破坏后端边界，也不要“清理”对照路径，除非用户明确要求。
- 交换链经 `ISwapChainPanelNative` 绑定 DXGI 合成交换链；渲染驱动方式（XAML `CompositionTarget.Rendering` vs 独立线程 + `Present(0, DoNotWait)`）是既有性能结论的变量，改动前先读 `diagnostics/performance/README.md`。
- Vortice 3.8.3 绑定层的 API 差异（Fence 无 `SetEventOnCompletion().Wait()`、命名参数等）已有既定解法，记录在 `DXR/REMAINING_WORK.md`；遇到绑定层限制先查该档案，不要盲目绕行。
- GPU 资源（COM 对象、描述符堆、upload 缓冲、fence、交换链）的创建与释放显式成对管理，清理幂等；必须处理窗口缩放的销毁重建与设备移除（device removed）路径。

## 验证与性能结论

- 验证入口是 diagnostics 探针，不是“能编译”或“能出图”。按改动风险选择模式：资源/材质/灯光 → `--scene`、`--contracts`；降噪与超分 → `--sr`、`--nrd`、`--glass-temporal`；时间累积 → `--temporal-convergence`；相机运动 → `--motion`；设置持久化 → `diagnostics/settings/SettingsProbe`；吞吐 → `diagnostics/performance`。断言语义见 `diagnostics/integration/README.md`。
- 断言阈值是契约：禁止为了让构建变绿而放宽阈值、删除断言或“修”测试。README 记录的已知基线失败（如 `--glass-sr` 粗玻璃对比度、`--lighting` 旧 ReSTIR 收敛）在判定回归前先核对，不要当作本次破坏。
- `--nrd-fixed` / `--dlssd-fixed` 等模式要求真实 SDK 执行，回退即失败；D3D12 debug layer 的 Error/Corruption 消息一律视为测试失败。
- 性能结论必须给出测量口径：GPU 时间戳覆盖范围（是否含编译/AS 构建/呈现）、预热、采样数与中位数（参照 `diagnostics/performance`）。HUD 的 RENDER/SUBMIT/FRAME 计数不是基准，不得据此宣称 FPS 或倍数。
- UI 行为用 lvt（或 `ui_settings.py`）在实际窗口上验证；探针日志与截图写入被忽略的 `output/` 目录，不提交。
- 行为、默认值或画质变更后，同步更新对应 `docs/` 主题文档与 README 相应章节（验证记录带日期与硬件）；新增/更换资产同步 `SOURCES.md` 署名与许可。

## 默认代码质量与审查标准

后续功能实现、修复、重构和 code review 默认执行以下高标准，无需用户重复提醒。审查必须覆盖功能正确性、重构方案设计、性能、内存分配和代码可读性；按风险投入验证，不以“能编译”或“画面变了”作为完成标准。

- **功能正确性**：沿真实调用链核对行为与兼容性，覆盖正常流程、空场景/加载失败/取消导入、窗口缩放、设置损坏回退、能力降级（SM 6.9→6.5、DXR tier、HDR→SDR、缺厂商 DLL）与设备移除。涉及随机性（种子、jitter、采样顺序）的改动要说明对确定性与既有收敛指标的影响。
- **重构设计**：先明确职责、依赖方向、GPU 资源所有权与生命周期，再确定拆分边界。保持后端抽象与单一状态源（设置在 `AppSettings`，渲染状态在后端）；避免只搬移代码却隐藏依赖或副作用。构造函数保持轻量，设备/交换链初始化与释放显式成对。
- **异步与生命周期**：渲染线程与 UI 线程边界清晰；fence 等待、迟到的合成回调、窗口关闭与设置窗口的释放顺序都要处理。COM 引用及时释放，清理幂等，单项失败不跳过其他资源；后台加载（纹理/mip/BLAS）必须有异常观察与取消路径。
- **性能**：每帧路径检查堆分配、描述符重建、上传堆 churn、屏障数量与 CPU 忙等；结论用 GPU 时间戳和对照消融支撑，区分 GPU 时间与 CPU 墙钟、着色吞吐与显示帧率。不为微优化牺牲正确性，也不把单场景结论外推到任意 GPU。
- **内存分配**：每帧路径避免闭包、装箱、LINQ 与集合复制（`CameraController` 的值类型帧是范式）；原生内存（committed resource、描述符堆、upload 缓冲）有明确上限与归还路径；没有测量不得宣称零分配或具体收益比例。
- **可读性**：命名准确、方法职责集中；注释解释约束与原因（如 csproj 中 MrtCore/Publish、Agility 的说明）并随实现同步更新；移除过时代码、重复守卫与失效文档，`DXR/REMAINING_WORK.md` 类档案保持真实。
- **验证证据**：运行与改动风险相称的验证——至少构建 Debug 与 Release 并运行相关探针模式；探针无法覆盖的（持续交互、多显示器、特定 GPU/驱动）明确列为未执行项。数值断言结果（RMSE、误差上限）原样引用，不四舍五入成“通过”。
- **审查输出**：按严重性报告可定位、可复现或有完整调用链证据的问题，给出文件位置、触发条件、影响和修复方向。区分本次引入的回归、既有问题与设计建议，不为覆盖维度凑数。说明已执行验证及剩余盲区。仅请求 review 时默认交付审查结果，生产代码修复按用户授权范围执行。
