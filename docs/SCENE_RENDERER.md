# glTF/GLB 实时 DXR 场景渲染器

## 操作

- 右上工具栏「打开模型」或 Ctrl+O，选择 `.gltf` / `.glb`；也可以将文件拖到视口。左上显示 GPU 调试信息。
- 启动默认选中「雨后便利店 · Rainy Corner」，GLB 随构建/发布复制到 `Assets/Models/RainyCorner.glb`，无需手动打开。默认仅使用模型光照、曝光 −2 EV；可在设置中启用外部光照。便利店与三个 Stanford 模型作为固定演示选项，导入文件不会移除它们。
- `.gltf` 引用的 `.bin` 和图片需要保留原有相对路径；GLB 可以内嵌缓冲和图片。
- 文件包含多个场景时，在设置窗口的「场景模型」中切换。内置 Stanford 模型仍可选择。
- 轨道相机：左键拖动旋转，中键平移，滚轮缩放。
- 自由相机：先点击视口，W/S 前后、A/D 左右、Q/E 升降，右键拖动观察，Shift 加速，滚轮调速。
- 工具栏和设置窗口的重置按钮、视口焦点下的 R，恢复当前场景的初始取景。模式切换保持当前视图。
- 设置提供移动速度、环境亮度和曝光 EV。环境强度设为 0、太阳关闭时可以检查模型自身灯光；导入场景不自动添加太阳或地面。
- 「全局太阳光」默认关闭，可设置方位角（0° 为 +Z，90° 为 +X）、高度角（0° 为地平线，90° 为正上方）和强度。应用于导入场景与内置光追场景，独立于模型嵌入灯光；改变太阳参数即时重置光照历史，无需重建场景。当前为方向光硬阴影。
- 「仅使用模型光照」统一禁用外加的天光与太阳，保留模型内嵌灯光、自发光材质和它们的间接照明；取消开关恢复原先外部光照参数。
- 加载期间可取消；解析/上传失败保持先前场景。GPU 上传和加速结构构建期间可能暂停呈现，但 UI 线程仍可响应。

## 当前支持范围

| 项目 | 支持情况 |
|---|---|
| 文件 | glTF 2.0 JSON + 外部资源、GLB、Data URI，SharpGLTF.Core 1.0.6 解析 |
| 场景 | 多 scene、节点层级、矩阵/TRS、重复网格实例、负缩放/非均匀缩放、包围盒自动取景 |
| 网格 | TRIANGLES、TRIANGLE_STRIP、TRIANGLE_FAN；索引、法线、切线、顶点颜色、TEXCOORD_0/1；accessor 解码由 SharpGLTF 提供 |
| 材质 | Metallic-Roughness；基础色、金属度/粗糙度、法线、遮蔽、自发光贴图及因子；双面材质 |
| 图片 | PNG/JPEG，按用途区分 sRGB/线性；生成 mip 链，显式 ray-cone LOD；读取 wrap/filter |
| 透明度/玻璃 | OPAQUE、MASK、BLEND；覆盖率透明与实体透射分开；`KHR_materials_transmission`、`KHR_materials_ior`、`KHR_materials_volume`，含透射/厚度贴图 |
| 灯光 | `KHR_lights_punctual`：方向光、点光源、聚光灯；颜色、强度、范围、锥角和节点变换 |
| 常用扩展 | `KHR_texture_transform`、`KHR_materials_unlit`、`KHR_materials_emissive_strength`、`KHR_mesh_quantization` |
| 光照 | GGX 镜面反射、漫反射、ReSTIR DI、环境照明、自发光三角形采样、MIS、折射焦散、路径继续与 Russian roulette |
| 降噪/显示 | NRD RELAX_DIFFUSE_SPECULAR、FSR/XeSS/DLSS SR、DLSSD 光线重建接入（运行验证留给用户）、线性滤波、曝光、SDR/HDR 编码 |

这是静态场景渲染器，不宣称完整支持所有 glTF 扩展。蒙皮和 Morph Target 暂时明确拒绝，节点动画显示静态姿态并提示；点/线图元、UV 集 2 以上暂不支持。

