using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SwiftRide.Payment.Infrastructure;
using MassTransit;
using SwiftRide.Payment.Api;

namespace SwiftRide.Payment.Tests;

public sealed class PaymentFactory : WebApplicationFactory<Program>
{
    public bool Messaging { get; init; }
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftride-pay-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection sqlite = new("Data Source=:memory:");
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _securityKey;
    private string? pgConnection;

    public string PrivateKeyPath => Path.Combine(directory, "private.pem");
    public string PublicKeyPath => Path.Combine(directory, "public.pem");
    public bool UsesPostgres => Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES") is not null;

    public PaymentFactory()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PrivateKeyPath, _rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(PublicKeyPath, _rsa.ExportSubjectPublicKeyInfoPem());
        _securityKey = new RsaSecurityKey(_rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        sqlite.Open();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PublicKeyPath"] = PublicKeyPath,
            ["ConnectionStrings:PaymentDb"] = "Host=localhost;Database=unused",
            ["RabbitMq:Enabled"] = "false",
            ["Seed:Enabled"] = "false",
            ["Database:MigrateOnStartup"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            if (Messaging)
            {
                services.AddSingleton<PaymentResultInbox>();
                services.AddScoped<PaymentOutboxDispatcher>();
                services.AddMassTransit(x =>
                {
                    x.AddConsumer<PaymentRequestedConsumer>();
                    x.AddConsumer<PaymentResultCollector>();
                    x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter($"payment-test-{Guid.NewGuid():N}", false));
                    if (ushort.TryParse(Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_RABBITMQ_PORT"), out var rabbitPort))
                        x.UsingRabbitMq((context, cfg) =>
                        {
                            cfg.Host("127.0.0.1", rabbitPort, "/", host =>
                            {
                                host.Username("payment_test");
                                host.Password("payment_test_only");
                            });
                            cfg.ConfigureEndpoints(context);
                        });
                    else x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
                });
            }
            services.RemoveAll<DbContextOptions<PaymentDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<PaymentDbContext>>();
            services.RemoveAll<PaymentDbContext>();

            if (UsesPostgres)
            {
                var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES"))
                { SearchPath = schema };
                pgConnection = connection.ConnectionString;
                services.AddDbContext<PaymentDbContext>(o => o.UseNpgsql(pgConnection));
            }
            else
            {
                services.AddDbContext<PaymentDbContext>(o => o.UseSqlite(sqlite));
                services.AddScoped<PaymentDbContext>(sp => new SqlitePaymentContext(sp.GetRequiredService<DbContextOptions<PaymentDbContext>>()));
            }
        });
    }

    public async Task InitializeAsync(bool seed = true)
    {
        _ = CreateClient();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        if (UsesPostgres)
        {
            await using var connection = new NpgsqlConnection(pgConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }

        if (seed)
        {
            await PaymentSeed.RunAsync(db);
        }
    }

    public string CreateToken(Guid userId, string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));

        var token = new JwtSecurityToken(
            issuer: "swiftride-auth",
            audience: "swiftride-api",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: new SigningCredentials(_securityKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient CreateAuthorizedClient(Guid userId, params string[] roles)
    {
        var client = CreateClient();
        var token = CreateToken(userId, roles);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await sqlite.DisposeAsync();
        _rsa.Dispose();
        if (pgConnection is not null)
        {
            await using var connection = new NpgsqlConnection(pgConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearPool(connection);
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SqlitePaymentContext(DbContextOptions<PaymentDbContext> options) : PaymentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        v => v.UtcTicks,
                        v => new DateTimeOffset(v, TimeSpan.Zero)));
                }
            }
        }
    }
}
