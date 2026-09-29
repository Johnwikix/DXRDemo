# DXRDemo reconstruction SDKs

These pinned SDK files and the C++ adapter were ported from the local Spectrum
project. Spectrum itself is unchanged. Only super resolution is enabled; there
is no frame generation or DLSS Ray Reconstruction implementation in this adapter.

- XeSS: `intel/xess`, commit `de0fb9c1c510661c571164e1418ceca8101dab69`.
- FSR: `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK`, commit `60f4ea81909200d8542eca14dccb2628b763a9a3`.
  The adapter selects a 3.1 provider, never FSR 4 or frame generation.
- DLSS: `NVIDIA/DLSS`, commit `374959484e79a640feaba44c93ac8cfb0a03f5b5`.
  The bridge links the static x64 NGX library and uses the release `nvngx_dlss.dll`.
  All quality slots explicitly request preset K. Driver overrides can still change model selection.

Verify and build from the repository root:

```powershell
./External/Upscalers/Restore.ps1 -VerifyOnly
./Native/Reconstruction/Build.ps1
```

Requires PowerShell 7, Visual Studio C++ x64 tools (v145), and a Windows SDK.
`Restore.ps1` restores missing files from those official commits and checks every
SHA-256. Existing mismatches fail. Normal builds verify files but never download
SDKs automatically. Git preserves their original bytes to keep hashes stable.

`DXRDemo/SuperResolution/ReconstructionAssets.targets` builds
`DXRDemo.Reconstruction.dll` and includes the bridge, SDK DLLs and licenses in
x64 build/publish/MSIX content. Non-x64 builds omit these optional components.
`SkipReconstructionNativeBuild=true` reuses an existing bridge;
`EnableVendorReconstruction=false` builds without bundled SR components. Use a
fresh output directory when disabling them, since stale DLLs remain discoverable.
The vendor binaries occupy about 160 MiB.

Run the application/probe **EXE**, not `dotnet application.dll`: the FidelityFX
loader discovers providers relative to the native process executable. A
`dotnet.exe` host can report `FFX_API_RETURN_NO_PROVIDER` even when the provider
DLL is beside the managed assembly. No process-wide DLL search path is changed.

The adapter borrows the D3D12 device, command list and textures. The host waits
for submitted work before replacing contexts/resources and before ComputeSharp
consumes output. Input dimensions are exact fixed sizes; SDK optimal/min/max
queries choose initialization hints, not an application-imposed input cutoff.
Initialization/dispatch failure is reported and falls back to native resolution.

Each SDK retains its original license. `NOTICE.txt` and licenses are copied to
`Licenses/` in output. The project's own licensing does not relicense these SDKs.
NVIDIA distribution/attribution terms remain in `dlss/LICENSE.txt`; this change
creates local build artifacts and performs no public upload or release.
