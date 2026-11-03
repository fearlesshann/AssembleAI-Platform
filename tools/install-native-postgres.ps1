<#
.SYNOPSIS
Prepares the pinned PostgreSQL Windows ZIP inside the workspace.
.DESCRIPTION
Checks archive length and the publisher's SHA256 before extracting. Does not
initialize a database, register a service, change global PATH or create users.
Reuses only an installation carrying this script's matching package marker.
.EXAMPLE
powershell -NoProfile -File tools/install-native-postgres.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This lock describes Windows x64 packages only.'
}
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installationRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot '.local-data/native-postgres'))
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-postgres.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.platform -ne 'windows-x64') { throw 'Unsupported native PostgreSQL lock.' }
$package = $lock.package
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-WithinInstallation {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = $installationRoot.TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a path outside the native PostgreSQL installation directory.'
    }
    $ancestor = $absolute
    while ($ancestor -and $ancestor.Length -ge $workspaceRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing a native PostgreSQL path containing a reparse point.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $absolute
}

function Assert-Archive {
    param([string]$Path)
    if ((Get-Item -LiteralPath $Path).Length -ne $package.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $package.sha256) {
        throw 'PostgreSQL archive verification failed. Existing files were not overwritten.'
    }
}

function Install-Postgres {
    if ($package.sha256 -notmatch '^[a-f0-9]{64}$' -or $package.version -notmatch '^\d+\.\d+$' -or
        $package.archiveName -notmatch '^postgresql-[A-Za-z0-9_.-]+-windows-x64-binaries\.zip$' -or
        $package.relativeHome -ne 'postgresql/pgsql') {
        throw 'Invalid package metadata in native PostgreSQL lock.'
    }
    $uri = [Uri]$package.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'get.enterprisedb.com' -or $uri.UserInfo -or
        $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne ('/postgresql/' + $package.archiveName) -or
        $package.checksumSource -ne ($package.url + '.sha256') -or
        $package.checksumKind -ne 'publisher-sha256-over-https') {
        throw 'The package lock must identify the EDB HTTPS release and its publisher checksum.'
    }
    $packageParent = Assert-WithinInstallation (Join-Path $installationRoot 'postgresql')
    $packageHome = Assert-WithinInstallation (Join-Path $installationRoot $package.relativeHome)
    $archive = Assert-WithinInstallation (Join-Path $installationRoot ('downloads/' + $package.archiveName))
    $marker = Join-Path $packageParent '.assembleai-native-package.json'
    if (Test-Path -LiteralPath $packageParent) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw 'Refusing to overwrite an unrecognized PostgreSQL installation directory.'
        }
        $installed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($installed.sha256 -ne $package.sha256 -or $installed.relativeHome -ne $package.relativeHome -or
            -not (Test-Path -LiteralPath $packageHome -PathType Container)) {
            throw 'The existing PostgreSQL installation does not match the lock; it was not modified.'
        }
        Assert-Archive $archive
        Write-Host "[PASS] Reusing PostgreSQL $($package.version); publisher SHA256 verified."
        return $packageHome
    }
    [void][IO.Directory]::CreateDirectory((Assert-WithinInstallation (Join-Path $installationRoot 'downloads')))
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $temporaryArchive = Assert-WithinInstallation ($archive + '.' + [Guid]::NewGuid().ToString('N') + '.download')
        try {
            Write-Host "Downloading PostgreSQL $($package.version) ($($package.size) bytes)."
            Invoke-WebRequest -Uri $package.url -OutFile $temporaryArchive -UseBasicParsing -TimeoutSec 900
            Assert-Archive $temporaryArchive
            [void](Assert-WithinInstallation $temporaryArchive)
            [void](Assert-WithinInstallation $archive)
            Move-Item -LiteralPath $temporaryArchive -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath (Assert-WithinInstallation $temporaryArchive) }
        }
    }
    Assert-Archive $archive
    Write-Host '[PASS] PostgreSQL archive matches publisher SHA256.'
    $staging = Assert-WithinInstallation (Join-Path $installationRoot ('postgresql.extract-' + [Guid]::NewGuid().ToString('N')))
    [void][IO.Directory]::CreateDirectory($staging)
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $stagingPrefix = $staging.TrimEnd('\') + '\'
            foreach ($entry in $zip.Entries) {
                $target = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
                if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':') -or
                    -not $target.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Archive contains a path outside the extraction directory.'
                }
            }
        } finally { $zip.Dispose() }
        Expand-Archive -LiteralPath $archive -DestinationPath $staging
        if (-not (Test-Path -LiteralPath (Join-Path $staging 'pgsql/bin/postgres.exe') -PathType Leaf)) {
            throw 'Archive layout does not match the locked PostgreSQL home.'
        }
        [IO.File]::WriteAllText((Join-Path $staging '.assembleai-native-package.json'),
            ($package | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        [void](Assert-WithinInstallation $staging)
        [void](Assert-WithinInstallation $packageParent)
        if (Test-Path -LiteralPath $packageParent) { throw 'Installation target appeared while extracting.' }
        Move-Item -LiteralPath $staging -Destination $packageParent
    } finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath (Assert-WithinInstallation $staging) -Recurse -Force
        }
    }
    return $packageHome
}

