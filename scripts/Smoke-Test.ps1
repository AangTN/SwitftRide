param([string]$BaseUrl = 'http://localhost:5000')
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
function Invoke-JsonPost([string]$Path, $Body) {
    Invoke-RestMethod -Uri "$BaseUrl$Path" -Method Post -ContentType 'application/json' -Body ($Body | ConvertTo-Json)
}
function Assert-Status([string]$Path, $Body, [int]$Expected) {
    $response = Invoke-WebRequest -Uri "$BaseUrl$Path" -Method Post -ContentType 'application/json' `
        -Body ($Body | ConvertTo-Json) -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne $Expected) { throw "Expected HTTP $Expected, got $($response.StatusCode) at $Path" }
}
Invoke-RestMethod "$BaseUrl/health/ready" | Out-Null
$challenge = Invoke-JsonPost '/api/auth/login/request-otp' @{ phoneNumber = '0900000002'; clientApp = 'DriverApp' }
if ($challenge.PSObject.Properties.Name -contains 'devOtp') { throw 'OTP must not be returned.' }
$tokens = Invoke-JsonPost '/api/auth/login/verify-otp' @{ challengeId = $challenge.challengeId; otp = '123456'; clientApp = 'DriverApp' }
$headers = @{ Authorization = "Bearer $($tokens.accessToken)" }
$me = Invoke-RestMethod "$BaseUrl/api/auth/me" -Headers $headers
if ($me.roles -notcontains 'Driver') { throw 'Driver seed role is missing.' }
$profile = Invoke-RestMethod "$BaseUrl/api/matching/drivers/me" -Headers $headers
if ($profile.driverId -ne $me.id -or !$profile.legalName -or !$profile.plateNumber) { throw 'Matching driver profile is invalid.' }
$rotated = Invoke-JsonPost '/api/auth/refresh' @{ refreshToken = $tokens.refreshToken }
Assert-Status '/api/auth/refresh' @{ refreshToken = $tokens.refreshToken } 401
Assert-Status '/api/auth/refresh' @{ refreshToken = $rotated.refreshToken } 401
# Reuse revokes refresh capability; the already issued access JWT stays valid until expiry.
Invoke-WebRequest "$BaseUrl/api/auth/logout" -Method Post -Headers $headers | Out-Null
Write-Output 'Gateway smoke test passed: OTP login, Auth /me, Matching driver profile, rotation, reuse revocation and logout.'
