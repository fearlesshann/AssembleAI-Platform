[CmdletBinding()]
param(
    [switch]$UseCompose,
    [string]$Psql = 'psql',
    [string]$DatabaseHost = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$Port = 5432,
    [string]$Username = 'postgres'
)
$ErrorActionPreference = 'Stop'
if ($UseCompose) {
    $composePath = Join-Path $PSScriptRoot '..\compose\compose.yaml'
    $envPath = Join-Path $PSScriptRoot '..\compose\.env'
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'The optional -UseCompose mode requires Docker with Compose v2.' }
    if (-not (Test-Path -LiteralPath $envPath)) { throw 'Create deploy/compose/.env before running Compose migrations.' }
    & docker compose --env-file $envPath -f $composePath exec -T postgres psql --no-psqlrc --username postgres --dbname assembleai_platform --set ON_ERROR_STOP=1 --file /migrations/apply.sql
} else {
    $psqlCommand = Get-Command $Psql -CommandType Application -ErrorAction SilentlyContinue
    if (-not $psqlCommand) { throw 'Native psql was not found. Supply -Psql with its path; Docker is not required.' }
    $migrationFile = Join-Path $PSScriptRoot 'migrations\apply.sql'
    # Use PGPASSWORD or a PostgreSQL password file; never place passwords in CLI arguments.
    # --no-password prevents a headless verification session from hanging on a prompt.
    & $psqlCommand.Source --no-psqlrc --no-password --host $DatabaseHost --port $Port --username $Username --dbname assembleai_platform --set ON_ERROR_STOP=1 --file $migrationFile
}
if ($LASTEXITCODE -ne 0) { throw "Platform migration failed with exit code $LASTEXITCODE." }
