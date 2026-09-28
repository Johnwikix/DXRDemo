param([uint32]$Width = 1280, [uint32]$Height = 720)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build -c Release --configfile NuGet.Config -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    python generate_variants.py
    if ($LASTEXITCODE -ne 0) { throw 'Shader generation failed.' }
    if (-not $env:DXR_DXC_PATH) {
        $env:DXR_DXC_PATH = (Resolve-Path '../../DXRDemo/bin/x64/Release/net10.0-windows10.0.22621.0/dxc.exe').Path
    }
    $probe = 'bin/Release/net10.0-windows10.0.22621.0/PerfProbe.dll'
    $env:PERF_DUMP = '1'
    foreach ($variant in @('baseline', 'samples2', 'analytic_raygen', 'analytic.cs', 'inline_query.cs', 'offset_direction')) {
        dotnet $probe "variants/$variant.hlsl" $Width $Height
        if ($LASTEXITCODE -ne 0) { throw "Probe failed: $variant" }
    }
    foreach ($samples in @(4,2)) {
        dotnet $probe compute $Width $Height $samples
        if ($LASTEXITCODE -ne 0) { throw 'ComputeSharp probe failed.' }
    }
    python validate_results.py
    if ($LASTEXITCODE -ne 0) { throw 'Readback validation failed.' }
} finally { Pop-Location }
