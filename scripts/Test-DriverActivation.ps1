$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Import-DevEnvironment.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $repoRoot 'tools/MigrateDriverProfiles/MigrateDriverProfiles.csproj') -- --verify
if ($LASTEXITCODE -ne 0) { throw 'Driver/Rider end-to-end verification failed.' }
