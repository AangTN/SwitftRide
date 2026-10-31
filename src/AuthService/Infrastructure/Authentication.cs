using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Infrastructure;

public sealed class DevelopmentOtpSender(IHostEnvironment environment, OtpSettings settings) : IOtpSender
{
    public bool IsAvailable => environment.IsDevelopment() && settings.UseDevelopmentOtp;
    public string CreateCode() => IsAvailable ? "123456" : throw new InvalidOperationException("Development OTP is disabled.");
    public Task SendAsync(string phone, string code, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("No SMS provider configured.");
        return Task.CompletedTask;
    }
}

public sealed class JwtTokenIssuer : ITokenIssuer, IDisposable
{
    private readonly RSA rsa;
    private readonly IConfiguration config;
    private readonly TokenSettings settings;
    public JwtTokenIssuer(IConfiguration config, TokenSettings settings)
    {
        this.config = config; this.settings = settings;
        rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(config["Security:PrivateKeyPath"]
            ?? throw new InvalidOperationException("Security:PrivateKeyPath is required.")));
    }
    public AccessTokenResult Issue(User user, AuthSession session, DateTimeOffset now)
    {
        var expires = now.AddMinutes(settings.AccessMinutes);
        if (expires > session.ExpiresAt) expires = session.ExpiresAt;
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()), new("sid", session.Id.ToString()),
            new("jti", Guid.NewGuid().ToString()),
            new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        var sessionRole = ClientApps.RoleFor(session.ClientApp);
        claims.AddRange(user.UserRoles.Where(r => r.Role.Name == sessionRole)
            .Select(r => new Claim("roles", r.Role.Name)));
        var jwt = new JwtSecurityToken(config["Security:Issuer"] ?? "swiftride-auth",
            config["Security:Audience"] ?? "swiftride-api", claims, now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
    public void Dispose() => rsa.Dispose();
}
