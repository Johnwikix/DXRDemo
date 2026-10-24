$ErrorActionPreference = 'Stop'
$revision = 'f1f6dd803f3159992d248178f6e09421c6eb8b6d' # NVIDIA OptiX 9.1.0
$include = Join-Path $PSScriptRoot 'include'
$hashes = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'headers.sha256.json') | ConvertFrom-Json -AsHashtable
New-Item -ItemType Directory -Force -Path $include | Out-Null
foreach ($file in @('optix.h','optix_host.h','optix_types.h','optix_function_table.h','optix_function_table_definition.h','optix_stubs.h')) {
    $target = Join-Path $include $file
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$file]) {
        $download = $target + '.download'
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/NVIDIA/optix-dev/$revision/include/$file" -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $hashes[$file]) { throw "OptiX header hash mismatch: $file" }
        Move-Item -LiteralPath $download -Destination $target -Force
    }
}
