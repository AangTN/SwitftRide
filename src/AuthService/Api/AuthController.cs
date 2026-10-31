using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Api;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register/request-otp"), AllowAnonymous]
    public Task<OtpResponse> RequestRegistration(RequestRegistration request, CancellationToken ct) =>
        auth.RequestOtpAsync(request.PhoneNumber, request.FullName, "Register", ClientApps.Rider, Ip, ct);

    [HttpPost("login/request-otp"), AllowAnonymous]
    public Task<OtpResponse> RequestLogin(RequestLogin request, CancellationToken ct) =>
        auth.RequestOtpAsync(request.PhoneNumber, null, "Login", request.ClientApp, Ip, ct);

    [HttpPost("register/verify-otp"), AllowAnonymous]
    public Task<TokenResponse> VerifyRegistration(VerifyOtp request, CancellationToken ct) => auth.VerifyAsync(request, "Register", ct);

    [HttpPost("login/verify-otp"), AllowAnonymous]
    public Task<TokenResponse> VerifyLogin(VerifyOtp request, CancellationToken ct) => auth.VerifyAsync(request, "Login", ct);

    [HttpPost("refresh"), AllowAnonymous]
    public Task<TokenResponse> Refresh(RefreshRequest request, CancellationToken ct) => auth.RefreshAsync(request.RefreshToken, ct);

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(ClaimId("sub"), ClaimId("sid"), ct);
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public Task<UserInfo> Me(CancellationToken ct) => auth.MeAsync(ClaimId("sub"), ct);

    [HttpPatch("me"), Authorize(Roles = "Rider")]
    public Task<UserInfo> UpdateName(UpdateRiderName request, CancellationToken ct) =>
        auth.UpdateRiderNameAsync(ClaimId("sub"), request.FullName, ct);

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private Guid ClaimId(string name) => Guid.TryParse(User.FindFirstValue(name), out var id) ? id :
        throw new AuthException(401, "invalid_token", "Required token claims are missing.");
}
