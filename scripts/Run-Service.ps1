param([Parameter(Mandatory)][ValidateSet('Auth', 'Trip', 'Matching', 'Payment', 'Gateway')][string]$Service)
. "$PSScriptRoot/Import-DevEnvironment.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:Database__MigrateOnStartup = 'true'
$env:Seed__Enabled = 'true'
$ports = @{ Auth = 5101; Trip = 5102; Matching = 5103; Payment = 5104; Gateway = 5000 }
$env:ASPNETCORE_URLS = "http://localhost:$($ports[$Service])"
if ($Service -eq 'Auth') { $env:Security__PrivateKeyPath = Join-Path $repoRoot '.secrets/auth-private.pem' }
if ($Service -eq 'Gateway') {
    $projectPath = Join-Path $repoRoot 'src/ApiGateway/SwiftRide.ApiGateway.csproj'
} else { $projectPath = Join-Path $repoRoot "src/$($Service)Service/Api/SwiftRide.$Service.Api.csproj" }
dotnet run --project $projectPath --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "Service exited with code $LASTEXITCODE" }
