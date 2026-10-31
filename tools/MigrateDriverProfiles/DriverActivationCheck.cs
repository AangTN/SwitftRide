using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Domain;
using SwiftRide.Auth.Infrastructure;
using SwiftRide.Matching.Infrastructure;

internal static class DriverActivationCheck
{
    public static async Task RunAsync(string authConnection, string matchingConnection)
    {
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(authConnection).Options);
        var matching = new MatchingDatabase(new MongoClient(matchingConnection), "MatchingDB");
        var id = Guid.NewGuid();
        var phone = "+8498" + Random.Shared.Next(1000000, 9999999);
        while (await db.Users.AnyAsync(x => x.PhoneNumber == phone)) phone = "+8498" + Random.Shared.Next(1000000, 9999999);
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5000"), Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            db.Users.Add(new User { Id = id, PhoneNumber = phone, FullName = "Integration Driver", PhoneVerifiedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                UserRoles = [new UserRole { UserId = id, RoleId = RoleIds.Driver }] });
            await db.SaveChangesAsync();
            await matching.Profiles.InsertOneAsync(new SwiftRide.Matching.Domain.DriverProfile {
                Id = id.ToString(), LegalName = "Integration Driver", PlateNumber = "TEST-" + id.ToString("N")[..10],
                VehicleName = "Test vehicle", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            var loginChallenge = await Post<OtpResponse>(client, "api/auth/login/request-otp", new { phoneNumber = phone, clientApp = "DriverApp" });
            var driver = await Post<TokenResponse>(client, "api/auth/login/verify-otp", new { loginChallenge.ChallengeId, otp = "123456", clientApp = "DriverApp" });
            Check(driver.User.Roles.SequenceEqual(["Driver"]), "Initial user must be Driver-only.");
            var request = await Post<OtpResponse>(client, "api/auth/register/request-otp", new { phoneNumber = phone, fullName = "Tên khách riêng" });
            db.ChangeTracker.Clear();
            Check((await db.Users.FindAsync(id))!.FullName == "Integration Driver", "Registration request must not change name before OTP.");
            var rider = await Post<TokenResponse>(client, "api/auth/register/verify-otp", new { request.ChallengeId, otp = "123456", clientApp = "RiderApp" });
            Check(rider.User.Id == id && rider.User.Roles.SequenceEqual(["Driver", "Rider"]), "Activation must preserve user ID and add only Rider.");
            using var crossApp = new HttpRequestMessage(HttpMethod.Get, "api/matching/drivers/me");
            crossApp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rider.AccessToken);
            using var crossAppResponse = await client.SendAsync(crossApp);
            Check(crossAppResponse.StatusCode == HttpStatusCode.Forbidden, "Rider token must not grant Driver access on a dual-role account.");
            using var patch = new HttpRequestMessage(HttpMethod.Patch, "api/auth/me") { Content = JsonContent.Create(new { fullName = "Tên khách đã đổi" }) };
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rider.AccessToken);
            using var updated = await client.SendAsync(patch);
            Check(updated.StatusCode == HttpStatusCode.OK, "Rider name edit failed.");
            var updatedUser = (await updated.Content.ReadFromJsonAsync<UserInfo>())!;
            Check(updatedUser.FullName == "Tên khách đã đổi", "Rider name was not updated.");
            var refreshedDriver = await Post<TokenResponse>(client, "api/auth/refresh", new { driver.RefreshToken });
            using var driverEdit = new HttpRequestMessage(HttpMethod.Patch, "api/auth/me") { Content = JsonContent.Create(new { fullName = "Forbidden edit" }) };
            driverEdit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedDriver.AccessToken);
            using var driverEditResponse = await client.SendAsync(driverEdit);
            Check(driverEditResponse.StatusCode == HttpStatusCode.Forbidden, "Refreshed Driver token must not grant Rider access on a dual-role account.");
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "api/matching/drivers/me");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", driver.AccessToken);
            using var profileResponse = await client.SendAsync(profileRequest);
            profileResponse.EnsureSuccessStatusCode();
            var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
            Check(profile.GetProperty("legalName").GetString() == "Integration Driver", "Rider name edit changed driver legal name.");
            Check(profile.GetProperty("driverId").GetString() == id.ToString(), "Matching returned another user's profile.");
            Console.WriteLine("PASS: Driver login, Rider activation on the same user, app-scoped tokens and refresh, customer rename, and independent Matching legal name through Gateway.");
        }
        finally
        {
            // Only this run's newly generated disposable identity is removed; seed/user data is untouched.
            await matching.Profiles.DeleteOneAsync(x => x.Id == id.ToString());
            await matching.Drivers.DeleteOneAsync(x => x.Id == id.ToString());
            await db.RefreshTokens.Where(x => db.AuthSessions.Where(s => s.UserId == id).Select(s => s.Id).Contains(x.SessionId)).ExecuteDeleteAsync();
            await db.AuthSessions.Where(x => x.UserId == id).ExecuteDeleteAsync();
            await db.OtpChallenges.Where(x => x.PhoneNumber == phone).ExecuteDeleteAsync();
            await db.UserRoles.Where(x => x.UserId == id).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{path}: HTTP {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
