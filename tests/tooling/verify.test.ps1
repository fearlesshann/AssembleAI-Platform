# Regression checks for the runner's failure behavior. No package installation or builds.
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner = Join-Path $repoRoot 'tools/verify.ps1'
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors.Message -join [Environment]::NewLine) }

$shell = (Get-Process -Id $PID).Path
$missingPython = Join-Path $repoRoot 'tests/tooling/does-not-exist/python.exe'
$output = & $shell -NoProfile -File $runner -Scope Core -Python $missingPython 2>&1
$code = $LASTEXITCODE
if ($code -ne 2 -or ($output -join "`n") -notmatch '\[BLOCKED\] Python environment') {
    throw "Missing explicit Python must fail with blocked exit 2; actual $code. Output: $output"
}
if (($output -join "`n") -match 'Selected Core code checks passed') { throw 'Blocked verification was reported as passed.' }

# An existing executable that is not Python must preserve the child's non-zero status.
$node = (Get-Command node -CommandType Application).Source
$output = & $shell -NoProfile -File $runner -Scope Core -Python $node 2>&1
$code = $LASTEXITCODE
if ($code -ne 1 -or ($output -join "`n") -notmatch '\[BLOCKED\] Python environment') {
    throw "Invalid interpreter's exit 1 must propagate; actual $code. Output: $output"
}
Write-Output 'PASS: runner parses; missing interpreter blocks; native failure exit propagates; no false-green summary.'
