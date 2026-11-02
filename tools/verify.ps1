<#
.SYNOPSIS
Runs local code verification without installing dependencies or starting Docker.
.DESCRIPTION
Core: Node tests, cache-only locked .NET restore/build/smoke, Python tests and proxy smoke.
Frontend: Node tests, vue-tsc, both Vite builds and Playwright browser tests.
All: both scopes, plus package-lock.json, uv.lock and project .venv prerequisites.
All covers code verification only; container, database migration and Keycloak acceptance
must be run separately. No scope implies T2 is complete.
.EXAMPLE
powershell -NoProfile -File tools/verify.ps1 -Scope Core -Python E:\Users\Mryeh\miniconda3\python.exe
.EXAMPLE
powershell -NoProfile -File tools/verify.ps1 -Scope All
#>
[CmdletBinding()]
param(
    [ValidateSet('Core', 'Frontend', 'All')]
    [string]$Scope = 'Core',
    [string]$Python
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results = [System.Collections.Generic.List[object]]::new()
$verificationExitCode = 0
$temporaryRoot = $null

function Write-Result {
    param([string]$Name, [string]$State, [string]$Detail, [int]$Code = 0)
    $results.Add([pscustomobject]@{ Check = $Name; State = $State; Detail = $Detail })
    Write-Host "[$State] $Name - $Detail"
    if ($Code -ne 0 -and $script:verificationExitCode -eq 0) {
        $script:verificationExitCode = $Code
    }
}

function Resolve-Executable {
    param([string]$Name)
    if (-not $Name) { return $null }
    if (Test-Path -LiteralPath $Name -PathType Leaf) {
        return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Name).Path)
    }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    return $null
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    # Windows CRT quoting, also accepted by ProcessStartInfo on PowerShell 7.
    # Arguments stay data: no shell, Invoke-Expression or concatenated executable commands.
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $slashes + 1)))
            [void]$builder.Append('"')
        } else {
            [void]$builder.Append(('\' * $slashes))
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    [void]$builder.Append(('\' * (2 * $slashes)))
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-LocalProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory = $workspaceRoot)
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.EnvironmentVariables['UV_OFFLINE'] = 'true'
    $start.EnvironmentVariables['UV_PYTHON_DOWNLOADS'] = 'never'
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()).Trim()
        }
    } catch {
        return [pscustomobject]@{ ExitCode = 127; Output = "Cannot launch '$Executable': $($_.Exception.Message)" }
    } finally { $process.Dispose() }
}

function Invoke-Check {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory = $workspaceRoot)
    Write-Host "`nRunning: $Name"
    $run = Invoke-LocalProcess -Executable $Executable -Arguments $Arguments -WorkingDirectory $WorkingDirectory
    if ($run.Output) { Write-Host $run.Output }
    if ($run.ExitCode -eq 0) {
        Write-Result $Name 'PASS' 'Command exited 0.'
        return $true
    }
    Write-Result $Name 'FAIL' "Command exited $($run.ExitCode). Dependent steps will not run." $run.ExitCode
    return $false
}

function Test-RequiredFile {
    param([string]$RelativePath, [string]$CheckName)
    if (Test-Path -LiteralPath (Join-Path $workspaceRoot $RelativePath) -PathType Leaf) {
        Write-Result $CheckName 'PASS' $RelativePath
        return $true
    }
    Write-Result $CheckName 'BLOCKED' "Missing $RelativePath. Verification never creates or invents a lockfile." 2
    return $false
}

function Find-Python {
    param([string]$Requested)
    if ($Requested) { return Resolve-Executable $Requested }
    $venvCandidates = @(
        (Join-Path $workspaceRoot 'apps/sample/backend/.venv/Scripts/python.exe'),
        (Join-Path $workspaceRoot 'apps/sample/backend/.venv/bin/python')
    )
    $candidates = @($venvCandidates)
    foreach ($name in @('python', 'python3')) {
        $resolved = Resolve-Executable $name
        if ($resolved) { $candidates += $resolved }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $probe = Invoke-LocalProcess $candidate @('-c', 'import sys; sys.exit(0 if sys.version_info[:2] == (3,13) else 3)')
        if ($probe.ExitCode -eq 0) { return $candidate }
    }
    $uv = Resolve-Executable 'uv'
    if ($uv) {
        # --no-cache avoids changing the user's cache; offline forbids downloads/network.
        $found = Invoke-LocalProcess $uv @('--offline', '--no-cache', 'python', 'find', '--no-python-downloads', '3.13')
        if ($found.ExitCode -eq 0) {
            $candidate = Resolve-Executable (($found.Output -split "`r?`n")[0].Trim())
            if ($candidate) { return $candidate }
        }
    }
    return $null
}