Draco、Meshopt、KTX2/BasisU、WebP 解码尚未接入。有标准回退数据的可选扩展使用回退并提示；不支持的 `extensionsRequired` 会拒绝加载。图片没有 PNG/JPEG 回退时明确报告错误。当前每个 GPU 场景最多 1024 个纹理视图；同一图像用于 sRGB 和线性数据时会占用不同视图/纹理。

BLEND 表示覆盖率透明；玻璃使用独立的 transmission/IOR/volume 路径。透射包含 Snell 折射、Fresnel 反射、全反射、粗糙微表面和 Beer–Lambert 吸收；封闭几何提供实际穿行厚度，厚度为零时按薄片处理。最多支持七层正确嵌套的实体介质；重叠且不嵌套的体积未完整处理。解析灯光穿过具有聚焦可能的实体玻璃时，由光子焦散通道计算照明；平行平板玻璃保留连续透射阴影，避免用稀疏光子替代窗户透光。薄片、自发光网格和环境的透射阴影仍采用直线近似。焦散在非透射接收表面按漫反射/镜面 BSDF 分别求值；光子镜面估计使用有限角向带宽，会适度展宽尖锐镜面焦散。Clearcoat、sheen、各向异性以及 OBJ/FBX 导入器仍是后续工作。环境照明目前为可调程序化天空，尚未提供 HDRI 文件导入。

多光源样例位于 `Samples/RainyCorner`，通过 Blender MCP 制作，包含 2,019,650 个实例化三角形、3,199 个网格实例、24 个导出材质（含渲染器默认项为 25）和 8 盏灯。瓶罐、包装、货架、五金及街道设备补充了真实几何细节。附 `.blend` / `.glb`、可复现脚本、Blender 参考图和实际 DXR 预览。

## 相机与线程

`CameraController` 用相对鼠标位移更新角度；按下事件只初始化拖动起点，解决原先绝对坐标重新映射造成的跳变。捕获丢失、失焦、重置时清理输入。WASD 由渲染时间步进驱动，斜向输入归一化。相机帧是值类型，锁内更新后同时交给光追和重投影，帧内不构造委托/集合。

导入层在后台解析并解码图片，产生共享 mesh/material/texture 数据及每个 scene 的实例列表。渲染线程上传候选 GPU 场景，构建成功后才发布选择项并完成异步请求。相机重置不重新加载文件或构建加速结构。GPU 资源通过队列 fence 保护销毁。

## 渲染与重建

`DxrSceneBackend` / `SceneTrace.hlsl` 为导入场景提供显式布局的索引几何、每网格 BLAS、节点 TLAS 实例、材质/灯光缓冲、纹理和采样器描述符表。旧 `DxrMeshBackend` 与软件 BVH 保留为内置模型的对照路径，共用新的相机帧。

主命中插值顶点属性，在世界空间使用逆转置法线；缺少法线时使用面法线，缺少切线时根据三角形 UV 构造切线基。纹理 LOD 使用世界 ray cone 到 UV 的面积换算，避免模型尺度改变贴图清晰度。

导入场景始终经过线性输出管线。带噪漫反射、镜面反射与无需降噪的照明分开保存，NRD 使用真实粗糙度、线性 view Z、相机运动矢量和各 lobe 的次级命中距离。概率 lobe 选择对应 NRD hit-distance reconstruction；天空/超出降噪范围的像素旁路，镜面信号不伪装成漫反射。

玻璃原先的问题来自随机 alpha 覆盖率改变主命中表面，以及透过玻璃的发光/直射背景被错误放入无需降噪的信号。现在单独发射确定性主引导射线，BLEND 后方的随机贡献全部进入滤波，折射信号进入镜面通道；次级距离跨过介质出射面继续累计。NRD 的 PBR 镜面预滤波半径由默认 50 缩至 2 像素。2 SPP 仍可能有平滑和残余噪点，尤其窗后小商品；单层主表面引导尚不能完整描述多层透射的运动。

DLSSD 使用独立的 NGX feature 和评价入口，消费原始带噪线性辐射、漫反射/镜面反照率、单位世界法线/粗糙度、真实镜面射线距离、深度、运动与相机矩阵。启用时旁路 NRD/自定义滤波和单独的 DLSS SR。界面按能力查询启用选项，失败时显示具体 NGX 状态并回退常规管线。此路径只做编译和静态接线检查，按用户要求未运行，详见 [超分与光线重建](SUPER_RESOLUTION.md)。