function Get-VersionOutput {
    param([string]$Executable, [string]$PostgresHome)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = '--version'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['PATH'] = (Join-Path $PostgresHome 'bin') + ';' + $env:SystemRoot + '\System32;' + $env:SystemRoot
    foreach ($variable in @($start.EnvironmentVariables.Keys)) {
        if ($variable -like 'PG*') { $start.EnvironmentVariables.Remove($variable) }
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            if (-not $process.HasExited) {
                $stopInfo = [Diagnostics.ProcessStartInfo]::new()
                $stopInfo.FileName = Join-Path $env:SystemRoot 'System32/taskkill.exe'
                $stopInfo.Arguments = '/PID ' + $process.Id + ' /T /F'
                $stopInfo.UseShellExecute = $false
                $stopInfo.CreateNoWindow = $true
                $stopInfo.RedirectStandardOutput = $true
                $stopInfo.RedirectStandardError = $true
                $stopper = [Diagnostics.Process]::Start($stopInfo)
                try {
                    $stopOutput = $stopper.StandardOutput.ReadToEndAsync()
                    $stopError = $stopper.StandardError.ReadToEndAsync()
                    if (-not $stopper.WaitForExit(10000)) { throw 'PostgreSQL version-probe cleanup timed out.' }
                    [void]$stopOutput.GetAwaiter().GetResult()
                    [void]$stopError.GetAwaiter().GetResult()
                    if (-not $process.WaitForExit(10000)) { throw 'PostgreSQL version probe did not exit after cleanup.' }
                } finally { $stopper.Dispose() }
            }
            throw 'PostgreSQL version check timed out.'
        }
        $text = ($output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()).Trim()
        if ($process.ExitCode -ne 0) { throw 'PostgreSQL version check failed.' }
        return $text
    } finally { $process.Dispose() }
}

$postgresHome = Install-Postgres
foreach ($name in @('initdb.exe', 'pg_ctl.exe', 'psql.exe', 'postgres.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $postgresHome ('bin/' + $name)) -PathType Leaf)) {
        throw "Required PostgreSQL binary is missing: $name"
    }
}
$postgresVersion = Get-VersionOutput (Join-Path $postgresHome 'bin/postgres.exe') $postgresHome
$psqlVersion = Get-VersionOutput (Join-Path $postgresHome 'bin/psql.exe') $postgresHome
foreach ($versionText in @($postgresVersion, $psqlVersion)) {
    if ($versionText -notmatch ('\(PostgreSQL\)\s+' + [regex]::Escape($package.version) + '(?:\s|$)')) {
        throw 'Installed PostgreSQL version output does not match the package lock.'
    }
    Write-Host "[PASS] $versionText"
}
$result = [ordered]@{ postgresHome = $postgresHome; version = $package.version }
[IO.File]::WriteAllText((Assert-WithinInstallation (Join-Path $installationRoot 'installed.json')),
    ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[pscustomobject]$result
