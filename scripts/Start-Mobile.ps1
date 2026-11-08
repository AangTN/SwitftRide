param(
    [ValidateSet('Both', 'Rider', 'Driver')][string]$App = 'Both',
    [ValidateSet('Web', 'Android', 'Apk')][string]$Target = 'Web',
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [switch]$SkipBuild,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mobileRoot = Join-Path $repoRoot 'apps/mobile'
$flutterCommand = Get-Command flutter -ErrorAction SilentlyContinue
$flutterPath = if ($flutterCommand) { $flutterCommand.Source } elseif (Test-Path 'D:/Tools/flutter/bin/flutter.bat') { 'D:/Tools/flutter/bin/flutter.bat' } else { throw 'Install Flutter and add flutter/bin to PATH.' }
$mapKey = ''
$envPath = Join-Path $repoRoot '.env'
if (Test-Path -LiteralPath $envPath) {
    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match '^\s*GOONG_MAPTILES_KEY\s*=\s*(.*?)\s*$') {
            $mapKey = $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
}
if (!$mapKey -and $env:GOONG_MAPTILES_KEY) { $mapKey = $env:GOONG_MAPTILES_KEY }
if (!$mapKey -and !$SkipBuild) { Write-Warning 'GOONG_MAPTILES_KEY is missing from .env. The rider app will show a map configuration message.' }
# Whitelist client configuration. Never pass the whole .env (JWT keys, passwords,
# or GOONG_API_KEY) to Flutter. Use a temporary file to keep keys out of CLI logs.
$clientConfigPath = [IO.Path]::GetTempFileName()
@{ GOONG_MAPTILES_KEY = $mapKey } | ConvertTo-Json | Set-Content -LiteralPath $clientConfigPath -Encoding utf8
$mapArguments = @("--dart-define-from-file=$clientConfigPath")
Push-Location $mobileRoot
try {
    $apps = if ($App -eq 'Both') { @('rider', 'driver') } else { @($App.ToLowerInvariant()) }
    foreach ($role in $apps) {
        if ($Target -eq 'Web') {
            if (!$SkipBuild) {
                & $flutterPath build web --target "lib/main_$role.dart" --output "build/web-$role" --no-web-resources-cdn @mapArguments
                if ($LASTEXITCODE -ne 0) { throw "Failed to build $role web app." }
            }
        } elseif ($Target -eq 'Android') {
            if ($App -eq 'Both') { throw 'Select -App Rider or -App Driver when running on Android.' }
            & $flutterPath run --flavor $role --target "lib/main_$role.dart" "--dart-define=API_BASE_URL=$ApiBaseUrl" @mapArguments
            if ($LASTEXITCODE -ne 0) { throw 'Android launch failed.' }
        } else {
            if (!$ApiBaseUrl.StartsWith('https://')) { throw 'APK release requires -ApiBaseUrl https://your-api-domain. Use Android target for local HTTP testing.' }
            & $flutterPath build apk --release --flavor $role --target "lib/main_$role.dart" "--dart-define=API_BASE_URL=$ApiBaseUrl" @mapArguments
            if ($LASTEXITCODE -ne 0) { throw "Failed to build $role APK." }
        }
    }
    if ($BuildOnly) { return }
    if ($Target -eq 'Web') {
        foreach ($role in $apps) {
            if (!(Test-Path "build/web-$role/index.html")) { throw "Build $role first or omit -SkipBuild." }
        }
        $env:SWIFTRIDE_API_URL = $ApiBaseUrl
        $env:SWIFTRIDE_APPS = $apps -join ','
        node (Join-Path $PSScriptRoot 'serve-mobile.mjs')
    }
} finally {
    Remove-Item -LiteralPath $clientConfigPath -ErrorAction SilentlyContinue
    Pop-Location
}
