$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$secretDirectory = Join-Path $repoRoot '.secrets'
New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null
$privatePath = Join-Path $secretDirectory 'auth-private.pem'
$publicPath = Join-Path $secretDirectory 'auth-public.pem'
if (!(Test-Path -LiteralPath $privatePath)) {
    $rsaKey = [System.Security.Cryptography.RSA]::Create(3072)
    try {
        [System.IO.File]::WriteAllText($privatePath, $rsaKey.ExportPkcs8PrivateKeyPem())
        [System.IO.File]::WriteAllText($publicPath, $rsaKey.ExportSubjectPublicKeyInfoPem())
    } finally { $rsaKey.Dispose() }
} elseif (!(Test-Path -LiteralPath $publicPath)) {
    throw 'Private key exists but public key is missing. Restore the matching public key.'
}
$envPath = Join-Path $repoRoot '.env'
if (!(Test-Path -LiteralPath $envPath)) {
    function New-DevSecret {
        'Sr1' + [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
    }
    $settings = @(
        'POSTGRES_ADMIN_PASSWORD=' + (New-DevSecret)
        'AUTH_DB_PASSWORD=' + (New-DevSecret)
        'PAYMENT_DB_PASSWORD=' + (New-DevSecret)
        'SQLSERVER_SA_PASSWORD=' + (New-DevSecret)
        'TRIP_DB_PASSWORD=' + (New-DevSecret)
        'MONGO_ROOT_PASSWORD=' + (New-DevSecret)
        'MATCHING_DB_PASSWORD=' + (New-DevSecret)
        'RABBITMQ_PASSWORD=' + (New-DevSecret)
        'OTP_HMAC_SECRET=' + (New-DevSecret)
    )
    [System.IO.File]::WriteAllText($envPath, ($settings -join "`n") + "`n")
}
Write-Output 'Development .env and RSA keys are ready. Existing secrets were preserved.'
