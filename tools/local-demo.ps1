<#
.SYNOPSIS
Starts an isolated ChatBI experience on a loopback port (default 4174) for up to four hours.
Uses a local test identity, synthetic PostgreSQL data and three fixed query plans.
No real model is called. Requires the built solution, portal and ChatBI artifacts,
the locked ChatBI Python environment and tools/install-native-postgres.ps1.
Run with -Stop from another terminal to stop and remove temporary data.
Use -Port 4180 when the default port belongs to another local service.
#>
param(
    [switch]$Stop,
    [string]$Node = $env:ASSEMBLEAI_NODE,
    [ValidateRange(1024, 65535)]
    [int]$Port = 4174
)
$ErrorActionPreference = 'Stop'
$demoRepo = Split-Path -Parent $PSScriptRoot
$demoRuns = Join-Path $demoRepo '.local-data/postgres-tests'
if ($Stop) {
    $demoFound = $false
    if (Test-Path -LiteralPath $demoRuns) {
        foreach ($demoDirectory in Get-ChildItem -LiteralPath $demoRuns -Directory -Filter 'run-*') {
            if (Test-Path -LiteralPath (Join-Path $demoDirectory.FullName 'demo-ready.json')) {
                [System.IO.File]::WriteAllText((Join-Path $demoDirectory.FullName 'demo-stop'), 'stop')
                $demoFound = $true
            }
        }
    }
    if ($demoFound) { Write-Output 'Stop requested. The local demo will remove its temporary data.' }
    else { Write-Output 'No ready local demo found.' }
    exit 0
}
if (-not $Node) { $Node = (Get-Command node.exe -ErrorAction Stop).Source }
if (-not (Test-Path -LiteralPath $Node -PathType Leaf)) { throw 'Node executable was not found. Supply -Node with its full path.' }
$demoPython = Join-Path $demoRepo 'apps/chatbi/backend/.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $demoPython -PathType Leaf)) { throw 'Install the locked ChatBI backend environment first.' }
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    $demoDotnet = Join-Path $env:ProgramFiles 'dotnet'
    if (-not (Test-Path -LiteralPath (Join-Path $demoDotnet 'dotnet.exe'))) { throw '.NET SDK is required.' }
    $env:PATH = "$demoDotnet;$env:PATH"
}
$env:ASSEMBLEAI_NODE = (Resolve-Path -LiteralPath $Node).Path
$env:ASSEMBLEAI_PYTHON = $demoPython
$env:ASSEMBLEAI_DEMO_PORT = $Port.ToString([System.Globalization.CultureInfo]::InvariantCulture)
& $demoPython (Join-Path $PSScriptRoot 'verify-postgres.py') --interactive-demo
exit $LASTEXITCODE
