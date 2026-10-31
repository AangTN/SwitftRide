using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.Matching.Application;
using SwiftRide.Matching.Domain;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class DriverProfileTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftride-matching-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RSA key = RSA.Create(2048);
    private ProfileFactory factory = null!;
    private HttpClient client = null!;
    private const string DriverId = "22222222-2222-2222-2222-222222222222";
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var publicKey = Path.Combine(directory, "public.pem");
        await File.WriteAllTextAsync(publicKey, key.ExportSubjectPublicKeyInfoPem());
        factory = new ProfileFactory(publicKey);
        client = factory.CreateClient();
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); key.Dispose(); Directory.Delete(directory, true); }
    private Task<HttpResponseMessage> Get(string? role = null, string id = DriverId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/matching/drivers/me");
        if (role is not null) {
            var token = new JwtSecurityToken("swiftride-auth", "swiftride-api", [new Claim("sub", id), new Claim("roles", role)], DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        return client.SendAsync(request);
    }
    [Fact] public async Task Anonymous_is_rejected() => Assert.Equal(HttpStatusCode.Unauthorized, (await Get()).StatusCode);
    [Fact] public async Task Rider_is_forbidden() => Assert.Equal(HttpStatusCode.Forbidden, (await Get("Rider")).StatusCode);
    [Fact] public async Task Missing_driver_profile_returns_404() => Assert.Equal(HttpStatusCode.NotFound, (await Get("Driver", Guid.NewGuid().ToString())).StatusCode);
    [Fact] public async Task Profile_is_selected_from_token_and_uses_its_own_legal_name()
    {
        var response = await Get("Driver");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(DriverId, json.GetProperty("driverId").GetString());
        Assert.Equal("Tên tài xế đã xác minh", json.GetProperty("legalName").GetString());
        Assert.Equal("59A1-12345", json.GetProperty("plateNumber").GetString());
    }
    private sealed class ProfileFactory(string publicKey) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Security:PublicKeyPath"] = publicKey, ["RabbitMq:Enabled"] = "false", ["Seed:Enabled"] = "false",
                ["Database:InitializeOnStartup"] = "false", ["ConnectionStrings:MatchingDb"] = "mongodb://localhost:27017/MatchingTest",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => { services.RemoveAll<IDriverProfileReader>(); services.AddSingleton<IDriverProfileReader, FakeProfiles>(); });
        }
    }
    private sealed class FakeProfiles : IDriverProfileReader
    {
        public Task<DriverProfile?> FindAsync(string userId, CancellationToken ct) => Task.FromResult(userId == DriverId ? new DriverProfile {
            Id = DriverId, LegalName = "Tên tài xế đã xác minh", PlateNumber = "59A1-12345", VehicleName = "Honda Wave Alpha"
        } : null);
    }
}
