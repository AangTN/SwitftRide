using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Infrastructure;

namespace SwiftRide.Auth.Tests;

public sealed class AuthFlowTests : IAsyncLifetime
{
    private readonly AuthFactory factory = new();
    private HttpClient client = null!;
    public async Task InitializeAsync() { await factory.InitializeAsync(); client = factory.CreateClient(); }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); }

    private async Task<OtpResponse> Request(string phone = "0900000001", bool register = false, string app = "RiderApp")
    {
        var response = await client.PostAsJsonAsync($"/api/auth/{(register ? "register" : "login")}/request-otp",
            new { phoneNumber = phone, fullName = "New Rider", clientApp = app, roles = new[] { "Admin", "Driver" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("devOtp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", json);
        return (await response.Content.ReadFromJsonAsync<OtpResponse>())!;
    }
    private Task<HttpResponseMessage> Verify(Guid id, string otp = "123456", bool register = false, string app = "RiderApp") =>
        client.PostAsJsonAsync($"/api/auth/{(register ? "register" : "login")}/verify-otp", new { challengeId = id, otp, clientApp = app });
    private async Task<TokenResponse> Login(string phone = "0900000001", string app = "RiderApp")
    {
        var challenge = await Request(phone, app: app);
        var response = await Verify(challenge.ChallengeId, app: app);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private Task<HttpResponseMessage> Refresh(string token) => client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token });
    private async Task<HttpResponseMessage> Authorized(string path, string token, bool post = false)
    {
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Login_requires_the_app_role_before_creating_a_challenge()
    {
        foreach (var (phone, app, error) in new[] {
            ("0900000002", "RiderApp", "rider_activation_required"),
            ("0900000001", "DriverApp", "role_required"),
            ("0900000001", "AdminApp", "role_required") })
        {
            var response = await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = phone, clientApp = app });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(error, await response.Content.ReadAsStringAsync());
        }
        await factory.InDb(async db => Assert.Empty(await db.OtpChallenges.ToListAsync()));
        var admin = await Login("0900000003", "AdminApp");
        Assert.Equal(HttpStatusCode.OK, (await Authorized("/test/roles/admin", admin.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Blocked_account_and_missing_or_unknown_client_cannot_request_login_otp()
    {
        await factory.InDb(async db => {
            (await db.Users.SingleAsync(x => x.PhoneNumber == "+84900000001")).Status = "Blocked";
            await db.SaveChangesAsync();
        });
        var blocked = await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("account_blocked", await blocked.Content.ReadAsStringAsync());
        foreach (var app in new string?[] { null, "OtherApp" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login/request-otp",
                new { phoneNumber = "0900000001", clientApp = app })).StatusCode);
        await factory.InDb(async db => Assert.Empty(await db.OtpChallenges.ToListAsync()));
    }

    [Fact]
    public async Task Challenge_binds_app_and_dual_role_tokens_and_refresh_remain_scoped()
    {
        await factory.InDb(async db => {
            db.UserRoles.Add(new() { UserId = DevelopmentAccounts.DriverId, RoleId = SwiftRide.Auth.Domain.RoleIds.Rider });
            await db.SaveChangesAsync();
        });
        var challenge = await Request("0900000002");
        var mismatch = await Verify(challenge.ChallengeId, app: "DriverApp");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("invalid_client", await mismatch.Content.ReadAsStringAsync());
        await factory.InDb(async db => {
            var saved = await db.OtpChallenges.FindAsync(challenge.ChallengeId);
            Assert.Equal("RiderApp", saved!.ClientApp);
            Assert.Equal(0, saved.AttemptCount);
            Assert.Null(saved.ConsumedAt);
            Assert.Empty(await db.AuthSessions.ToListAsync());
        });
        var rider = (await (await Verify(challenge.ChallengeId)).Content.ReadFromJsonAsync<TokenResponse>())!;
        // Backdate cooldown history rather than moving JWT nbf ahead of the validator's real clock.
        await factory.InDb(async db => {
            (await db.OtpChallenges.FindAsync(challenge.ChallengeId))!.CreatedAt -= TimeSpan.FromSeconds(61);
            await db.SaveChangesAsync();
        });
        var driver = await Login("0900000002", "DriverApp");
        foreach (var (original, role, allowed, denied) in new[] {
            (rider, "Rider", "rider", "driver"), (driver, "Driver", "driver", "rider") })
        {
            var refreshed = (await (await Refresh(original.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>())!;
            foreach (var token in new[] { original, refreshed })
            {
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
                Assert.Equal([role], jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value));
                Assert.Equal(HttpStatusCode.OK, (await Authorized("/test/roles/" + allowed, token.AccessToken)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await Authorized("/test/roles/" + denied, token.AccessToken)).StatusCode);
            }
        }
    }

    [Fact]
    public async Task Login_verification_does_not_reject_a_role_change_but_still_checks_active_status()
    {
        var challenge = await Request();
        await factory.InDb(async db => {
            db.UserRoles.Remove(await db.UserRoles.SingleAsync(x => x.UserId == DevelopmentAccounts.RiderId));
            await db.SaveChangesAsync();
        });
        var response = await Verify(challenge.ChallengeId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken).Claims, c => c.Type == "roles");
        var driverChallenge = await Request("0900000002", app: "DriverApp");
        await factory.InDb(async db => {
            (await db.Users.FindAsync(DevelopmentAccounts.DriverId))!.Status = "Blocked";
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await Verify(driverChallenge.ChallengeId, app: "DriverApp")).StatusCode);
    }

    [Fact]
    public async Task Registration_normalizes_phone_and_only_grants_rider()
    {
        var challenge = await Request("0912345678", true);
        await factory.InDb(async db => Assert.False(await db.Users.AnyAsync(x => x.PhoneNumber == "+84912345678")));
        var response = await Verify(challenge.ChallengeId, register: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("+84912345678", token.User.PhoneNumber);
        Assert.Equal(["Rider"], token.User.Roles);
        Assert.Equal(HttpStatusCode.OK, (await Authorized("/api/auth/me", token.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized("/test/roles/driver", token.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId, register: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register/request-otp",
            new { phoneNumber = "+84912345678", fullName = "Duplicate" })).StatusCode);
    }

    [Fact]
    public async Task Unknown_phone_requires_registration_and_invalid_phone_is_rejected()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0912345678", clientApp = "RiderApp" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "123", clientApp = "RiderApp" })).StatusCode);
    }

    [Fact]
    public async Task Existing_driver_activates_rider_only_after_otp_and_keeps_the_same_user()
    {
        var driver = await Login("0900000002", "DriverApp");
        var request = await client.PostAsJsonAsync("/api/auth/register/request-otp", new { phoneNumber = "0900000002", fullName = "Tên khách riêng" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var challenge = (await request.Content.ReadFromJsonAsync<OtpResponse>())!;
        await factory.InDb(async db => {
            var existing = await db.Users.Include(x => x.UserRoles).SingleAsync(x => x.Id == driver.User.Id);
            Assert.Equal("Driver Demo", existing.FullName);
            Assert.Single(existing.UserRoles);
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId, "000000", register: true)).StatusCode);
        var response = await Verify(challenge.ChallengeId, register: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rider = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(driver.User.Id, rider.User.Id);
        Assert.Equal("Tên khách riêng", rider.User.FullName);
        Assert.Equal(["Driver", "Rider"], rider.User.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized("/test/roles/driver", rider.AccessToken)).StatusCode);
        await factory.InDb(async db => {
            await AuthSeed.RunAsync(db);
            Assert.Equal(3, await db.Users.CountAsync());
            Assert.Equal(2, await db.UserRoles.CountAsync(x => x.UserId == driver.User.Id));
            Assert.Equal("Tên khách riêng", (await db.Users.FindAsync(driver.User.Id))!.FullName);
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId, register: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register/request-otp", new { phoneNumber = "0900000002", fullName = "Again" })).StatusCode);
        var updated = await client.PatchAsJsonAsync("/api/auth/me", new { fullName = "Tên mới" });
        Assert.Equal(HttpStatusCode.Unauthorized, updated.StatusCode);
        using var edit = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me") { Content = JsonContent.Create(new { fullName = "Tên mới" }) };
        edit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rider.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(edit)).StatusCode);
        var refreshed = (await (await Refresh(driver.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(["Driver", "Rider"], refreshed.User.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized("/test/roles/rider", refreshed.AccessToken)).StatusCode);
        Assert.Equal("Tên mới", refreshed.User.FullName);
    }

    [Fact]
    public async Task Driver_only_cannot_change_rider_name_or_register_as_driver()
    {
        var driver = await Login("0900000002", "DriverApp");
        using var edit = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me") { Content = JsonContent.Create(new { fullName = "Illegal edit" }) };
        edit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", driver.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(edit)).StatusCode);
        var challenge = await Request("0912345678", true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId, register: true, app: "DriverApp")).StatusCode);
        await factory.InDb(async db => Assert.False(await db.Users.AnyAsync(x => x.PhoneNumber == "+84912345678")));
        Assert.Equal(HttpStatusCode.OK, (await Verify(challenge.ChallengeId, register: true)).StatusCode);
    }

    [Fact]
    public async Task Fifth_wrong_attempt_locks_challenge_without_blocking_account()
    {
        var challenge = await Request();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId, "000000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(challenge.ChallengeId)).StatusCode);
        await factory.InDb(async db =>
        {
            var c = await db.OtpChallenges.SingleAsync(x => x.Id == challenge.ChallengeId);
            Assert.Equal(5, c.AttemptCount); Assert.NotNull(c.InvalidatedAt);
            Assert.Equal("Active", (await db.Users.SingleAsync(x => x.PhoneNumber == "+84900000001")).Status);
        });
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var replacement = await Request();
        Assert.Equal(HttpStatusCode.OK, (await Verify(replacement.ChallengeId)).StatusCode);
    }

    [Fact]
    public async Task Otp_expiry_cooldown_resend_and_purpose_are_enforced()
    {
        var first = await Request();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(first.ChallengeId, register: true)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = await Request();
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(first.ChallengeId)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(second.ChallengeId)).StatusCode);
    }

    [Fact]
    public async Task Ip_phone_request_quota_cannot_be_bypassed_with_resend_and_recovers_after_an_hour()
    {
        for (var i = 0; i < 5; i++) { await Request(); factory.Clock.Advance(TimeSpan.FromSeconds(61)); }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" })).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        await Request();
    }

    [Fact]
    public async Task Refresh_reuse_revokes_all_generations_but_not_other_sessions()
    {
        var first = await Login();
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var other = await Login();
        var r2 = await Refresh(first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var second = (await r2.Content.ReadFromJsonAsync<TokenResponse>())!;
        var r3 = await Refresh(second.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        var third = (await r3.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(first.RefreshTokenExpiresAt, third.RefreshTokenExpiresAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(first.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(third.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(other.RefreshToken)).StatusCode);
        await factory.InDb(async db =>
        {
            Assert.Equal(1, await db.AuthSessions.CountAsync(x => x.RevocationReason == "RefreshTokenReuse"));
            Assert.All(await db.RefreshTokens.ToListAsync(), t => Assert.Equal(64, t.TokenHash.Length));
        });
    }

    [Fact]
    public async Task Logout_only_revokes_current_session()
    {
        var token = await Login();
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var other = await Login();
        Assert.Equal(HttpStatusCode.NoContent, (await Authorized("/api/auth/logout", token.AccessToken, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(token.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(other.RefreshToken)).StatusCode);
        // JWT is stateless and remains valid until its expiry, as documented.
        Assert.Equal(HttpStatusCode.OK, (await Authorized("/api/auth/me", token.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Driver_starts_with_driver_only_and_seed_is_idempotent()
    {
        var token = await Login("0900000002", "DriverApp");
        Assert.Equal(["Driver"], token.User.Roles);
        Assert.Equal(HttpStatusCode.OK, (await Authorized("/test/roles/driver", token.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized("/test/roles/admin", token.AccessToken)).StatusCode);
        await factory.InDb(async db =>
        {
            await AuthSeed.RunAsync(db); await AuthSeed.RunAsync(db);
            Assert.Equal(3, await db.Users.CountAsync()); Assert.Equal(3, await db.UserRoles.CountAsync());
        });
    }

    [Fact]
    public async Task Refresh_uses_current_roles_and_blocks_disabled_users()
    {
        var token = await Login("0900000002", "DriverApp");
        await factory.InDb(async db =>
        {
            db.UserRoles.Remove(await db.UserRoles.SingleAsync(x => x.UserId == token.User.Id && x.Role.Name == "Driver"));
            await db.SaveChangesAsync();
        });
        var refreshed = (await (await Refresh(token.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Empty(refreshed.User.Roles);
        await factory.InDb(async db => { (await db.Users.FindAsync(token.User.Id))!.Status = "Blocked"; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(refreshed.RefreshToken)).StatusCode);
        await factory.InDb(async db => Assert.Equal("UserBlocked", (await db.AuthSessions.SingleAsync()).RevocationReason));
    }

    [Fact]
    public async Task Session_expiry_prevents_refresh()
    {
        var token = await Login(); factory.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(token.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Jwt_validation_rejects_missing_invalid_expired_and_wrong_audience_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized("/api/auth/me", "invalid")).StatusCode);
        using var rsa = RSA.Create(); rsa.ImportFromPem(await File.ReadAllTextAsync(factory.PrivateKeyPath));
        string Signed(string audience, DateTime expires, RSA key) => new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken("swiftride-auth", audience, [], DateTime.UtcNow.AddHours(-2), expires,
                new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized("/api/auth/me", Signed("wrong", DateTime.UtcNow.AddMinutes(5), rsa))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized("/api/auth/me", Signed("swiftride-api", DateTime.UtcNow.AddMinutes(-1), rsa))).StatusCode);
        using var stranger = RSA.Create(2048);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Authorized("/api/auth/me", Signed("swiftride-api", DateTime.UtcNow.AddMinutes(5), stranger))).StatusCode);
    }

    [Fact]
    public async Task Readiness_and_live_endpoints_are_available_without_authentication()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}

public sealed class ProductionGuardTests
{
    [Theory]
    [InlineData("Otp:UseDevelopmentOtp")]
    [InlineData("Seed:Enabled")]
    public async Task Development_features_fail_closed_in_production(string feature)
    {
        await using var factory = new AuthFactory { EnvironmentName = "Production" };
        factory.Overrides["Otp:UseDevelopmentOtp"] = "false";
        factory.Overrides[feature] = "true";
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("cannot be enabled outside Development", error.Message);
    }

    [Fact]
    public async Task Without_sms_provider_production_otp_returns_service_unavailable()
    {
        await using var factory = new AuthFactory { EnvironmentName = "Production" };
        factory.Overrides["Otp:UseDevelopmentOtp"] = "false";
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" })).StatusCode);
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task Pair_quota_does_not_block_other_phones_or_other_ips_and_is_shared_across_purposes()
    {
        await using var factory = new AuthFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        var since = factory.Clock.GetUtcNow().AddMinutes(-10);
        await factory.InDb(async db =>
        {
            db.UserRoles.Add(new() { UserId = DevelopmentAccounts.RiderId, RoleId = SwiftRide.Auth.Domain.RoleIds.Driver });
            db.OtpChallenges.AddRange(Enumerable.Range(0, 5).Select(i => new SwiftRide.Auth.Domain.OtpChallenge
            {
                PhoneNumber = "+84900000001", RequestIp = "192.0.2.1", Purpose = i % 2 == 0 ? "Login" : "Register",
                ClientApp = i % 2 == 0 ? "RiderApp" : "DriverApp", CreatedAt = since.AddSeconds(i), ExpiresAt = since.AddSeconds(i + 300)
            }));
            await db.SaveChangesAsync();
        });
        // No trusted forwarded header: a spoofed header cannot select a different quota.
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "192.0.2.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.9");
        var denied = await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" });
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login/request-otp",
            new { phoneNumber = "0900000001", clientApp = "DriverApp" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register/request-otp",
            new { phoneNumber = "0912345673", fullName = "New Rider" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Remote-Ip");
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "192.0.2.2");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login/request-otp",
            new { phoneNumber = "0900000001", clientApp = "RiderApp" })).StatusCode);
    }
}
