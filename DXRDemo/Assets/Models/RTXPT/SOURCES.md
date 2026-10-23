# RTXPT assets

Source: https://github.com/NVIDIA-RTX/RTXPT-Assets

Pinned revision: `2a38b92df0fb43ebf6c6e6603893052dacf5a4c8`, the asset submodule
referenced by RTXPT `f08d1c739071e0faad0c7c274d861124c511abab`.

- **Kitchen**: original by Jay-Artist, https://blendswap.com/blend/5156,
  CC-BY; RTXPT modifications by Kate Anderson. The original attribution is
  preserved in `ArtLicenses.txt`.
- **ConvergenceTest**: NVIDIA RTXPT test model; the upstream SDK license is
  preserved in `RTXPT-LICENSE.txt`. Do not describe this asset as CC0.
- **Bistro / Le Petit Coin**: the street corner shown in the RTXPT README.
  Amazon Lumberyard, ORCA (2017), [CC BY 4.0](https://developer.nvidia.com/orca/amazon-lumberyard-bistro).
  Attribution and modification notice are preserved in `BISTRO-LICENSE.txt`.

DXRDemo modifications: decode the supplied DDS equivalents to PNG, limit
textures to 1024 pixels on the longest edge, repair malformed normals,
reconstruct invalid tangent frames from triangle UVs, and repair missing
extension declarations. Kitchen's optional `KHR_materials_specular` extension
references absent `Image*.png` files; this extension is removed and the
metallic-roughness fallback is retained. Geometry is not decimated.

These are the standalone Kitchen, ConvergenceTest and Bistro models. Kitchen's extra
plants, drone, liquid/ice props, animations and HDR environment from the larger
RTXPT scene graph are not included. DXRDemo supplies its procedural environment.
The original authored cameras are applied; the convergence scene also includes
its directional light, represented by the renderer's punctual-light model.

Bistro retains all 4,209,006 instanced triangles (street, cafe interior and vehicles),
uses the Outside camera from `bistro-programmer-art.scene.json`, and applies all
254 upstream `Materials/bistro.*.material.json` overrides. The CPU preparation
script bakes specular/glossiness into approximate metallic/roughness textures,
including linear-light color conversion and the original gloss alpha channel.
It preserves opacity/cutouts, normal scale, emission, transmission, IOR, volume
attenuation and nested priority. Normal-map Z is reconstructed as in RTXPT.
Diffuse transmission in foliage is not represented by the current material ABI;
the conversion is not a pixel-identical reproduction of RTXPT's BRDF. Additional
dragon/chess/robot test props and scene animations are not bundled.

Bistro uses **Shanghai Bund**, the same HDR panorama as the upstream scene's
BC6H cubemap. We package the unmodified 1K Radiance original by Greg Zaal from
[Poly Haven](https://polyhaven.com/a/shanghai_bund), licensed under CC0, so CPU
loading retains HDR radiance. It is not an 8-bit export of the BC6H texture.
Its download URL and SHA-256 are recorded in `manifest.json`; normal builds
never access the network.

`EnvironmentMaps/20060807_wells6_hd.hdr` is the unmodified RTXPT RGBE light probe
by Bernhard Vogl, originally from http://dativ.at/lightprobes/. Its upstream
attribution is preserved in `EnvironmentMaps/readme.txt`. The CC0 statement in
that readme applies to the **other** HDRIHaven maps; it is not asserted for Wells.
Kitchen continues to use procedural sky by default; this map is available for
HDR import and single-frame validation. Its input hash is in `manifest.json`.

`manifest.json` records the original input hashes and conversion operations.
Regenerate with `python tools/prepare_rtxpt_assets.py G:/SoftwareProject/Nvidia/RTXPT`
from the repository root. Runtime and deployment do not require that checkout.
Requires Pillow and NumPy. `--scene Bistro` rebuilds only Bistro. If the packaged
Shanghai Bund input is absent, preparation downloads and verifies that one HDR.

Optional `*-bc7srgb.dds` / `*-bc7linear.dds` GPU sidecars preserve the packaged
texture dimensions and full mip chains. PNG remains the portable glTF fallback
and the CPU light-distribution source. Bistro has 515 compatible BC7 views;
Kitchen has 102. Non-block-aligned images retain RGBA8. Convergence has no
compatible texture images to compress. Each scene's `bc7-manifest.json` records
source/output hashes and transfer functions. BC7 is lossy; measured Bistro
top-mip RGB PSNR is 40.34 dB (8-bit RGB RMSE 2.45 across the compressed views).

Regenerate using `python tools/prepare_texture_bc7.py <scene.gltf> --texconv <texconv.exe>`.
The script passes `-nogpu` and uses BC7 mode 6, BOX mip filtering and separate alpha.
Tool: [Microsoft DirectXTex, May 2026](https://github.com/microsoft/DirectXTex/releases/tag/may2026),
`texconv.exe` SHA-256 `dcfdec10244e02cf5037fba089c55fb7e1326b1c8181742d77d15fa5cb5eef06`.
The tool binary is not redistributed with the application.
