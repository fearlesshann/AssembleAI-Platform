<#
.SYNOPSIS
Downloads verified portable knowledge dependencies and builds the pinned MinIO.
.DESCRIPTION
Writes dependencies only to .local-data/native-knowledge. An explicit marked
BuildCacheRoot can hold compiler files and caches on another drive. No services,
global environment changes, or Visual Studio installation actions.
#>
[CmdletBinding()]
param([switch]$SkipMinioBuild, [string]$BuildCacheRoot, [switch]$IncludeVector)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Native knowledge packages require Windows x64.'
}
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installationRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot '.local-data/native-knowledge'))
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-knowledge.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.platform -ne 'windows-x64') { throw 'Invalid native knowledge lock.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Local-Path([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($installationRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a path outside the native knowledge root.'
    }
    for ($ancestor = $absolute; $ancestor -and $ancestor.Length -ge $workspaceRoot.Length; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Native knowledge paths cannot contain reparse points.'
            }
        }
    }
    return $absolute
}

function Build-Path([string]$Name) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $buildRoot $Name))
    if (-not $absolute.StartsWith($buildRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build cache path escapes its marked directory.'
    }
    for ($ancestor = $absolute; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Build cache paths cannot contain reparse points.'
            }
        }
    }
    return $absolute
}

function Assert-Archive([string]$Path, $Package) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Package.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Package.sha256) {
        throw 'Archive verification failed; existing files were not overwritten.'
    }
}

function Install-Package([string]$Name, $Package) {
    $expectedHosts = @{ go = 'go.dev'; erlang = 'github.com'; rabbitmq = 'github.com' }
    $uri = [Uri]$Package.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne $expectedHosts[$Name] -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $Package.sha256 -notmatch '^[a-f0-9]{64}$' -or $Package.archiveName -notmatch '^[a-zA-Z0-9_.-]+\.zip$' -or $Package.size -le 0) {
        throw 'Invalid package metadata.'
    }
    $parent = Local-Path (Join-Path $installationRoot $Name)
    $packageHome = Local-Path (Join-Path $installationRoot $Package.relativeHome)
    $archive = Local-Path (Join-Path $installationRoot ('downloads/' + $Package.archiveName))
    $marker = Local-Path (Join-Path $parent '.assembleai-native-package.json')
    if (Test-Path -LiteralPath $parent) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "Unrecognized $Name directory; refusing to overwrite it." }
        $installed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($installed.sha256 -ne $Package.sha256 -or $installed.relativeHome -ne $Package.relativeHome -or -not (Test-Path -LiteralPath $packageHome)) {
            throw "Existing $Name installation does not match its lock."
        }
        Assert-Archive $archive $Package
        Write-Host "[PASS] Reusing verified $Name $($Package.version)."
        return $packageHome
    }
    [void][IO.Directory]::CreateDirectory((Local-Path (Join-Path $installationRoot 'downloads')))
    if (-not (Test-Path -LiteralPath $archive)) {
        $partial = Local-Path ($archive + '.' + [Guid]::NewGuid().ToString('N') + '.download')
        try {
            Write-Host "Downloading $Name $($Package.version) ($($Package.size) bytes)."
            Invoke-WebRequest -Uri $Package.url -OutFile $partial -TimeoutSec 900 -UseBasicParsing
            Assert-Archive $partial $Package
            Move-Item -LiteralPath $partial -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath (Local-Path $partial) -Force }
        }
    }
    Assert-Archive $archive $Package
    $staging = Local-Path (Join-Path $installationRoot ($Name + '.extract-' + [Guid]::NewGuid().ToString('N')))
    [void][IO.Directory]::CreateDirectory($staging)
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $zip.Entries) {
                $target = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
                if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':') -or
                    -not $target.StartsWith($staging.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Archive entry escapes its staging directory.'
                }
            }
        } finally { $zip.Dispose() }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
        [IO.File]::WriteAllText((Join-Path $staging '.assembleai-native-package.json'), ($Package | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $parent) { throw 'Installation target appeared while extracting.' }
        Move-Item -LiteralPath (Local-Path $staging) -Destination (Local-Path $parent)
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Local-Path $staging) -Recurse -Force }
    }
    if (-not (Test-Path -LiteralPath $packageHome)) { throw "Unexpected $Name archive layout." }
    return $packageHome
}

