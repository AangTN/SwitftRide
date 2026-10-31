param(
    [ValidateSet('Both', 'Rider', 'Driver')][string]$App = 'Both',
    [ValidateSet('Web', 'Android', 'Apk')][string]$Target = 'Web',
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mobileRoot = Join-Path $repoRoot 'apps/mobile'
$flutterCommand = Get-Command flutter -ErrorAction SilentlyContinue
$flutterPath = if ($flutterCommand) { $flutterCommand.Source } elseif (Test-Path 'D:/Tools/flutter/bin/flutter.bat') { 'D:/Tools/flutter/bin/flutter.bat' } else { throw 'Install Flutter and add flutter/bin to PATH.' }
Push-Location $mobileRoot
try {
    $apps = if ($App -eq 'Both') { @('rider', 'driver') } else { @($App.ToLowerInvariant()) }
    foreach ($role in $apps) {
        if ($Target -eq 'Web') {
            if (!$SkipBuild) {
                & $flutterPath build web --target "lib/main_$role.dart" --output "build/web-$role" --no-web-resources-cdn
                if ($LASTEXITCODE -ne 0) { throw "Failed to build $role web app." }
            }
        } elseif ($Target -eq 'Android') {
            if ($App -eq 'Both') { throw 'Select -App Rider or -App Driver when running on Android.' }
            & $flutterPath run --flavor $role --target "lib/main_$role.dart" "--dart-define=API_BASE_URL=$ApiBaseUrl"
            if ($LASTEXITCODE -ne 0) { throw 'Android launch failed.' }
        } else {
            if (!$ApiBaseUrl.StartsWith('https://')) { throw 'APK release requires -ApiBaseUrl https://your-api-domain. Use Android target for local HTTP testing.' }
            & $flutterPath build apk --release --flavor $role --target "lib/main_$role.dart" "--dart-define=API_BASE_URL=$ApiBaseUrl"
            if ($LASTEXITCODE -ne 0) { throw "Failed to build $role APK." }
        }
    }
    if ($Target -eq 'Web') {
        if (!(Test-Path 'build/web-rider/index.html') -or !(Test-Path 'build/web-driver/index.html')) { throw 'First run with -App Both to build both previews.' }
        $env:SWIFTRIDE_API_URL = $ApiBaseUrl
        node (Join-Path $PSScriptRoot 'serve-mobile.mjs')
    }
} finally { Pop-Location }
