using System.ComponentModel.DataAnnotations;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Application;

public sealed record RequestRegistration([Required, StringLength(150, MinimumLength = 1)] string FullName,
    [Required, StringLength(20)] string PhoneNumber);
public sealed record RequestLogin([Required, StringLength(20)] string PhoneNumber,
    [Required, RegularExpression("^(RiderApp|DriverApp|AdminApp)$")] string ClientApp);
public sealed record VerifyOtp(Guid ChallengeId,
    [Required, RegularExpression("^[0-9]{6}$")] string Otp,
    [Required, RegularExpression("^(RiderApp|DriverApp|AdminApp)$")] string ClientApp,
    [StringLength(200)] string? DeviceName = null);
public sealed record RefreshRequest([Required, StringLength(256, MinimumLength = 32)] string RefreshToken);
public sealed record OtpResponse(Guid ChallengeId, int ExpiresInSeconds, int RetryAfterSeconds);
public sealed record UserInfo(Guid Id, string PhoneNumber, string FullName, string[] Roles);
public sealed record UpdateRiderName([Required, StringLength(150, MinimumLength = 1)] string FullName);
public sealed record TokenResponse(string AccessToken, string RefreshToken, string TokenType,
    DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt, UserInfo User);
public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed class AuthException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class OtpSettings
{
    public bool UseDevelopmentOtp { get; set; }
    public int LifetimeSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public int ResendSeconds { get; set; } = 60;
    public int RequestsPerIpPhonePerHour { get; set; } = 5;
    public string HmacSecret { get; set; } = "";
}

public sealed class TokenSettings
{
    public int AccessMinutes { get; set; } = 15;
    public int SessionDays { get; set; } = 30;
}

public interface IOtpSender
{
    bool IsAvailable { get; }
    string CreateCode();
    Task SendAsync(string phone, string code, CancellationToken ct);
}

public interface ITokenIssuer
{
    AccessTokenResult Issue(User user, AuthSession session, DateTimeOffset now);
}

public interface IAuthTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public interface IAuthStore
{
    Task<IAuthTransaction> BeginAsync(CancellationToken ct);
    Task LockAsync(string resource, CancellationToken ct);
    Task<User?> FindUserByPhoneAsync(string phone, CancellationToken ct);
    Task<User?> FindUserAsync(Guid id, CancellationToken ct);
    Task<Role?> FindRoleByNameAsync(string name, CancellationToken ct);
    Task<OtpChallenge?> FindChallengeAsync(Guid id, CancellationToken ct);
    Task<OtpChallenge?> LatestChallengeAsync(string phone, string purpose, CancellationToken ct);
    Task<int> CountIpPhoneRequestsAsync(string ip, string phone, DateTimeOffset since, CancellationToken ct);
    Task InvalidateChallengesAsync(string phone, string purpose, DateTimeOffset now, CancellationToken ct);
    Task<RefreshToken?> FindRefreshAsync(string hash, CancellationToken ct);
    Task<AuthSession?> FindSessionAsync(Guid id, CancellationToken ct);
    void Add(User user);
    void Add(OtpChallenge challenge);
    void Add(AuthSession session);
    void Add(RefreshToken token);
    Task SaveAsync(CancellationToken ct);
}
