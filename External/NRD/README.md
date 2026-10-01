# NVIDIA Real-Time Denoisers SDK

Pinned upstream: NVIDIA-RTX/NRD **v4.17.3**, commit
`792eff196afdd350fd9c3f862119017ccb438a0e` (30 April 2026).
`Runtime/NRD.dll` is built from that unmodified source with all official DXIL
denoisers embedded. The application instantiates **RELAX_DIFFUSE**, which matches
its diffuse-only Stanford mesh path tracer. The C++ D3D12 adapter in
`Native/Reconstruction/Nrd.cpp` executes the SDK's complete dispatch list; it
does not substitute custom filtering passes for NRD shaders.

Build configuration: Release x64, C++ v145, static CRT, DXIL on, DXBC/SPIR-V/NRI
off, `NRD_NORMAL_ENCODING=4` (signed normal, floating point accepted),
`NRD_ROUGHNESS_ENCODING=1` (linear). SDK optional features retain their defaults.
Shader compilation uses the project's Microsoft.Direct3D.DXC 1.9.2609.5 package.
Normal frame accumulation uses the official RELAX defaults with anti-firefly on.
Checkerboarding and hit-distance reconstruction are off because every primary
hit has a sampled diffuse path. This is not DLSS Ray Reconstruction.

`Verify.ps1` validates byte hashes of the vendored runtime, matching headers,
shader packing helpers and licenses. Normal app builds verify them and build the
small native adapter; they do not rebuild or download NRD.

To rebuild the SDK explicitly, run `External/NRD/Rebuild.ps1` (PowerShell 7,
Visual Studio 2026 C++/CMake and Windows SDK required). `-DxcPath` can override
the compiler location. The script verifies fixed source-archive hashes for NRD,
ShaderMake commit `18f5a344e7ca8fa65daaf079d07bc8ce38453e05` and MathLib v11,
then configures CMake with those local dependencies. Archives and build products
stay in ignored `cache/`. It copies the finished SDK files and intentionally
refreshes their checksums; compiler/build paths can change the DLL's byte hash.

The app dynamically loads `NRD.dll` beside its native bridge. Missing or
incompatible NRD reports a legacy-denoiser fallback without disabling FSR/XeSS/
DLSS. The SDK runtime is approximately 3.4 MiB. Build/publish/MSIX content includes
the SDK and all three license files. The upstream NRD, MathLib and ShaderMake
licenses are preserved; the application license does not relicense them.

See [NRD integration and verification](../../docs/NRD.md).
