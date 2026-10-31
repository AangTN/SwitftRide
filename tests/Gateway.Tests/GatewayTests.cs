using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.ServiceDefaults;
using Xunit;

namespace SwiftRide.Gateway.Tests;

public sealed class GatewayTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftride-gateway-" + Guid.NewGuid().ToString("N"));
    private readonly RSA signingKey = RSA.Create(2048);
    private WebApplication backend = null!;
    private GatewayFactory factory = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var publicKey = Path.Combine(directory, "public.pem");
        await File.WriteAllTextAsync(publicKey, signingKey.ExportSubjectPublicKeyInfoPem());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PublicKeyPath"] = publicKey, ["Logging:LogLevel:Default"] = "Warning"
        });
        builder.Services.AddServiceDefaults(builder.Configuration, false, broker: false);
        backend = builder.Build();
        backend.Urls.Add("http://127.0.0.1:0");
        backend.UseServiceDefaults();
        backend.MapPost("/api/auth/login/request-otp", () => Results.Ok(new { challengeId = Guid.NewGuid() })).AllowAnonymous();
        backend.MapGet("/api/auth/me", (HttpContext c) => Results.Ok(new { correlation = c.TraceIdentifier })).RequireAuthorization();
        backend.MapGet("/api/trips/probe", () => Results.Ok()).RequireAuthorization(p => p.RequireRole("Driver"));
        await backend.StartAsync();
        factory = new GatewayFactory(publicKey, backend.Urls.Single());
        client = factory.CreateClient();
    }

    private string Token(string role)
    {
        var jwt = new JwtSecurityToken("swiftride-auth", "swiftride-api",
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("sid", Guid.NewGuid().ToString()), new Claim("roles", role)],
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(15),
            new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
    private async Task<HttpResponseMessage> Get(string path, string? role = null, string? correlation = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (role is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
        if (correlation is not null) request.Headers.Add("X-Correlation-ID", correlation);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Public_auth_routes_forward_without_token_but_profile_requires_authentication()
    {
        using var response = await client.PostAsync("/api/auth/login/request-otp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var denied = await Get("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/auth/me", "Rider")).StatusCode);
    }

    [Fact]
    public async Task Backend_enforces_role_even_after_gateway_validates_jwt()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/trips/probe", "Rider")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/trips/probe", "Driver")).StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_preserved_across_gateway_and_backend()
    {
        var correlation = Guid.NewGuid().ToString();
        using var response = await Get("/api/auth/me", "Rider", correlation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlation, response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Contains(correlation, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gateway_readiness_fails_when_backend_is_down_but_liveness_stays_healthy()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await backend.StopAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    public async Task DisposeAsync()
    {
        client.Dispose(); await factory.DisposeAsync(); await backend.DisposeAsync(); signingKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class GatewayFactory(string publicKey, string backendAddress) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
            {
                var values = new Dictionary<string, string?> { ["Security:PublicKeyPath"] = publicKey,
                    ["Logging:LogLevel:Default"] = "Warning" };
                foreach (var service in new[] { "auth", "trip", "matching", "payment" })
                    values[$"ReverseProxy:Clusters:{service}:Destinations:primary:Address"] = backendAddress;
                config.AddInMemoryCollection(values);
            });
            return base.CreateHost(builder);
        }
    }
}
