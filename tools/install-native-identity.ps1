<#
.SYNOPSIS
Prepares pinned JDK and Keycloak ZIP distributions inside the workspace.
.DESCRIPTION
Checks the archive length and SHA256 before extracting. No global environment,
Windows service, administrator installation or Keycloak server is created.
Reuses only completed installations carrying this script's matching package marker.
.EXAMPLE
powershell -NoProfile -File tools/install-native-identity.ps1
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
$installationRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot '.local-data/native-identity'))
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-identity.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.platform -ne 'windows-x64') { throw 'Unsupported native identity lock.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-WithinInstallation {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = $installationRoot.TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a path outside the native identity installation directory.'
    }
    # Existing ancestors must not redirect file operations through a junction/symlink.
    $ancestor = $absolute
    while ($ancestor -and $ancestor.Length -ge $workspaceRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing a native identity path containing a reparse point.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $absolute
}

function Assert-Archive {
    param([string]$Path, [object]$Package)
    if ((Get-Item -LiteralPath $Path).Length -ne $Package.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Package.sha256) {
        throw "Archive verification failed: $($Package.archiveName). Existing files were not overwritten."
    }
}

function Install-Package {
    param([string]$Name, [object]$Package)
    if ($Name -notin @('jdk', 'keycloak') -or $Package.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $Package.archiveName -notmatch '^[A-Za-z0-9_.+-]+\.zip$' -or
        $Package.relativeHome -notmatch ('^' + $Name + '/[A-Za-z0-9_.+-]+$')) {
        throw 'Invalid package metadata in native identity lock.'
    }
    $uri = [Uri]$Package.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'The package lock must use an official GitHub HTTPS release URL.'
    }
    $packageParent = Assert-WithinInstallation (Join-Path $installationRoot $Name)
    $packageHome = Assert-WithinInstallation (Join-Path $installationRoot $Package.relativeHome)
    $archive = Assert-WithinInstallation (Join-Path $installationRoot ('downloads/' + $Package.archiveName))
    $marker = Join-Path $packageParent '.assembleai-native-package.json'
    if (Test-Path -LiteralPath $packageParent) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw "Refusing to overwrite an unrecognized $Name directory: $packageParent"
        }
        $installed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($installed.sha256 -ne $Package.sha256 -or $installed.relativeHome -ne $Package.relativeHome -or
            -not (Test-Path -LiteralPath $packageHome -PathType Container)) {
            throw "The existing $Name installation does not match the lock; it was not modified."
        }
        Assert-Archive $archive $Package
        Write-Host "[PASS] Reusing $Name $($Package.version); archive SHA256 verified."
        return $packageHome
    }
    [void][IO.Directory]::CreateDirectory((Assert-WithinInstallation (Join-Path $installationRoot 'downloads')))
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $temporaryArchive = Assert-WithinInstallation ($archive + '.' + [Guid]::NewGuid().ToString('N') + '.download')
        try {
            Write-Host "Downloading $Name $($Package.version) ($($Package.size) bytes)."
            # Use PowerShell's Windows proxy handling; native curl may not use the
            # same configured proxy. No content is trusted until its hash matches.
            Invoke-WebRequest -Uri $Package.url -OutFile $temporaryArchive -UseBasicParsing -TimeoutSec 900
            Assert-Archive $temporaryArchive $Package
            [void](Assert-WithinInstallation $temporaryArchive)
            [void](Assert-WithinInstallation $archive)
            Move-Item -LiteralPath $temporaryArchive -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath (Assert-WithinInstallation $temporaryArchive) }
        }
    }
    Assert-Archive $archive $Package
    Write-Host "[PASS] $Name archive SHA256 verified."
    $staging = Assert-WithinInstallation (Join-Path $installationRoot ($Name + '.extract-' + [Guid]::NewGuid().ToString('N')))
    [void][IO.Directory]::CreateDirectory($staging)
    try {
        # Check every archive entry before extraction, including rooted paths and traversal.
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $stagingPrefix = $staging.TrimEnd('\') + '\'
            foreach ($entry in $zip.Entries) {
                $target = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
                if ([IO.Path]::IsPathRooted($entry.FullName) -or
                    -not $target.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Archive contains a path outside the extraction directory.'
                }
            }
        } finally { $zip.Dispose() }
        Expand-Archive -LiteralPath $archive -DestinationPath $staging
        $topDirectory = Split-Path -Leaf $Package.relativeHome
        if (-not (Test-Path -LiteralPath (Join-Path $staging $topDirectory) -PathType Container)) {
            throw "Archive layout does not match the locked $Name home."
        }
        [IO.File]::WriteAllText((Join-Path $staging '.assembleai-native-package.json'),
            ($Package | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        # Both resolved targets are confined before the directory move. Never replace a prior installation.
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
    param([string]$Executable, [string]$Arguments, [string]$JavaHome)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['JAVA_HOME'] = $JavaHome
    $start.EnvironmentVariables['JAVA'] = Join-Path $JavaHome 'bin/java.exe'
    $start.EnvironmentVariables['KC_DEBUG'] = 'false'
    $start.EnvironmentVariables['PATH'] = (Join-Path $JavaHome 'bin') + ';' + $env:SystemRoot + '\System32;' + $env:SystemRoot
    foreach ($variable in @('JAVA_OPTS', 'JAVA_OPTS_APPEND', 'JAVA_TOOL_OPTIONS', 'JDK_JAVA_OPTIONS',
        '_JAVA_OPTIONS', 'DEBUG', 'DEBUG_PORT', 'DEBUG_SUSPEND', 'JAVA_ADD_OPENS', 'JAVA_LOCALE', 'PRINT_ENV')) {
        $start.EnvironmentVariables.Remove($variable)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            # kc.bat launches Java below cmd.exe. Kill only this still-running
            # version probe's process tree; killing cmd alone would leave Java.
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
                    if (-not $stopper.WaitForExit(10000)) { throw 'Version-probe process cleanup timed out.' }
                    [void]$stopOutput.GetAwaiter().GetResult()
                    [void]$stopError.GetAwaiter().GetResult()
                    if (-not $process.WaitForExit(10000)) { throw 'Version-probe process did not exit after cleanup.' }
                } finally { $stopper.Dispose() }
            }
            throw 'Native dependency version check timed out.'
        }
        $text = $output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Native dependency version check failed: $text" }
        return $text.Trim()
    } finally { $process.Dispose() }
}

$javaHome = Install-Package 'jdk' $lock.packages.jdk
$keycloakHome = Install-Package 'keycloak' $lock.packages.keycloak
$javaVersion = Get-VersionOutput (Join-Path $javaHome 'bin/java.exe') '-version' $javaHome
$keycloakBatch = Join-Path $keycloakHome 'bin/kc.bat'
$keycloakVersion = Get-VersionOutput (Join-Path $env:SystemRoot 'System32/cmd.exe') ('/d /s /c ""' + $keycloakBatch + '" --version"') $javaHome
if ($javaVersion -notmatch [regex]::Escape($lock.packages.jdk.version) -or
    $keycloakVersion -notmatch ('Keycloak\s+' + [regex]::Escape($lock.packages.keycloak.version))) {
    throw 'Installed version output does not match the package lock.'
}
Write-Host "[PASS] $javaVersion"
Write-Host "[PASS] $keycloakVersion"
$result = [ordered]@{
    javaHome = $javaHome
    keycloakHome = $keycloakHome
    javaVersion = $lock.packages.jdk.version
    keycloakVersion = $lock.packages.keycloak.version
}
[IO.File]::WriteAllText((Assert-WithinInstallation (Join-Path $installationRoot 'installed.json')),
    ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[pscustomobject]$result
