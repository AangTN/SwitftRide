using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Trip.Infrastructure;
using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddDbContext<TripDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TripDb")
    ?? throw new InvalidOperationException("ConnectionStrings:TripDb is required.")));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("Trip-db");
var app = builder.Build();
if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.MigrateAsync();
}
app.UseServiceDefaults();
app.Run();

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
        }
        catch { return HealthCheckResult.Unhealthy("Database unavailable."); }
    }
}