function Write-Summary {
    Write-Host "`nVerification summary ($Scope)"
    $results | Format-Table Check, State, Detail -Wrap | Out-Host
    Write-Host 'Coverage: code verification only. Docker/container startup, database migrations and Keycloak login are NOT verified here.'
    if ($Scope -eq 'Core') {
        Write-Host 'Core excludes frontend dependency installation, builds, browser tests and reproducible lockfile/venv acceptance.'
    }
    if ($script:verificationExitCode -eq 0) {
        Write-Host "Selected $Scope code checks passed. This does not mark T2 complete."
    } else {
        Write-Host "Verification incomplete or failed (exit $script:verificationExitCode). No all-green result."
    }
}

Push-Location $workspaceRoot
try {
    Write-Host "Workspace: $workspaceRoot"
    Write-Host "Scope: $Scope. No dependency installation, network probes, Docker startup or credential writes."
    $node = Resolve-Executable 'node'
    $dotnet = Resolve-Executable 'dotnet'
    $pythonPath = $null
    $includeCore = $Scope -in @('Core', 'All')
    $includeFrontend = $Scope -in @('Frontend', 'All')

    if ($node) {
        $nodeProbe = Invoke-LocalProcess $node @('--version')
        if ($nodeProbe.ExitCode -eq 0 -and $nodeProbe.Output -match '^v24\.' -and [version]($nodeProbe.Output.TrimStart('v')) -ge [version]'24.18.0') {
            Write-Result 'Node runtime' 'PASS' "$node ($($nodeProbe.Output))"
        } else {
            Write-Result 'Node runtime' 'BLOCKED' "Node >=24.18.0 <25 is required; probe: $($nodeProbe.Output)" $(if ($nodeProbe.ExitCode -ne 0) { $nodeProbe.ExitCode } else { 2 })
            $node = $null
        }
    } else { Write-Result 'Node runtime' 'BLOCKED' 'Node executable was not found.' 2 }

    if ($includeCore) {
        if ($dotnet) {
            $sdkProbe = Invoke-LocalProcess $dotnet @('--version')
            if ($sdkProbe.ExitCode -eq 0) { Write-Result '.NET SDK' 'PASS' "$dotnet ($($sdkProbe.Output))" }
            else { Write-Result '.NET SDK' 'BLOCKED' $sdkProbe.Output $sdkProbe.ExitCode; $dotnet = $null }
        } else { Write-Result '.NET SDK' 'BLOCKED' 'dotnet executable was not found.' 2 }
        $pythonPath = Find-Python $Python
        if (-not $pythonPath) {
            Write-Result 'Python environment' 'BLOCKED' 'No usable Python 3.13 found. Supply -Python with an existing interpreter; nothing will be installed.' 2
        } else {
            $pythonProbeCode = @'
import sys, pathlib, tomllib, importlib.metadata
if sys.version_info[:2] != (3, 13):
    print('Python 3.13 is required; found ' + sys.version.split()[0]); sys.exit(3)
project = tomllib.loads(pathlib.Path('apps/sample/backend/pyproject.toml').read_text(encoding='utf-8'))
for requirement in project['project']['dependencies'] + project['dependency-groups']['dev']:
    name, expected = requirement.split('==', 1)
    actual = importlib.metadata.version(name)
    if actual != expected:
        print(f'{name}: expected {expected}, found {actual}'); sys.exit(3)
import fastapi, uvicorn, pytest, httpx
print(sys.executable + ' | Python ' + sys.version.split()[0] + ' | declared backend/test versions match')
'@
            $pythonProbe = Invoke-LocalProcess $pythonPath @('-c', $pythonProbeCode)
            if ($pythonProbe.ExitCode -eq 0) { Write-Result 'Python environment' 'PASS' $pythonProbe.Output }
            else { Write-Result 'Python environment' 'BLOCKED' $pythonProbe.Output $pythonProbe.ExitCode; $pythonPath = $null }
        }
        if ($Python -and -not $pythonPath) {
            # An explicit invalid interpreter must fail promptly, never silently choose another.
            Write-Result 'Core checks' 'SKIP' 'The explicitly selected Python interpreter is unusable.'
            Write-Summary
            exit $verificationExitCode
        }
    }

    if ($Scope -eq 'All') {
        [void](Test-RequiredFile 'package-lock.json' 'npm lockfile')
        [void](Test-RequiredFile 'apps/sample/backend/uv.lock' 'uv lockfile')
        $venvPython = @('apps/sample/backend/.venv/Scripts/python.exe', 'apps/sample/backend/.venv/bin/python') |
            Where-Object { Test-Path -LiteralPath (Join-Path $workspaceRoot $_) -PathType Leaf } | Select-Object -First 1
        if (-not $venvPython) {
            Write-Result 'Project Python venv' 'BLOCKED' 'Missing apps/sample/backend/.venv; a preinstalled global environment does not prove reproducibility.' 2
        } elseif (-not $pythonPath -or [System.IO.Path]::GetFullPath($pythonPath) -ne [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $venvPython))) {
            Write-Result 'Project Python venv' 'BLOCKED' 'All must use the project .venv interpreter. Remove -Python or point it at that environment.' 2
        } else { Write-Result 'Project Python venv' 'PASS' $venvPython }
    }

    if ($node) {
        $nodeTests = @(Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'tests/frontend') -Filter '*.test.mjs' -File | ForEach-Object { $_.FullName })
        if ($nodeTests.Count -eq 0) { Write-Result 'Node tests' 'BLOCKED' 'No frontend Node tests were found.' 2 }
        else { [void](Invoke-Check 'Node tests' $node (@('--test') + $nodeTests)) }
    } else { Write-Result 'Node tests' 'SKIP' 'Node prerequisite failed.' }

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('assembleai-verify-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $temporaryRoot)
    $buildPassed = $false
    $pythonTestsPassed = $false
    if ($includeCore) {
        if ($dotnet) {
            $emptyFeed = Join-Path $temporaryRoot 'empty-nuget-feed'
            [void](New-Item -ItemType Directory -Path $emptyFeed)
            $restored = Invoke-Check '.NET cache-only locked restore' $dotnet @('restore', 'AssembleAI.slnx', '--locked-mode', '--source', $emptyFeed, '-p:NuGetAudit=false')
            if ($restored) {
                $buildPassed = Invoke-Check '.NET build' $dotnet @('build', 'AssembleAI.slnx', '--no-restore')
                if ($buildPassed) { [void](Invoke-Check '.NET HTTP smoke' $dotnet @('run', '--project', 'tests/platform/AssembleAI.Platform.SmokeTests.csproj', '--no-build', '--no-restore')) }
                else { Write-Result '.NET HTTP smoke' 'SKIP' 'Build failed.' }
            } else { Write-Result '.NET build / smoke' 'SKIP' 'Cached locked restore failed; no package downloads were attempted.' }
        } else { Write-Result '.NET build / smoke' 'SKIP' '.NET prerequisite failed.' }
        if ($pythonPath) {
            $pythonTestsPassed = Invoke-Check 'FastAPI pytest' $pythonPath @('-m', 'pytest', '-q', '-c', 'apps/sample/backend/pyproject.toml', 'tests/backend')
        } else { Write-Result 'FastAPI pytest' 'SKIP' 'Python prerequisite failed.' }
        if ($buildPassed -and $pythonTestsPassed) {
            [void](Invoke-Check 'FastAPI to YARP HTTP smoke' $pythonPath @('tests/platform/smoke_fastapi_proxy.py'))
        } else { Write-Result 'FastAPI to YARP HTTP smoke' 'SKIP' 'Requires a passing platform build and Python tests.' }
    }

    if ($includeFrontend) {
        $frontendReady = [bool]$node
        foreach ($relativePath in @('node_modules/vue-tsc/bin/vue-tsc.js', 'node_modules/vite/bin/vite.js', 'node_modules/@playwright/test/cli.js', 'node_modules/qiankun/package.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $workspaceRoot $relativePath) -PathType Leaf)) {
                Write-Result 'Frontend dependency' 'BLOCKED' "Missing $relativePath. Install the declared dependencies outside this verification command." 2
                $frontendReady = $false
            }
        }
        if ($frontendReady) {
            $typesPassed = Invoke-Check 'Vue type check' $node @((Join-Path $workspaceRoot 'node_modules/vue-tsc/bin/vue-tsc.js'), '--noEmit', '-p', 'tsconfig.json')
            if ($typesPassed) {
                $vite = Join-Path $workspaceRoot 'node_modules/vite/bin/vite.js'
                $sampleBuilt = Invoke-Check 'Sample UMD build' $node @($vite, 'build') (Join-Path $workspaceRoot 'apps/sample/frontend')
                $portalBuilt = Invoke-Check 'Portal build' $node @($vite, 'build') (Join-Path $workspaceRoot 'apps/portal')
                if ($sampleBuilt -and $portalBuilt) {
                    # The project config uses process.execPath for its preview server too.
                    [void](Invoke-Check 'Playwright browser tests' $node @((Join-Path $workspaceRoot 'node_modules/@playwright/test/cli.js'), 'test', '--config', 'playwright.config.ts'))
                } else { Write-Result 'Playwright browser tests' 'SKIP' 'One or both Vite builds failed.' }
            } else { Write-Result 'Frontend builds / browser' 'SKIP' 'Type check failed.' }
        } else { Write-Result 'Frontend builds / browser' 'SKIP' 'Dependencies or Node are unavailable. This is not a successful frontend verification.' }
    }
} catch {
    Write-Result 'Verification runner' 'FAIL' $_.Exception.Message 1
} finally {
    Pop-Location
    if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryRoot)
        $expectedParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ([System.IO.Path]::GetDirectoryName($resolvedTemporary).TrimEnd([System.IO.Path]::DirectorySeparatorChar) -eq $expectedParent -and
            [System.IO.Path]::GetFileName($resolvedTemporary).StartsWith('assembleai-verify-')) {
            try { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
            catch { Write-Result 'Temporary cleanup' 'FAIL' $_.Exception.Message 1 }
        } else { Write-Result 'Temporary cleanup' 'FAIL' 'Temporary directory containment check failed; directory was not removed.' 1 }
    }
}
Write-Summary
exit $verificationExitCode
