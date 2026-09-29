# ReSTIR DI、直射阴影与折射焦散

`DxrSceneBackend` 的 glTF/GLB 场景默认启用。旧球体、Stanford mesh 和软件后端保持原有算法。实现位于 `SceneTrace.hlsl`、`SceneLighting.hlsli` 和 `DxrSceneBackend.cs`；无新增 SDK 依赖。后端 `RestirEnabled` / `CausticsEnabled` 属性用于诊断 A/B，不新增 UI 设置。

## ReSTIR DI

- 在离散解析灯光与自发光三角形面积的联合域中采样，两类都存在时各分配一半候选概率。三角形由面积 CDF 选择，再均匀采样面积；光源概率与面积密度均参与权重。
- 目标函数为当前 BSDF、光源辐射与几何项的亮度，不包含可见性。RIS 权重为 `target / proposal`，最终 `W = sum(weights) / (M * target(selected))`；合并旧 reservoir 时重新计算当前目标并使用 `target_current * W_old * M_old`。
- 主表面的第一条样本使用 8 个新候选，加上一帧重投影位置和 4 个随机邻域 reservoir。其他 SPP、次级反弹使用 4 候选 RIS。环境天空保留原有独立采样，发光面的 MIS 分区与 BSDF 继续路径一致。
- 64 字节 reservoir 双缓冲；当前帧只读上一帧，避免同 dispatch 跨像素读写竞争。最终可见性每次重新追踪，不复用旧阴影。
- 法线、材质、粗糙度、世界位置和切平面阈值拒绝不兼容历史。尺寸、场景、光照、SPP、反弹数、FOV、相机切换、帧序号不连续或显式 reset 会禁用历史。无效像素写空 reservoir。
- 采用有偏的实时复用估计器，没有完整可见性偏差校正，历史相关性和低粗糙度表面仍可能产生噪声。不是 ReSTIR GI/PT，也不等价于 RTXDI SDK 的完整实现。

无发光面且解析灯不超过 2 盏时，保持确定性逐灯计算，以保留单太阳的锐利阴影并避免不必要的复用开销。ReSTIR 随机照明进入 NRD/RR 的 diffuse/specular 通道，不放入无需降噪的信号。

## 阴影

先使用 `ACCEPT_FIRST_HIT_AND_END_SEARCH` 查找不透明遮挡物，跳过玻璃；遇到遮挡即返回，不执行完整材质求值。场景没有 transmission 时只有这一次查询。包含玻璃时，再用有序最近交点查询累计透射；第二次查询跳过不透明/覆盖率材质，避免 alpha 被重复随机采样。

`CausticGeometry` 按世界空间、面积加权的几何法线协方差寻找主轴；至少 95% 的面积法线与主轴平行（绝对余弦 ≥ 0.9995）时按平行玻璃处理，保留连续直射透射。这个识别不依赖模型名称、网格三角形大小、旋转或镜像；法线贴图玻璃保守地保留焦散路径。其余实体折射几何通过实例标志交给光子通道，不再叠加直线透射近似。主射线没有 BLEND 覆盖率时，复用确定性引导命中，省去重复的主相交查询。

## 折射焦散

首次执行 `ClearPhotonGrid → PhotonGen → RayGen`，dispatch 间添加 UAV barrier。静态几何和光照下复用世界空间光子图；相机运动、降噪切换和相机历史重置不改变光子样本。场景、太阳方向/强度、核设置或光线裁剪参数变化时重建。没有聚焦候选几何或解析灯时跳过光子 pass。

- 131,072 条光子，以逐灯分层、二维低差异序列采样方向光、点光、聚光或全局太阳。仅用聚焦候选几何的世界包围球限制发射区域，避免把大面积橱窗也计入发射预算；方向光从投影圆盘发射，点光从覆盖包围球的立体角锥发射。功率按每盏灯实际分配的样本数及发射面积/立体角归一化。
- 最多 12 次表面相交，支持 Snell 折射、Fresnel 分支、全反射、粗糙微表面、七层正确嵌套介质和实际路径长度的 Beer–Lambert 吸收。光子使用功率传输，不乘相机辐射传输的 `eta²`。
- 只保存经过聚焦候选几何折射后到达非透射表面的光子；只穿过平行橱窗的路径不写入 map，避免与连续透射重复计能。按接收面的漫反射和镜面 BSDF 分别求值。
- 65,536 桶世界空间哈希表，原子发布链表；一个光子最多存一个节点。查询邻近 27 个网格，校验真实 cell，防止哈希碰撞重复计能；法线与平面距离抑制跨面漏光。
- 使用积分为 1 的 Epanechnikov 圆盘核，半径为聚焦候选包围球半径的 1.5%（至少 near × 8）。光子镜面估计单独使用最小 0.35 的粗糙度作为角向带宽，防止有限光子估计落入近似 delta 的 GGX 峰；这不修改材质、普通直射、相机路径的 BSDF 或 NRD/RR 的粗糙度输入。

这是有限光子数、有限核宽度和有限角向带宽的有偏估计。平行玻璃保留直线近似，未精确重建厚平板的横向位移；镜面焦散会比真实的尖锐高光更宽。当前不采样天空或自发光面发出的焦散，不包含色散或先经金属反射生成的焦散。多个相距很远的聚焦体仍会降低发射效率；尚未做自适应光子预算或核半径调整。非嵌套交叠介质沿用原渲染器限制。

