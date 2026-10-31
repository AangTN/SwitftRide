$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Import-DevEnvironment.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
# Tests create and drop only their own randomly named schemas in AuthDB.
$env:SWIFTRIDE_TEST_POSTGRES = $env:ConnectionStrings__AuthDb
try {
    dotnet test (Join-Path $repoRoot 'tests/Auth.Tests/SwiftRide.Auth.Tests.csproj') --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with code $LASTEXITCODE" }
} finally { Remove-Item Env:SWIFTRIDE_TEST_POSTGRES -ErrorAction SilentlyContinue }
