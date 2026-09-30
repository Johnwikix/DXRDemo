# 玻璃、光线重建与便利店细化验证

2026-09-29，本机 Intel Arc 140T，Windows x64 Release。

以下为历史验证记录。2026-09-30 已改为不透明表面 NRD SH 降噪、SG
resolve/re-jittering 和降噪后玻璃合成，并移除自实现 RELAX；当前玻璃细节、
抖动与透射贴图回归结果见 [NRD 验证](NRD.md#current-glass-and-pipeline-verification-on-intel-arc-140t-2026-09-30)。

## 交付内容

- 默认便利店 GLB：2,019,650 个三角形（含实例）、233 个共享网格、3,199 个网格实例、8 盏灯。
- Blender MCP 细化商品包装、瓶罐、货架与五金；`.blend` 已保存，建模/细化脚本语法检查通过。
- 玻璃由 alpha 覆盖率改为 transmission/IOR/volume，加入折射、全反射、吸收和稳定降噪引导。
- 实际 NGX DLSSD feature 创建/评价入口、引导缓冲、设置项、失败回退和固定版本运行库均已接入。

## 已执行

| 检查 | 结果 |
|---|---|
| 原生桥接与 SDK SHA-256 | 通过 |
| WinUI x64 Release 构建 | 0 错误；保留既有 WinUIEx `Icon` 弃用警告 |
| 诊断程序构建 | 0 错误、0 警告 |
| `--scene` | 导入、默认场景、相机、模型光照、PBR、NRD/SR 与阴影回归通过 |
| `--glass` | 2,888 个玻璃像素；相对 512 SPP 参考，2 SPP RMSE 0.351406 → NRD 0.131150 |
| `--sr` | FSR/XeSS 的 80 组比例/降噪/SDR-HDR 组合及重投影、尺寸切换通过 |
| 默认集成入口 | 24 组硬件/软件渲染对照、尺寸变化和 GPU HUD 通过 |
| D3D12 调试层 | 上述启用调试层的场景、玻璃与 SR 检查无 Error/Corruption |
| `--scene-preview` | 800×600、2 SPP、10 次反弹、NRD、仅模型光照；实际图保存至样例目录 |
| 打包资产 | 输出 GLB、DLSSD DLL、桥接 DLL 与源资产哈希一致 |

玻璃误差在每通道使用 `x/(1+x)` 压缩后计算。该指标说明低采样噪声降低，不代表窗后细节和多层透射运动已经完美；当前 NRD 仍可能平滑玻璃后的细小结构，直射玻璃阴影也未实现折射焦散。

## 按用户要求保留的验证边界

**未创建或执行 DLSSD feature 进行运行验证。** 用户将在支持的 RTX 设备上验证兼容性、重建质量、运动和透明材质；本机 Intel 测试不构成 DLSSD 验证。构建与静态接线检查已完成。

本地测试日志位于忽略的 `diagnostics/integration/output/final-{sr,parity,scene,glass}.log`。复现命令见 [场景渲染器](SCENE_RENDERER.md) 与 [超分/光线重建](SUPER_RESOLUTION.md)。
