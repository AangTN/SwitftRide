using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Application;

public sealed partial class AuthService(IAuthStore store, IOtpSender otpSender, ITokenIssuer tokens,
    OtpSettings otp, TokenSettings tokenSettings, TimeProvider clock)
{
    public static string NormalizePhone(string phone)
    {
        var normalized = phone.Trim();
        if (normalized.StartsWith('0')) normalized = "+84" + normalized[1..];
        if (!VietnamPhone().IsMatch(normalized))
            throw new AuthException(400, "invalid_phone", "Use a valid Vietnamese mobile number: 0xxxxxxxxx or +84xxxxxxxxx.");
        return normalized;
    }

    [GeneratedRegex(@"^\+84[35789][0-9]{8}$")]
    private static partial Regex VietnamPhone();

    public async Task<OtpResponse> RequestOtpAsync(string phone, string? name, string purpose, string clientApp, string ip, CancellationToken ct)
    {
        if (!otpSender.IsAvailable)
            throw new AuthException(503, "otp_unavailable", "An SMS provider has not been configured.");
        phone = NormalizePhone(phone);
        var requiredRole = ClientApps.RoleFor(clientApp)
            ?? throw new AuthException(400, "invalid_client", "Unknown client application.");
        if (purpose == "Register" && clientApp != ClientApps.Rider)
            throw new AuthException(400, "invalid_client", "Rider registration requires RiderApp.");
        name = name?.Trim();
        if (purpose == "Register" && string.IsNullOrWhiteSpace(name))
            throw new AuthException(400, "invalid_name", "Full name is required.");
        await using var tx = await store.BeginAsync(ct);
        // Serializes cooldown, pair quota and challenge replacement across all nodes.
        await store.LockAsync("otp-phone:" + phone, ct);
        var now = UtcNow();
        var user = await store.FindUserByPhoneAsync(phone, ct);
        if (purpose == "Register" && user is not null && user.UserRoles.Any(x => x.Role.Name == RoleNames.Rider))
            throw new AuthException(409, "phone_registered", "This phone number is already registered.");
        if (purpose == "Login" && user is null)
            throw new AuthException(404, "registration_required", "Register this phone number first.");
        if (user is { Status: not "Active" })
            throw new AuthException(403, "account_blocked", "This account is blocked.");
        if (purpose == "Login" && !user!.UserRoles.Any(x => x.Role.Name == requiredRole))
            throw new AuthException(403, clientApp == ClientApps.Rider ? "rider_activation_required" : "role_required",
                "This account does not have access to the requested application.");
        var latest = await store.LatestChallengeAsync(phone, purpose, ct);
        if (latest is not null && latest.CreatedAt.AddSeconds(otp.ResendSeconds) > now)
            throw new AuthException(429, "otp_cooldown", "Please wait before requesting another code.");
        if (await store.CountIpPhoneRequestsAsync(ip, phone, now.AddHours(-1), ct) >= otp.RequestsPerIpPhonePerHour)
            throw new AuthException(429, "otp_rate_limited", "Too many OTP requests. Please try again later.");
        await store.InvalidateChallengesAsync(phone, purpose, now, ct);
        var code = otpSender.CreateCode();
        var challenge = new OtpChallenge
        {
            PhoneNumber = phone, Purpose = purpose, ClientApp = clientApp, RegistrationFullName = name, RequestIp = ip,
            CreatedAt = now, ExpiresAt = now.AddSeconds(otp.LifetimeSeconds)
        };
        challenge.CodeHash = HashOtp(challenge, code);
        store.Add(challenge);
        await store.SaveAsync(ct);
        await otpSender.SendAsync(phone, code, ct);
        await tx.CommitAsync(ct);
        return new(challenge.Id, otp.LifetimeSeconds, otp.ResendSeconds);
    }

    public async Task<TokenResponse> VerifyAsync(VerifyOtp request, string purpose, CancellationToken ct)
    {
        if (!otpSender.IsAvailable)
            throw new AuthException(503, "otp_unavailable", "An SMS provider has not been configured.");
        if (request.ClientApp is not ("RiderApp" or "DriverApp" or "AdminApp"))
            throw new AuthException(400, "invalid_client", "Unknown client application.");
        await using var tx = await store.BeginAsync(ct);
        await store.LockAsync("challenge:" + request.ChallengeId, ct);
        var challenge = await store.FindChallengeAsync(request.ChallengeId, ct);
        var now = UtcNow();
        if (challenge is null || challenge.Purpose != purpose || challenge.ConsumedAt is not null ||
            challenge.InvalidatedAt is not null || challenge.ExpiresAt <= now || challenge.AttemptCount >= otp.MaxAttempts)
            throw new AuthException(400, "invalid_challenge", "The OTP request is invalid, expired, used or locked.");
        await store.LockAsync("otp-phone:" + challenge.PhoneNumber, ct);
        // Reload after the phone lock: a concurrent resend may have invalidated this challenge.
        challenge = await store.FindChallengeAsync(request.ChallengeId, ct);
        if (challenge is null || challenge.InvalidatedAt is not null || challenge.ConsumedAt is not null)
            throw new AuthException(400, "invalid_challenge", "The OTP request is no longer valid.");
        if (challenge.ClientApp != request.ClientApp)
            throw new AuthException(400, "invalid_client", "The client application does not match the OTP request.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash),
                Convert.FromHexString(HashOtp(challenge, request.Otp))))
        {
            challenge.AttemptCount++;
            if (challenge.AttemptCount >= otp.MaxAttempts) challenge.InvalidatedAt = now;
            await store.SaveAsync(ct);
            await tx.CommitAsync(ct); // Persist attempts before returning an error.
            throw new AuthException(400, challenge.InvalidatedAt is null ? "invalid_otp" : "otp_attempts_exceeded",
                challenge.InvalidatedAt is null ? "The OTP is incorrect." : "Request a new OTP; too many incorrect attempts.");
        }
        var user = await store.FindUserByPhoneAsync(challenge.PhoneNumber, ct);
        if (purpose == "Register")
        {
            if (challenge.ClientApp != ClientApps.Rider)
                throw new AuthException(400, "invalid_client", "Rider registration requires RiderApp.");
            if (user is not null && user.Status != "Active")
                throw new AuthException(403, "account_blocked", "This account is blocked.");
            if (user is not null && user.UserRoles.Any(x => x.Role.Name == RoleNames.Rider))
                throw new AuthException(409, "phone_registered", "This phone number is already registered as a rider.");
            var isNew = user is null;
            user ??= new User { PhoneNumber = challenge.PhoneNumber, CreatedAt = now };
            user.FullName = challenge.RegistrationFullName!;
            user.PhoneVerifiedAt = now;
            user.UpdatedAt = now;
            var riderRole = await store.FindRoleByNameAsync(RoleNames.Rider, ct)
                ?? throw new InvalidOperationException("The Rider role has not been initialized.");
            user.UserRoles.Add(new() { UserId = user.Id, RoleId = riderRole.Id, Role = riderRole });
            if (isNew) store.Add(user);
        }
        if (user is null) throw new AuthException(404, "registration_required", "Register this phone number first.");
        if (user.Status != "Active") throw new AuthException(403, "account_blocked", "This account is blocked.");
        challenge.ConsumedAt = now;
        var session = new AuthSession { UserId = user.Id, ClientApp = challenge.ClientApp,
            DeviceName = request.DeviceName, CreatedAt = now, ExpiresAt = now.AddDays(tokenSettings.SessionDays) };
        store.Add(session);
        var (refresh, raw) = NewRefresh(session, now);
        store.Add(refresh);
        var result = Response(user, session, raw, now);
        await store.SaveAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<TokenResponse> RefreshAsync(string raw, CancellationToken ct)
    {
        var hash = HashRefresh(raw);
        await using var tx = await store.BeginAsync(ct);
        // Discovery is not authorization. The session lock below serializes ALL generations, logout and reuse.
        var found = await store.FindRefreshAsync(hash, ct);
        if (found is null) throw InvalidRefresh();
        await store.LockAsync("session:" + found.SessionId, ct);
        var refresh = await store.FindRefreshAsync(hash, ct);
        var session = await store.FindSessionAsync(found.SessionId, ct);
        var now = UtcNow();
        if (refresh is null || session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
            throw InvalidRefresh();
        if (refresh.UsedAt is not null)
        {
            session.RevokedAt = now;
            session.RevocationReason = "RefreshTokenReuse";
            await store.SaveAsync(ct);
            await tx.CommitAsync(ct); // Do not roll back the revocation when returning 401.
            throw InvalidRefresh();
        }
        if (refresh.ExpiresAt <= now) throw InvalidRefresh();
        var user = await store.FindUserAsync(session.UserId, ct);
        if (user is null || user.Status != "Active")
        {
            session.RevokedAt = now;
            session.RevocationReason = "UserBlocked";
            await store.SaveAsync(ct);
            await tx.CommitAsync(ct);
            throw InvalidRefresh();
        }
        var (replacement, replacementRaw) = NewRefresh(session, now);
        refresh.UsedAt = now;
        refresh.ReplacedByTokenId = replacement.Id;
        store.Add(replacement);
        var result = Response(user, session, replacementRaw, now);
        await store.SaveAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await using var tx = await store.BeginAsync(ct);
        await store.LockAsync("session:" + sessionId, ct);
        var session = await store.FindSessionAsync(sessionId, ct);
        if (session is null || session.UserId != userId)
            throw new AuthException(401, "invalid_session", "Invalid session.");
        if (session.RevokedAt is null)
        {
            session.RevokedAt = UtcNow();
            session.RevocationReason = "Logout";
            await store.SaveAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<UserInfo> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await store.FindUserAsync(userId, ct);
        if (user is null || user.Status != "Active")
            throw new AuthException(403, "account_unavailable", "The account is unavailable.");
        return Info(user);
    }

    public async Task<UserInfo> UpdateRiderNameAsync(Guid userId, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new AuthException(400, "invalid_name", "Full name is required.");
        await using var tx = await store.BeginAsync(ct);
        var found = await store.FindUserAsync(userId, ct);
        if (found is null) throw new AuthException(403, "account_unavailable", "Account is unavailable.");
        await store.LockAsync("otp-phone:" + found.PhoneNumber, ct);
        var user = await store.FindUserAsync(userId, ct);
        if (user is null || user.Status != "Active" || !user.UserRoles.Any(x => x.Role.Name == RoleNames.Rider))
            throw new AuthException(403, "rider_required", "Rider access is required.");
        user.FullName = name;
        user.UpdatedAt = UtcNow();
        await store.SaveAsync(ct);
        await tx.CommitAsync(ct);
        return Info(user);
    }

    // PostgreSQL timestamps have microsecond precision; responses and persisted expiry must agree exactly.
    private DateTimeOffset UtcNow()
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    private string HashOtp(OtpChallenge c, string code) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(otp.HmacSecret), Encoding.UTF8.GetBytes($"{c.Id:N}:{c.PhoneNumber}:{c.Purpose}:{code}")));
    private static string HashRefresh(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private static AuthException InvalidRefresh() => new(401, "invalid_refresh_token", "The refresh token is invalid or its session has been revoked.");
    private static (RefreshToken Token, string Raw) NewRefresh(AuthSession session, DateTimeOffset now)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return (new() { SessionId = session.Id, TokenHash = HashRefresh(raw), CreatedAt = now, ExpiresAt = session.ExpiresAt }, raw);
    }
    private TokenResponse Response(User user, AuthSession session, string raw, DateTimeOffset now)
    {
        var access = tokens.Issue(user, session, now);
        return new(access.Token, raw, "Bearer", access.ExpiresAt, session.ExpiresAt, Info(user));
    }
    private static UserInfo Info(User u) => new(u.Id, u.PhoneNumber, u.FullName,
        u.UserRoles.Select(r => r.Role.Name).Order().ToArray());
}
