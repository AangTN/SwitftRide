$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Import-DevEnvironment.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $repoRoot 'tools/MigrateDriverProfiles/MigrateDriverProfiles.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Driver profile migration failed. Keep Auth/Matching stopped until resolved.' }
