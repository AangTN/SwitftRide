using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Auth.Application;

namespace SwiftRide.Auth.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES") is null)
            Skip = "Set SWIFTRIDE_TEST_POSTGRES to run concurrency tests against real PostgreSQL.";
    }
}

public sealed class PostgresConcurrencyTests
{
    [PostgresFact]
    public async Task Concurrent_requests_cannot_exceed_fifth_send_for_the_same_ip_phone_pair()
    {
        await using var factory = new AuthFactory(); await factory.InitializeAsync();
        await factory.InDb(async db =>
        {
            var created = factory.Clock.GetUtcNow().AddMinutes(-10);
            db.OtpChallenges.AddRange(Enumerable.Range(0, 4).Select(i => new SwiftRide.Auth.Domain.OtpChallenge
            {
                PhoneNumber = "+84900000001", RequestIp = "192.0.2.1", Purpose = "Login", ClientApp = "RiderApp",
                CreatedAt = created.AddSeconds(i), ExpiresAt = created.AddSeconds(i + 300)
            }));
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "192.0.2.1");
        var results = await Task.WhenAll(new[] { "0900000001", "+84900000001" }.Select(phone =>
            client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = phone, clientApp = "RiderApp" })));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var sixth = await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Contains("otp_rate_limited", await sixth.Content.ReadAsStringAsync());
        await factory.InDb(async db => Assert.Equal(5, await db.OtpChallenges.CountAsync()));
    }

    [PostgresFact]
    public async Task Concurrent_refresh_has_one_winner_and_reuse_revokes_the_winning_chain()
    {
        await using var factory = new AuthFactory(); await factory.InitializeAsync();
        using var client = factory.CreateClient();
        var challenge = (await (await client.PostAsJsonAsync("/api/auth/login/request-otp", new { phoneNumber = "0900000001", clientApp = "RiderApp" }))
            .Content.ReadFromJsonAsync<OtpResponse>())!;
        var tokens = (await (await client.PostAsJsonAsync("/api/auth/login/verify-otp",
            new { challengeId = challenge.ChallengeId, otp = "123456", clientApp = "RiderApp" })).Content.ReadFromJsonAsync<TokenResponse>())!;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken })));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Unauthorized);
        var winner = (await results.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = winner.RefreshToken })).StatusCode);
        await factory.InDb(async db =>
        {
            Assert.Equal(2, await db.RefreshTokens.CountAsync());
            Assert.Equal("RefreshTokenReuse", (await db.AuthSessions.SingleAsync()).RevocationReason);
        });
    }

    [PostgresFact]
    public async Task Concurrent_registration_verification_consumes_otp_once_and_creates_one_user()
    {
        await using var factory = new AuthFactory(); await factory.InitializeAsync();
        using var client = factory.CreateClient();
        var challenge = (await (await client.PostAsJsonAsync("/api/auth/register/request-otp",
            new { phoneNumber = "0912345678", fullName = "Concurrent Rider" })).Content.ReadFromJsonAsync<OtpResponse>())!;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/auth/register/verify-otp",
            new { challengeId = challenge.ChallengeId, otp = "123456", clientApp = "RiderApp" })));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
        await factory.InDb(async db =>
        {
            Assert.Equal(1, await db.Users.CountAsync(x => x.PhoneNumber == "+84912345678"));
            Assert.Equal(1, await db.AuthSessions.CountAsync());
        });
    }

    [PostgresFact]
    public async Task Concurrent_normalized_otp_requests_obey_the_same_cooldown()
    {
        await using var factory = new AuthFactory(); await factory.InitializeAsync();
        using var client = factory.CreateClient();
        var results = await Task.WhenAll(new[] { "0912345678", "+84912345678" }.Select(phone =>
            client.PostAsJsonAsync("/api/auth/register/request-otp", new { phoneNumber = phone, fullName = "Rider" })));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        await factory.InDb(async db => Assert.Equal(1, await db.OtpChallenges.CountAsync()));
    }
}