function Run-Local([string]$Executable, [string[]]$Arguments, [hashtable]$Environment, [string]$WorkingDirectory, [int]$TimeoutSeconds = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    foreach ($name in @('SystemRoot','WINDIR','COMSPEC','PATHEXT','PATH','USERPROFILE','HOMEDRIVE','HOMEPATH','LOCALAPPDATA','APPDATA','PROGRAMDATA','ProgramFiles','ProgramFiles(x86)','ProgramW6432','HTTP_PROXY','HTTPS_PROXY','NO_PROXY')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($value) { $start.Environment[$name] = $value }
    }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(15000)
            throw 'Dependency command timed out.'
        }
        $result = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        foreach ($name in @('HTTP_PROXY','HTTPS_PROXY')) {
            $proxyValue = $start.Environment[$name]
            if ($proxyValue) { $result = $result.Replace($proxyValue, '[proxy]') }
        }
        if ($process.ExitCode -ne 0) { throw ('Dependency command failed: ' + $result) }
        return $result.Trim()
    } finally { $process.Dispose() }
}

function Get-BuildPayload($Payload) {
    $uri = [Uri]$Payload.url
    $name = [IO.Path]::GetFileName($Payload.fileName)
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'download.visualstudio.microsoft.com' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $Payload.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $name -notmatch '^[a-zA-Z0-9 ._-]+\.(vsix|msi|cab)$') { throw 'Invalid Microsoft build payload.' }
    $destination = Build-Path ('toolchain/downloads/' + $name)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    if (-not (Test-Path -LiteralPath $destination)) {
        $partial = Build-Path ('toolchain/downloads/' + $name + '.download')
        try {
            Invoke-WebRequest -Uri $Payload.url -OutFile $partial -TimeoutSec 600 -UseBasicParsing
            Assert-Archive $partial $Payload
            Move-Item -LiteralPath $partial -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath (Build-Path ('toolchain/downloads/' + $name + '.download')) -Force }
        }
    }
    Assert-Archive $destination $Payload
    return $destination
}

function Read-MsiRows($Database, [string]$Sql, [int]$Columns) {
    $view = $Database.OpenView($Sql)
    try {
        [void]$view.Execute()
        while ($record = $view.Fetch()) {
            $values = @()
            for ($i = 1; $i -le $Columns; $i++) { $values += $record.StringData($i) }
            Write-Output -NoEnumerate $values
        }
    } finally { [void]$view.Close() }
}

function Resolve-SdkDirectory([string]$Id, [hashtable]$Directories, [hashtable]$Resolved, [int]$Depth = 0) {
    if ($Id -eq 'TARGETDIR') { return '' }
    if ($Depth -gt 32 -or -not $Directories.ContainsKey($Id)) { throw 'Invalid SDK directory table.' }
    if ($Resolved.ContainsKey($Id)) { return $Resolved[$Id] }
    $row = $Directories[$Id]
    $parent = Resolve-SdkDirectory $row[0] $Directories $Resolved ($Depth + 1)
    $name = ($row[1].Split(':')[0]).Split('|')[-1]
    if ($name -eq '.') { $relative = $parent }
    else {
        if ($name -notmatch '^[a-zA-Z0-9 ._()-]+$' -or $name -eq '..') { throw 'Unsafe SDK directory name.' }
        $relative = if ($parent) { $parent + '/' + $name } else { $name }
    }
    $Resolved[$Id] = $relative
    return $relative
}

