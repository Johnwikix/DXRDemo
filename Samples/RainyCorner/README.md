# 雨后便利店街角 · Koyomi Mart

通过本机 Blender 5.2.2 LTS 的 Blender MCP 建模并导出；这是原创几何场景，没有下载第三方模型或材质。

- `RainyCorner.blend`：可编辑 Blender 源场景，3,732 个对象。
- `RainyCorner.glb`：供 DXRDemo 加载，340 个共享网格、3,727 个网格实例、2,097,886 个三角形（含实例）、29 个导出材质（渲染器加默认材质后为 30）、3 盏内嵌灯光。
- `RainyCorner-preview.png`：Blender Cycles 渲染参考，**不是 DXR 截图**。
- `RainyCorner-dxr.png`：DXRDemo + NRD 的实际离屏输出。
- `build_scene.py`：可复现的建模脚本，分为建筑、室内、街道与导出阶段。MCP 调用按阶段执行这些代码。
- `refine_scene.py`：细化脚本，补充成型瓶身/瓶盖、罐口拉环、包装封边/条码、货架价签/网架、售货机操作面板、门窗密封条/铰链、自行车辐条/轮毂、空调格栅/散热片及街道紧固件；复用共享网格。
- `repair_scene.py`：实际装配修正，重建自行车、护栏和垃圾桶，修正接地、排水格栅和连续灯带；由细化脚本导出阶段自动执行。
- `model-audit.json`：28 处接地检查、垃圾桶与墙面间距、篮筐支架连接、链线和灯带检查结果。
- `RainyCorner-bicycle.png`、`RainyCorner-basket.png`、`RainyCorner-vending.png`、`RainyCorner-contacts.png`：Blender 近景检查图。为便于检查结构，近景增加了临时中性面光补光；该灯只存在于渲染进程，不保存到模型，也不导出到 GLB。
- `render_review.py`：用本机 Blender 渲染全景和近景；全景使用源场景的夜间灯光。

场景包含完整正方形底座、玻璃便利店、自动门、灯箱招牌、雨棚、两台售货机、自行车、伞架、垃圾桶、路灯、电线杆/线缆、路牌、护栏、停车线、斑马线、排水沟、小巷、空调外机和公告栏。规则椭圆积水已移除，路面保留适度湿润反射。室内包括零食货架、满载饮料柜、便当/饭团陈列、收银台、咖啡机、关东煮柜台、杂志架、冰柜、导视和后场门。场景没有人物。

售货机使用带遮光槽的 4 条竖向 LED 发光面和 2 条顶部发光面，店内使用 3 条向下发光的长条灯具；灯带通过真实网格和 `KHR_materials_emissive_strength` 导出，由 DXR 的三角形面光采样照明。原来的售货机和店内灯带替代点光源已移除。其余内嵌灯光为 1 盏侧面霓虹点光源和门口、路灯的 2 盏聚光灯。导出明确启用 `export_lights`，用 `RAW` 转换保留 35～180 的强度数值；不宣称 Blender 与 DXR 的曝光及灯光单位换算完全一致。

2026-09-30 的装配修正：自行车两轮和侧撑鞋实际接触路面，车体倾斜 7°；包含双侧叉架、48 齿前齿盘、18 齿后飞轮、102 节闭合链条、曲柄踏板、刹车线、挡泥板、后货架和成形坐垫。开放式网篮通过车把夹具和前轮轴上的双侧托架连接车体。垃圾桶重建为带真实投放开口的空心壳体，脚垫落在侧墙外的路面，与墙面净距 0.29 米。护栏底板、售货机脚垫、伞架及空调支脚均有实际地面接触，接地误差阈值为 0.2 毫米；移除了悬空的排水格栅。

程序启动时默认选中此场景，启用「仅使用模型光照」并设置曝光 -2 EV；设置中的场景列表可随时切回。GLB 随构建和发布打包到 `Assets/Models/RainyCorner.glb`。关闭外部天空和太阳后，模型灯光、自发光及反射依然存在。实际预览使用原生 800×600、NRD、2 SPP、10 次反弹和曝光 -2 EV。低采样透射仍有平滑及残余噪点；Blender 参考图不代表实时 DXR 的画质。

玻璃使用 `KHR_materials_transmission`（透射率 1）、`KHR_materials_ior`（1.5）和 `KHR_materials_volume`；alpha 为 1，粗糙度 0.06。封闭玻璃几何提供实际折射路径厚度。导出器可能裁掉未连接的厚度设置组，因此脚本在导出的 GLB JSON 中保留明确的 volume 参数，不修改几何缓冲。金属和湿地反射使用 Metallic-Roughness。日本文字由本机游ゴシック字体生成，GLB 中已转换为几何轮廓。

## 复现

在 Blender MCP 中先检查场景，创建独立场景后依次执行 `build_scene.py` 的 `setup()`、`interior()`、`street()`、`finish()`，然后在该场景执行 `refine_scene.py` 的 `start()`、`products()`、`fixtures()`、`street()`、`export()`。`export()` 自动执行 `repair_scene.apply()` 和装配检查，避免重新导出旧结构。细化脚本检查 `detail_revision`，装配脚本检查 `assembly_revision`，重复 `apply()` 不会追加对象。脚本不会删除其他场景；重新制作时使用干净的 Blender 文件以保留约定的材质名称。

单独修正已有源文件，可分阶段执行 `repair_scene.py` 的 `begin()`、`street()`、`bicycle()`、`vending()`、`export()`；`begin()` 重建本修正阶段的集合，因此请在修改该集合中的物体后保留备份。跨 MCP 调用需将模块保存在 `sys.modules` 中。渲染参考图和全部近景：

```powershell
& 'D:\Program Files\Blender\blender.exe' --background .\RainyCorner.blend --python .\render_review.py
```

从 `diagnostics/integration` 执行：

```powershell
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene-preview ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --glass ../../Samples/RainyCorner/RainyCorner.glb
```

完整集成测试包括 glTF/GLB、灯光、透明度、纹理、NRD/SR、模型光照开关和内置兔子的阴影回归；D3D12 Error/Corruption 会使测试失败。

本次修正已完成装配几何检查、导出后灯带位置检查、Blender 全景及 4 张近景渲染、DXRDemo Release 构建，以及最终 GLB 的 `--scene-preview` 48 帧实际 NRD 渲染。此次没有重新运行整个集成测试集。
