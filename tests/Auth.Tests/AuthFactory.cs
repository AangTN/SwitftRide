using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SwiftRide.Auth.Infrastructure;

namespace SwiftRide.Auth.Tests;

public sealed class MutableClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan time) => now = now.Add(time);
}

public sealed class AuthFactory : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftride-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection sqlite = new("Data Source=:memory:");
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private string? pgConnection;
    public MutableClock Clock { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = [];
    public string EnvironmentName { get; set; } = "Development";
    public string PrivateKeyPath => Path.Combine(directory, "private.pem");
    public bool UsesPostgres => System.Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES") is not null;

    public AuthFactory()
    {
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(2048);
        File.WriteAllText(PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory, "public.pem"), rsa.ExportSubjectPublicKeyInfoPem());
        sqlite.Open();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:PublicKeyPath"] = Path.Combine(directory, "public.pem"),
            ["Security:PrivateKeyPath"] = PrivateKeyPath,
            ["ConnectionStrings:AuthDb"] = "Host=localhost;Database=unused",
            ["RabbitMq:Enabled"] = "false", ["Seed:Enabled"] = "false", ["Database:MigrateOnStartup"] = "false",
            ["Otp:UseDevelopmentOtp"] = "true", ["Otp:HmacSecret"] = "test-only-hmac-secret-with-at-least-32-characters",
            ["Logging:LogLevel:Default"] = "Warning", ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
        }.Concat(Overrides).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Last().Value)));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AuthDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
            services.RemoveAll<AuthDbContext>();
            if (UsesPostgres)
            {
                var connection = new NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES"))
                { SearchPath = schema };
                pgConnection = connection.ConnectionString;
                services.AddDbContext<AuthDbContext>(o => o.UseNpgsql(pgConnection));
            }
            else
            {
                services.AddDbContext<AuthDbContext>(o => o.UseSqlite(sqlite));
                services.AddScoped<AuthDbContext>(sp => new SqliteAuthContext(sp.GetRequiredService<DbContextOptions<AuthDbContext>>()));
            }
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, TestRemoteIpFilter>();
            services.AddControllers().AddApplicationPart(typeof(RoleProbeController).Assembly);
        });
    }

    public async Task InitializeAsync(string? migrationTarget = null, bool seed = true)
    {
        _ = CreateClient();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        if (UsesPostgres)
        {
            await using var connection = new NpgsqlConnection(pgConnection);
            await connection.OpenAsync();
            // Identifier is generated exclusively from Guid.ToString("N"), never from input.
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(migrationTarget);
        }
        else await db.Database.EnsureCreatedAsync();
        if (seed) await AuthSeed.RunAsync(db);
    }

    public async Task InDb(Func<AuthDbContext, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await sqlite.DisposeAsync();
        if (pgConnection is not null)
        {
            await using var connection = new NpgsqlConnection(pgConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearPool(connection);
        }
        Directory.Delete(directory, recursive: true);
    }

    private sealed class SqliteAuthContext(DbContextOptions<AuthDbContext> options) : AuthDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)));
        }
    }
}

// Test-only transport metadata, set before the application's forwarded-header middleware.
internal sealed class TestRemoteIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["X-Test-Remote-Ip"].ToString(), out var ip))
                context.Connection.RemoteIpAddress = ip;
            await nextMiddleware();
        });
        next(app);
    };
}

// Test-only endpoints verify actual ASP.NET role enforcement, without adding diagnostic APIs to production.
[ApiController, Route("test/roles")]
public sealed class RoleProbeController : ControllerBase
{
    [HttpGet("rider"), Authorize(Roles = "Rider")]
    public IActionResult Rider() => Ok();
    [HttpGet("driver"), Authorize(Roles = "Driver")]
    public IActionResult Driver() => Ok();
    [HttpGet("admin"), Authorize(Roles = "Admin")]
    public IActionResult Admin() => Ok();
}
