[CmdletBinding()]
param([string] $Destination = (Join-Path $PSScriptRoot '../.local-data/knowledge-model/e5-small-dd476dd0-384-v1'))
$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'knowledge-model.lock.json') -Raw | ConvertFrom-Json
if (-not [IO.Path]::IsPathFullyQualified($Destination)) { throw 'Model destination must be absolute.' }
$directory = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
foreach ($asset in $lock.files) {
    if ($asset.name -notmatch '^[a-zA-Z0-9_.-]+$') { throw 'Invalid locked model filename.' }
    $target = Join-Path $directory $asset.name
    if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $asset.bytes -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $asset.sha256) {
        Write-Host "Verified $($asset.name)"
        continue
    }
    $partial = "$target.download"
    $url = "https://huggingface.co/$($lock.repository)/resolve/$($lock.revision)/onnx/$($asset.name)"
    try {
        Invoke-WebRequest -Uri $url -OutFile $partial -TimeoutSec 600
        if ((Get-Item -LiteralPath $partial).Length -ne $asset.bytes -or
            (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw 'Downloaded model does not match the pinned size and SHA256.'
        }
        Move-Item -LiteralPath $partial -Destination $target -Force
        Write-Host "Installed $($asset.name)"
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}
Write-Host "Verified profile $($lock.profile) at $directory"
