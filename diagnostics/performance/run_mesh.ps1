param([switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    python fetch_models.py
    if ($LASTEXITCODE -ne 0) { throw 'Model download failed.' }
    python prepare_meshes.py
    if ($LASTEXITCODE -ne 0) { throw 'Mesh/BVH preparation failed.' }
    if ($PrepareOnly) { return }
    dotnet build -c Release --configfile NuGet.Config -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    $probe = 'bin/Release/net10.0-windows10.0.22621.0/PerfProbe.dll'
    foreach ($model in @('dragon_vrip_res4','dragon_vrip_res3','bun_zipper','dragon_vrip_res2','Armadillo','dragon_vrip')) {
        dotnet $probe mesh $model 1280 720 4 10
        if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $model" }
    }
    python analyze_mesh_results.py
    if ($LASTEXITCODE -ne 0) { throw 'Output comparison failed.' }
} finally { Pop-Location }