function Install-Compiler {
    $compilerRoot = Build-Path 'toolchain'
    $marker = Build-Path 'toolchain/.assembleai-native-toolchain.json'
    $identity = $lock.toolchain.catalogSha256 + ':' + $lock.toolchain.msvcVersion + ':' + $lock.toolchain.sdkVersion
    if (Test-Path -LiteralPath $marker) {
        $previous = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($previous.identity -ne $identity -or $previous.workspace -ne $workspaceRoot) { throw 'Toolchain marker does not match the pinned build.' }
    } elseif (Test-Path -LiteralPath $compilerRoot) {
        throw 'Refusing an unmarked compiler directory.'
    } else {
        [void][IO.Directory]::CreateDirectory($compilerRoot)
        [IO.File]::WriteAllText($marker, (@{identity=$identity;workspace=$workspaceRoot} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    foreach ($payload in $lock.toolchain.msvc) {
        $archive = Get-BuildPayload $payload
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $zip.Entries) {
                if (-not $entry.FullName.StartsWith('Contents/') -or $entry.FullName.EndsWith('/')) { continue }
                $relative = [Uri]::UnescapeDataString($entry.FullName.Substring(9))
                $target = Build-Path ('toolchain/msvc/' + $relative)
                if (-not $target.StartsWith((Build-Path 'toolchain/msvc') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe MSVC archive entry.' }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                if ((Test-Path -LiteralPath $target -PathType Leaf) -and (Get-Item -LiteralPath $target).Length -eq $entry.Length) {
                    $stream = $entry.Open()
                    $hasher = [Security.Cryptography.SHA256]::Create()
                    try { $entryHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
                    finally { $stream.Dispose(); $hasher.Dispose() }
                    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $entryHash) { continue }
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally { $zip.Dispose() }
    }
    # Read MSI tables only; never invoke Windows Installer installation actions.
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    foreach ($group in $lock.toolchain.sdk) {
        $database = $windowsInstaller.OpenDatabase((Get-BuildPayload $group.msi), 0)
        $directories = @{}
        foreach ($row in (Read-MsiRows $database 'SELECT Directory, Directory_Parent, DefaultDir FROM Directory' 3)) { $directories[$row[0]] = @($row[1], $row[2]) }
        $components = @{}
        foreach ($row in (Read-MsiRows $database 'SELECT Component, Directory_ FROM Component' 2)) { $components[$row[0]] = $row[1] }
        $resolved = @{}
        $files = @()
        foreach ($row in (Read-MsiRows $database 'SELECT File, Component_, FileName, Sequence FROM File' 4)) {
            $directory = Resolve-SdkDirectory $components[$row[1]] $directories $resolved
            $relative = $directory + '/' + $row[2].Split('|')[-1]
            $version = [regex]::Escape($lock.toolchain.sdkVersion)
            if ($relative -notmatch ('^Windows Kits/10/(Include/' + $version + '/(ucrt|shared|um)/|Lib/' + $version + '/(ucrt|um)/x64/)')) { continue }
            $target = Build-Path ('toolchain/sdk/' + $relative)
            if (-not $target.StartsWith((Build-Path 'toolchain/sdk') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe SDK file table.' }
            $files += [pscustomobject]@{id=$row[0];target=$target;sequence=[int]$row[3]}
        }
        $previousSequence = 0
        foreach ($media in (Read-MsiRows $database 'SELECT Cabinet, LastSequence FROM Media ORDER BY LastSequence' 2)) {
            $selected = @($files | Where-Object { $_.sequence -gt $previousSequence -and $_.sequence -le [int]$media[1] })
            $previousSequence = [int]$media[1]
            if ($selected.Count -eq 0) { continue }
            $payloads = @($group.cabinets | Where-Object { [IO.Path]::GetFileName($_.fileName) -eq $media[0] })
            if ($payloads.Count -ne 1) { throw 'SDK cabinet is not locked.' }
            $cabinet = Get-BuildPayload $payloads[0]
            $stagingRelative = 'toolchain/extract-' + [Guid]::NewGuid().ToString('N')
            $staging = Build-Path $stagingRelative
            [void][IO.Directory]::CreateDirectory($staging)
            try {
                [void](Run-Local (Join-Path $env:SystemRoot 'System32/expand.exe') @('-F:*', $cabinet, $staging) @{TEMP=$temporary;TMP=$temporary} $compilerRoot 120)
                foreach ($file in $selected) {
                    if ($file.id -notmatch '^[a-zA-Z0-9_.-]+$') { throw 'Unsafe SDK cabinet member.' }
                    $extracted = Join-Path $staging $file.id
                    if (-not (Test-Path -LiteralPath $extracted -PathType Leaf)) { throw 'SDK cabinet lacks a locked MSI file.' }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.target))
                    Copy-Item -LiteralPath $extracted -Destination $file.target -Force
                }
            } finally { Remove-Item -LiteralPath (Build-Path $stagingRelative) -Recurse -Force }
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller)
    $toolsets = @(Get-ChildItem -LiteralPath (Build-Path 'toolchain/msvc/VC/Tools/MSVC') -Directory)
    if ($toolsets.Count -ne 1) { throw 'Expected one pinned MSVC toolset.' }
    $msvc = $toolsets[0].FullName
    $sdk = Build-Path ('toolchain/sdk/Windows Kits/10')
    foreach ($file in @((Join-Path $msvc 'bin/Hostx64/x64/cl.exe'), (Join-Path $msvc 'bin/Hostx64/x64/nmake.exe'), (Join-Path $sdk ('Include/' + $lock.toolchain.sdkVersion + '/um/windows.h')))) {
        if (-not (Test-Path -LiteralPath $file)) { throw 'Portable compiler or SDK is incomplete.' }
    }
    return @{msvc=$msvc;sdk=$sdk}
}

function Stop-CompilerTelemetry([string]$CompilerRoot) {
    # cl may start VCTIP despite being invoked without Visual Studio. It is not
    # a service; stop only the binary inside this workspace-owned toolchain.
    $expected = [IO.Path]::GetFullPath((Join-Path $CompilerRoot 'bin/Hostx64/x64/VCTIP.EXE'))
    if (-not $expected.StartsWith((Build-Path 'toolchain/msvc') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Compiler cleanup path is outside the owned toolchain.' }
    foreach ($process in (Get-CimInstance Win32_Process -Filter "Name='VCTIP.EXE'")) {
        if ($process.ExecutablePath -and [IO.Path]::GetFullPath($process.ExecutablePath) -eq $expected) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-Vector {
    if ($lock.pgvector.repository -ne 'https://github.com/pgvector/pgvector.git' -or
        $lock.pgvector.commit -notmatch '^[a-f0-9]{40}$' -or $lock.pgvector.version -notmatch '^\d+\.\d+\.\d+$' -or
        $lock.pgvector.tag -ne ('v' + $lock.pgvector.version)) { throw 'Invalid pgvector source lock.' }
    $runtime = Local-Path (Join-Path $installationRoot 'postgresql/pgsql')
    $runtimeMarker = Local-Path (Join-Path $installationRoot 'postgresql/.assembleai-native-vector.json')
    if (Test-Path -LiteralPath $runtimeMarker) {
        $existing = Get-Content -LiteralPath $runtimeMarker -Raw | ConvertFrom-Json
        if ($existing.commit -ne $lock.pgvector.commit -or $existing.postgresVersion -ne $lock.pgvector.postgresVersion -or
            -not (Test-Path -LiteralPath (Join-Path $runtime 'share/extension/vector.control'))) { throw 'Existing vector runtime does not match the lock.' }
        Write-Host '[PASS] Reusing isolated PostgreSQL 17.11 / pgvector 0.8.7 runtime.'
        return $runtime
    }
    $compiler = Install-Compiler
    $originalPostgres = [IO.Path]::GetFullPath((Join-Path $workspaceRoot '.local-data/native-postgres/postgresql/pgsql'))
    if (-not (Test-Path -LiteralPath (Join-Path $originalPostgres 'include/server/postgres.h'))) { throw 'Install the locked native PostgreSQL package first.' }
    $environment = @{
        PATH = (Join-Path $compiler.msvc 'bin/Hostx64/x64') + ';' + (Join-Path $originalPostgres 'bin') + ';' + $env:SystemRoot + '\System32'
        INCLUDE = (Join-Path $compiler.msvc 'include') + ';' + (Join-Path $compiler.sdk ('Include/' + $lock.toolchain.sdkVersion + '/ucrt')) + ';' + (Join-Path $compiler.sdk ('Include/' + $lock.toolchain.sdkVersion + '/shared')) + ';' + (Join-Path $compiler.sdk ('Include/' + $lock.toolchain.sdkVersion + '/um'))
        LIB = (Join-Path $compiler.msvc 'lib/x64') + ';' + (Join-Path $compiler.sdk ('Lib/' + $lock.toolchain.sdkVersion + '/ucrt/x64')) + ';' + (Join-Path $compiler.sdk ('Lib/' + $lock.toolchain.sdkVersion + '/um/x64'))
        PGROOT=$originalPostgres; TEMP=$temporary; TMP=$temporary
    }
    $version = Run-Local (Join-Path $originalPostgres 'bin/pg_config.exe') @('--version') $environment $installationRoot
    if ($version -ne ('PostgreSQL ' + $lock.pgvector.postgresVersion)) { throw 'PostgreSQL ABI version does not match vector lock.' }
    $vectorRoot = Local-Path (Join-Path $installationRoot 'pgvector')
    $source = Local-Path (Join-Path $vectorRoot 'source')
    $sourceMarker = Local-Path (Join-Path $vectorRoot '.assembleai-native-source.json')
    if (Test-Path -LiteralPath $vectorRoot) {
        if (-not (Test-Path -LiteralPath $sourceMarker) -or (Get-Content -LiteralPath $sourceMarker -Raw | ConvertFrom-Json).commit -ne $lock.pgvector.commit) { throw 'Unrecognized vector source directory.' }
    } else {
        [void][IO.Directory]::CreateDirectory($vectorRoot)
        [IO.File]::WriteAllText($sourceMarker, ($lock.pgvector | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    if (-not (Test-Path -LiteralPath $source)) { [void](Run-Local $git @('clone','--depth','1','--branch',$lock.pgvector.tag,$lock.pgvector.repository,$source) $buildEnvironment $vectorRoot 300) }
    if ((Run-Local $git @('rev-parse','HEAD') $buildEnvironment $source) -ne $lock.pgvector.commit) { throw 'Vector source commit does not match lock.' }
    if ((Run-Local $git @('diff','--name-only','HEAD') $buildEnvironment $source)) { throw 'Vector tracked source is modified.' }
    $nmake = Join-Path $compiler.msvc 'bin/Hostx64/x64/nmake.exe'
    try { [void](Run-Local $nmake @('/F','Makefile.win') $environment $source 300) }
    finally { Stop-CompilerTelemetry $compiler.msvc }
    $runtimeParent = Local-Path (Join-Path $installationRoot 'postgresql')
    if (Test-Path -LiteralPath $runtimeParent) { throw 'Refusing an unmarked existing vector runtime.' }
    $stagingName = 'postgresql.stage-' + [Guid]::NewGuid().ToString('N')
    $staging = Local-Path (Join-Path $installationRoot $stagingName)
    $stagedRuntime = Local-Path (Join-Path $staging 'pgsql')
    [void][IO.Directory]::CreateDirectory($stagedRuntime)
    try {
        foreach ($directory in @('bin','lib','share')) { Copy-Item -LiteralPath (Join-Path $originalPostgres $directory) -Destination (Join-Path $stagedRuntime $directory) -Recurse }
        $environment['PGROOT'] = $stagedRuntime
        try { [void](Run-Local $nmake @('/F','Makefile.win','install') $environment $source 120) }
        finally { Stop-CompilerTelemetry $compiler.msvc }
        [IO.File]::WriteAllText((Join-Path $staging '.assembleai-native-vector.json'), (@{commit=$lock.pgvector.commit;postgresVersion=$lock.pgvector.postgresVersion;vectorVersion=$lock.pgvector.version} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath (Local-Path $staging) -Destination $runtimeParent
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Local-Path $staging) -Recurse -Force }
    }
    Write-Host '[PASS] Built pgvector 0.8.7 into a separate PostgreSQL 17.11 home.'
    return $runtime
}

# PowerShell 7 provides ArgumentList and process-tree termination used above.
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this installer with pwsh (PowerShell 7+).' }
$goHome = Install-Package 'go' $lock.packages.go
$erlangHome = Install-Package 'erlang' $lock.packages.erlang
$rabbitHome = Install-Package 'rabbitmq' $lock.packages.rabbitmq
$buildRoot = $installationRoot
if ($BuildCacheRoot) {
    $buildRoot = [IO.Path]::GetFullPath($BuildCacheRoot)
    if ($buildRoot -eq [IO.Path]::GetPathRoot($buildRoot)) { throw 'A drive root cannot be used as a build cache.' }
    $buildMarker = Build-Path '.assembleai-native-build-cache.json'
    if (Test-Path -LiteralPath $buildRoot) {
        if (-not (Test-Path -LiteralPath $buildMarker)) { throw 'Refusing an unmarked existing build cache directory.' }
        $existingBuild = Get-Content -LiteralPath $buildMarker -Raw | ConvertFrom-Json
        if ($existingBuild.workspace -ne $workspaceRoot -or $existingBuild.schemaVersion -ne 1) { throw 'Build cache belongs to another workspace.' }
    } else {
        [void][IO.Directory]::CreateDirectory($buildRoot)
        [IO.File]::WriteAllText($buildMarker, (@{schemaVersion=1;workspace=$workspaceRoot} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
}
$temporary = Build-Path 'tmp'
[void][IO.Directory]::CreateDirectory($temporary)
$buildEnvironment = @{
    PATH = (Join-Path $goHome 'bin') + ';' + $env:PATH
    GOROOT = $goHome
    GOPATH = Local-Path (Join-Path $installationRoot 'go-work')
    GOCACHE = Build-Path 'go-cache'
    GOMODCACHE = Local-Path (Join-Path $installationRoot 'go-modules')
    GOTMPDIR = $temporary
    TEMP = $temporary
    TMP = $temporary
    CGO_ENABLED = '0'
    GOTOOLCHAIN = 'local'
    GOPROXY = 'https://proxy.golang.org'
    GOSUMDB = 'sum.golang.org'
    GOMAXPROCS = '2'
    GOFLAGS = '-p=2'
    GIT_TERMINAL_PROMPT = '0'
}
# Go does not read Windows' system proxy, while Invoke-WebRequest does. Reuse
# the user's existing route only inside the build child; never change settings
# or print a proxy URL (it may be sensitive on another workstation).
if (-not $env:HTTPS_PROXY -and -not $env:HTTP_PROXY) {
    $probeUri = [Uri]'https://proxy.golang.org'
    $systemProxy = [Net.WebRequest]::GetSystemWebProxy().GetProxy($probeUri)
    if ($systemProxy.AbsoluteUri -ne $probeUri.AbsoluteUri -and $systemProxy.Scheme -in @('http', 'https')) {
        $buildEnvironment['HTTPS_PROXY'] = $systemProxy.AbsoluteUri
        $buildEnvironment['HTTP_PROXY'] = $systemProxy.AbsoluteUri
    }
}
$goExe = Join-Path $goHome 'bin/go.exe'
$version = Run-Local $goExe @('version') $buildEnvironment $installationRoot
if ($version -ne ('go version go' + $lock.packages.go.version + ' windows/amd64')) { throw 'Go version does not match lock.' }
Write-Host "[PASS] $version"
$erlExe = Join-Path $erlangHome 'bin/erl.exe'
if (-not (Test-Path -LiteralPath $erlExe)) {
    [void](Run-Local (Join-Path $erlangHome 'Install.exe') @('-s', $erlangHome) @{TEMP=$temporary;TMP=$temporary} $erlangHome)
}
if (-not (Test-Path -LiteralPath $erlExe) -or -not (Test-Path -LiteralPath (Join-Path $rabbitHome 'sbin/rabbitmq-server.bat'))) {
    throw 'Erlang or RabbitMQ entry point is missing.'
}
$otpRelease = Run-Local $erlExe @('-noshell', '-eval', 'io:format("~s", [erlang:system_info(otp_release)]), halt().') @{TEMP=$temporary;TMP=$temporary} $erlangHome
if ($otpRelease -ne '28') { throw 'Unexpected Erlang OTP release.' }
Write-Host '[PASS] Erlang OTP 28 can run without a Windows service.'
$minioRoot = Local-Path (Join-Path $installationRoot 'minio')
$minioExe = Local-Path (Join-Path $minioRoot 'minio.exe')
$source = Local-Path (Join-Path $minioRoot 'source')
$minioMarker = Local-Path (Join-Path $minioRoot '.assembleai-native-source.json')
if (-not $SkipMinioBuild) {
    if ($lock.minio.repository -ne 'https://github.com/minio/minio.git' -or $lock.minio.commit -notmatch '^[a-f0-9]{40}$' -or $lock.minio.goVersion -ne $lock.packages.go.version) {
        throw 'Invalid MinIO source lock.'
    }
    if (Test-Path -LiteralPath $minioRoot) {
        if (-not (Test-Path -LiteralPath $minioMarker)) { throw 'Unrecognized MinIO source directory.' }
        $marker = Get-Content -LiteralPath $minioMarker -Raw | ConvertFrom-Json
        if ($marker.commit -ne $lock.minio.commit) { throw 'Existing MinIO source does not match the lock.' }
    } else {
        [void][IO.Directory]::CreateDirectory($minioRoot)
        [IO.File]::WriteAllText($minioMarker, ($lock.minio | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    if (-not (Test-Path -LiteralPath $source)) {
        [void](Run-Local $git @('clone', '--depth', '1', '--branch', $lock.minio.tag, $lock.minio.repository, $source) $buildEnvironment $minioRoot 600)
    }
    $commit = Run-Local $git @('rev-parse', 'HEAD') $buildEnvironment $source
    if ($commit -ne $lock.minio.commit) { throw 'MinIO checkout does not match the pinned commit.' }
    $changes = Run-Local $git @('status', '--porcelain') $buildEnvironment $source
    if ($changes) { throw 'MinIO source checkout is dirty; refusing to build it.' }
    if (-not (Test-Path -LiteralPath $minioExe)) {
        Write-Host 'Building the pinned MinIO source (isolated cache; two compiler jobs).'
        # Recent PowerShell converts ISO JSON strings to DateTime automatically.
        # Keep the linker definition invariant; a localized date has spaces.
        $versionLiteral = if ($lock.minio.version -is [datetime]) { $lock.minio.version.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } else { [string]$lock.minio.version }
        $flags = '-s -w -X github.com/minio/minio/cmd.Version=' + $versionLiteral + ' -X github.com/minio/minio/cmd.ReleaseTag=' + $lock.minio.tag
        [void](Run-Local $goExe @('build', '-mod=readonly', '-trimpath', '-ldflags', $flags, '-o', $minioExe, '.') $buildEnvironment $source 1800)
    }
    $minioVersion = Run-Local $minioExe @('--version') @{TEMP=$temporary;TMP=$temporary} $minioRoot
    if (-not $minioVersion.Contains($lock.minio.tag)) { throw 'MinIO executable version does not match lock.' }
    Write-Host "[PASS] MinIO $($lock.minio.tag) built from the locked commit."
}
$postgresHome = $null
if ($IncludeVector) { $postgresHome = Install-Vector }
elseif (Test-Path -LiteralPath (Join-Path $installationRoot 'postgresql/.assembleai-native-vector.json')) {
    $postgresHome = Local-Path (Join-Path $installationRoot 'postgresql/pgsql')
}
$result = [ordered]@{
    schemaVersion = 1
    goHome = $goHome
    erlangHome = $erlangHome
    rabbitmqHome = $rabbitHome
    minioExecutable = if (Test-Path -LiteralPath $minioExe) { $minioExe } else { $null }
    minioSha256 = if (Test-Path -LiteralPath $minioExe) { (Get-FileHash -LiteralPath $minioExe -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    minioCommit = $lock.minio.commit
    postgresHome = $postgresHome
    vectorVersion = if ($postgresHome) { $lock.pgvector.version } else { $null }
    pgvectorCommit = if ($postgresHome) { $lock.pgvector.commit } else { $null }
    buildCacheRoot = $buildRoot
    versions = [ordered]@{ go=$lock.packages.go.version; erlang=$lock.packages.erlang.version; rabbitmq=$lock.packages.rabbitmq.version; minio=$lock.minio.tag }
}
[IO.File]::WriteAllText((Local-Path (Join-Path $installationRoot 'installed.json')), ($result | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host '[PASS] Native knowledge dependency manifest written (contains no credentials).'