## 验证

从 `diagnostics/integration` 运行 `IntegrationProbe.exe --lighting`。测试启用 D3D12 debug layer，使用实际 production shader 与 DXR 后端。结果写入忽略目录 `output/lighting/results.json`，玻璃球单反弹接收面输出为 `caustics.ppm`（玻璃自身因反弹预算为 1 显示黑色）。

2026-09-29 修复后专项测量，192×144、1 SPP、1 bounce、128 点光源：

| 项目 | 结果 |
|---|---:|
| 逐灯计算平均耗时 | 1.904 ms |
| ReSTIR 平均耗时 | 0.879 ms |
| 64 帧平均能量 / 逐灯参考 | 0.99945 |
| 64 帧平均 MSE / 首帧 MSE | 0.00113 |
| diffuse × albedo + specular + unfiltered 重组误差 | 4.77e-7 |
| 自发光面积光 64 帧平均能量 / 原采样参考 | 0.99917 |
| 球体焦散峰值 / 无遮挡照度 | 20.54 |
| 聚焦像素数（超过直线透射与无遮挡参考 1.5 倍） | 100 |
| IOR=1 接收面误差 | 0 |

耗时是 CPU stopwatch 包含命令录制、GPU 完成等待的 16 帧均值，不是 GPU timestamp，也不能外推为默认便利店或完整应用帧率。64 帧平均与首帧的误差比较包含时间平均的收益，不单独证明邻域复用收益。检查还覆盖相机切换、重复帧、光源关闭、尺寸重建和无 D3D12 Error/Corruption 消息。

点光和聚光分别产生 104 / 105 个超过直线透射参考 1.5 倍的接收面像素。新增三组平板玻璃回归（普通、旋转/镜像缩放、密集细分/倾斜）在原实现上首先复现了 0.905442 的原始像素误差，修复后三组与连续透射参考的最大误差均为 0。光子缓存回归验证相机切换不重建、太阳开关和强度变化必须重建。

原有 `--scene` 完整回归覆盖真实 NRD dispatch、四种降噪路径、默认 Rainy Corner 启动和场景切换、自由相机/重置、可用 NRD+SR 组合。修复另外使用 `--lighting-scene nrd-fixed` / `dlssd-fixed` 实际运行便利店 48 帧、640×400、2 SPP、10 bounces；断言对应算法实际执行而非回退，并导出 PNG。两种输出中地面大面积离散白斑消失，焦散开启、光子构建次数为 1。普通低 SPP 路径采样噪声及重建模糊仍有可能存在，这不等于所有视角均已无噪点。

算法依据：[NVIDIA ReSTIR DI 集成说明](https://github.com/NVIDIA-RTX/RTXDI/blob/main/Doc/Integration.md)、[NVIDIA 实时光子焦散说明](https://developer.nvidia.com/blog/generating-ray-traced-caustic-effects-in-unreal-engine-4-part-1/)、[PBRT 光子估计与高光泽表面的采样限制](https://www.pbr-book.org/3ed-2018/Light_Transport_III_Bidirectional_Methods/Stochastic_Progressive_Photon_Mapping)。本实现未复制其 SDK 或 UE 分支。

## 修复前的圆斑定位记录

2026-09-29 用户报告：地面、玻璃后的表面出现大面积离散亮斑，关闭降噪及 DLSSD 下尤其明显。先前的聚焦、能量、API 和 NRD 流程检查没有覆盖这个真实场景的视觉质量问题，不能据此宣称焦散已达到可用质量。

使用 `--lighting-scene` 固定相机、帧 0–7、2 SPP、10 bounces，无降噪和超分，分别切换 ReSTIR 与焦散。另在诊断输出目录临时将 Raw 输出替换为主表面焦散 diffuse、specular 及其和，捕获后恢复了着色器。生产渲染算法未在这次定位中更改。

- 关闭 ReSTIR 仍保留圆斑；关闭焦散后这些光子圆斑不再生成，普通低 SPP 噪声仍在。
- 单独的焦散输出重现了截图中的离散圆斑，说明伪影在降噪输入之前已经存在。
- 同一固定帧的 primary caustic RGB 总和为 9,131.26，其中 specular 为 7,001.91，约 76.7%。RGB 峰值：合计 150.10、specular 149.85、diffuse 12.45。这里是单个诊断视角/帧，不代表所有场景的比例。
- 引入点为 `PhotonGen` / `GatherCaustics`：每帧全场景 32,768 条光子，以玻璃总包围球半径的 1.5% 为固定核半径，稀疏光子直接形成可见圆盘。`specular += energy * r` 又把部分样本经低粗糙度 GGX 大幅放大。
- `Visibility` 对实体玻璃后的解析直射直接返回零，将原先连续的透射照明也交给这个稀疏估计；平面玻璃不会必然聚焦，但当前实现仍统一使用该通道。

定位产物在 `diagnostics/integration/output/lighting/scene/`：`before-fix`、`no-restir`、`no-caustics`、`neither`，以及 `photons`、`photons-diffuse`、`photons-specular`。修复后产物为 `after-fix`、`nrd-fixed.png`、`dlssd-fixed.png`。修复通过透射路径划分及光子采样/镜面带宽处理解决根因，没有改动降噪器强度。
