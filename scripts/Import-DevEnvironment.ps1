$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'
if (!(Test-Path -LiteralPath $envPath)) { throw 'Run scripts/Initialize-Dev.ps1 first.' }
foreach ($line in Get-Content -LiteralPath $envPath) {
    if ($line -match '^([A-Z][A-Z0-9_]*)=(.+)$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}
$env:Security__PublicKeyPath = Join-Path $repoRoot '.secrets/auth-public.pem'
$env:ConnectionStrings__AuthDb = "Host=localhost;Port=5432;Database=AuthDB;Username=auth_app;Password=$env:AUTH_DB_PASSWORD"
$env:ConnectionStrings__PaymentDb = "Host=localhost;Port=5432;Database=PaymentDB;Username=payment_app;Password=$env:PAYMENT_DB_PASSWORD"
$env:ConnectionStrings__TripDb = "Server=localhost,1433;Database=TripDB;User Id=trip_app;Password=$env:TRIP_DB_PASSWORD;TrustServerCertificate=True"
$env:ConnectionStrings__MatchingDb = "mongodb://matching_app:$env:MATCHING_DB_PASSWORD@localhost:27017/MatchingDB?authSource=MatchingDB"
$env:RabbitMq__Password = $env:RABBITMQ_PASSWORD
$env:Otp__HmacSecret = $env:OTP_HMAC_SECRET
$env:ASPNETCORE_ENVIRONMENT = 'Development'