导入场景默认启用 ReSTIR DI：解析灯光与自发光三角形进入同一 reservoir，主表面生成 8 个候选，重投影复用上一帧中心与 4 个邻域样本，历史有效样本数限制为 32。最终只对选中的灯光执行可见性查询；次级反弹使用 4 候选 RIS。无自发光网格且解析灯光不超过 2 盏时保留逐灯确定性计算，硬阴影与发光在 NRD 后合成。ReSTIR、焦散、随机透明遮挡、环境采样和间接光进入对应 lobe 降噪。合成后的画面再交给 SR，因此低渲染比例下仍受 SR 重建质量影响。实现与验证限制见 [ReSTIR 与焦散](RESTIR_CAUSTICS.md)。

内置 Bunny/Armadillo/Dragon 的旧漫反射管线也独立输出主命中的太阳直射光，NRD 只处理剩余的天空与间接照明，之后加回直射光。回归不只比较全图平均误差，还隔离太阳、关闭环境，在兔子投影的 1,409 个地面阴影像素以及相机转动时逐像素比较原始参考；最大误差约 0.00032（FP16 合成误差）。

连续相机运动保留可重投影历史；重置、场景/照明变化和长时间间隔清空历史。投影 FOV/near/far 同时传递给光追、深度、NRD 和 SR。SDK 失败会报告实际回退；PBR 可以继续运行原生分辨率的线性滤波管线。

## 验证入口

从仓库根目录构建应用；没有完整安装所声明 Windows SDK 时，本机验证使用以下覆盖参数：

```powershell
dotnet build DXRDemo/DXRDemo.csproj -c Release -p:Platform=x64 -p:WindowsSdkPackageVersion=10.0.22621.57 -p:WindowsPackageType=None
dotnet build diagnostics/integration/IntegrationProbe.csproj -c Release --configfile diagnostics/performance/NuGet.Config
```

在 `diagnostics/integration` 目录运行实际 EXE（厂商 SDK provider discovery 依赖进程目录）：

```powershell
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --lighting
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene C:/Models/example.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --glass ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --sr
```

`SceneProbe` 生成可重现的 glTF/GLB、3×3 贴图和重复实例样例。检查相机帧率独立性、模式连续性、重置、导入取消/失败、材质参数、灯光、alpha、纯金属信号、lobe 能量重组、模型尺度与 mip、真实 NRD dispatch 及可用 SR。太阳测试验证开关、角度、强度线性关系，以及双面遮挡片的 225 个可见阴影像素；隔离直接光时，NRD 与关闭降噪的输出最大误差为 0。D3D12 Error/Corruption 消息会使测试失败。截图和日志写入忽略的 `output/`。

`--glass` 对 2,888 个可见玻璃像素比较 512 SPP 参考与 2 SPP 输出：在线性辐射经 `x/(1+x)` 压缩后，RGB RMSE 从 0.351406 降至 0.131150（约 63%）。同时检查材质 ABI、volume 导入、静止相机引导稳定、IOR 改变影响折射结果及 D3D12 调试层。它不会创建或执行 DLSSD feature；误差降低也不等于多层玻璃和运动质量已经完美。

## 性能与后续工作

加载时间包含后台解码、mip 生成、GPU 上传、BLAS/TLAS 构建和首次着色器/驱动编译；冷启动首次导入会更慢。静态场景不逐帧重建加速结构，帧资源和输出资源引用会复用。当前队列间仍采用保守同步，未宣称任意模型在任意 GPU 上达到固定 FPS。

后续优先项为压缩资产解码、HDRI 环境、纹理/显存预算、批量异步上传和 GPU 队列重叠；再扩展高级材质、动画和其他模型格式。ReSTIR 当前为有偏的实时复用版本，尚未实现光源树、ReSTIR GI/PT 或可见性偏差校正。

规范依据：[glTF 2.0](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html)、[punctual lights](https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_lights_punctual/README.md)、[SharpGLTF](https://github.com/vpenades/SharpGLTF)、[NRD](https://github.com/NVIDIA-RTX/NRD)。真实资产验证使用 [Khronos DamagedHelmet](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/DamagedHelmet)，下载文件仅保存在本地测试输出目录，许可证见原资产页面。
