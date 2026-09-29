# 雨后便利店街角 · Koyomi Mart

通过本机 Blender 5.2.2 LTS 的 Blender MCP 建模并导出；这是原创几何场景，没有下载第三方模型或材质。

- `RainyCorner.blend`：可编辑 Blender 源场景，3,208 个对象。
- `RainyCorner.glb`：供 DXRDemo 加载，233 个共享网格、3,199 个网格实例、2,019,650 个三角形（含实例）、24 个导出材质（渲染器加默认材质后为 25）、8 盏内嵌灯光。
- `RainyCorner-preview.png`：Blender Cycles 渲染参考，**不是 DXR 截图**。
- `RainyCorner-dxr.png`：DXRDemo + NRD 的实际离屏输出。
- `build_scene.py`：可复现的建模脚本，分为建筑、室内、街道与导出阶段。MCP 调用按阶段执行这些代码。
- `refine_scene.py`：细化脚本，补充成型瓶身/瓶盖、罐口拉环、包装封边/条码、货架价签/网架、售货机操作面板、门窗密封条/铰链、自行车辐条/轮毂、空调格栅/散热片及街道紧固件；复用共享网格。

场景包含完整正方形底座、玻璃便利店、自动门、灯箱招牌、雨棚、两台售货机、自行车、伞架、垃圾桶、路灯、电线杆/线缆、路牌、护栏、停车线、斑马线、排水沟、积水、小巷、空调外机和公告栏。室内包括零食货架、满载饮料柜、便当/饭团陈列、收银台、咖啡机、关东煮柜台、杂志架、冰柜、导视和后场门。场景没有人物。

灯光包括 3 盏暖色店内点光源、2 盏售货机点光源、1 盏侧面霓虹点光源，以及门口和路灯的 2 盏聚光灯。灯箱、售货机、招牌等另有自发光材质。导出明确启用 `export_lights`，并用 Blender 的 `RAW` 灯光转换模式保留本场景为测试调定的 20～180 强度数值；不宣称 Blender 与 DXR 的曝光及灯光单位换算完全一致。

程序启动时默认选中此场景，启用「仅使用模型光照」并设置曝光 -2 EV；设置中的场景列表可随时切回。GLB 随构建和发布打包到 `Assets/Models/RainyCorner.glb`。关闭外部天空和太阳后，模型灯光、自发光及反射依然存在。实际预览使用原生 800×600、NRD、2 SPP、10 次反弹和曝光 -2 EV。低采样透射仍有平滑及残余噪点；Blender 参考图不代表实时 DXR 的画质。

玻璃使用 `KHR_materials_transmission`（透射率 1）、`KHR_materials_ior`（1.5）和 `KHR_materials_volume`；alpha 为 1，粗糙度 0.06。封闭玻璃几何提供实际折射路径厚度。导出器可能裁掉未连接的厚度设置组，因此脚本在导出的 GLB JSON 中保留明确的 volume 参数，不修改几何缓冲。金属和湿地反射使用 Metallic-Roughness。日本文字由本机游ゴシック字体生成，GLB 中已转换为几何轮廓。

## 复现

在 Blender MCP 中先检查场景，创建独立场景后依次执行 `build_scene.py` 的 `setup()`、`interior()`、`street()`、`finish()`，然后在该场景执行 `refine_scene.py` 的 `start()`、`products()`、`fixtures()`、`street()`、`export()`。细化脚本检查 `detail_revision`，避免意外重复追加。脚本不会删除原有场景；重新制作时使用干净的 Blender 文件以保留约定的材质名称。渲染参考图使用 `bpy.ops.render.render(write_still=True)`。

从 `diagnostics/integration` 执行：

```powershell
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --scene-preview ../../Samples/RainyCorner/RainyCorner.glb
./bin/Release/net10.0-windows10.0.22621.0/IntegrationProbe.exe --glass ../../Samples/RainyCorner/RainyCorner.glb
```

完整集成测试包括 glTF/GLB、灯光、透明度、纹理、NRD/SR、模型光照开关和内置兔子的阴影回归；D3D12 Error/Corruption 会使测试失败。
