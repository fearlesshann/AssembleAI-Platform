[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot '.env'
if (Test-Path -LiteralPath $destination) {
    throw '.env already exists; existing local credentials were not changed.'
}
$keys = @(
    'POSTGRES_PASSWORD', 'PLATFORM_DB_PASSWORD', 'KEYCLOAK_DB_PASSWORD',
    'REDIS_PASSWORD', 'RABBITMQ_PASSWORD', 'MINIO_ROOT_PASSWORD',
    'KEYCLOAK_ADMIN_PASSWORD', 'ASSEMBLEAI_OIDC_CLIENT_SECRET'
)
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $lines = foreach ($key in $keys) {
        $bytes = New-Object byte[] 32
        $random.GetBytes($bytes)
        $value = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
        "$key=$value"
    }
    # CreateNew avoids overwriting a file created concurrently.
    $stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write(($lines -join "`n") + "`n") } finally { $writer.Dispose() }
} finally { $random.Dispose() }
Write-Output 'Created deploy/compose/.env with independent random development credentials; values were not printed.'
