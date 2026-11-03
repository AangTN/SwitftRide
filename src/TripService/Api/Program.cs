using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Infrastructure;
using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddDbContext<TripDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TripDb")
    ?? throw new InvalidOperationException("ConnectionStrings:TripDb is required.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TripWorkflowOptions>(builder.Configuration.GetSection("TripWorkflow"));
builder.Services.AddSignalR();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddScoped<ITripRealtimePublisher, TripRealtimePublisher>();
if (builder.Configuration.GetValue("RabbitMq:Enabled", true)) builder.Services.AddScoped<ITripMatchingNotifier, TripMatchingNotifier>();
else builder.Services.AddScoped<ITripMatchingNotifier, NoopTripMatchingNotifier>();
if (builder.Configuration.GetValue("RabbitMq:Enabled", true)) builder.Services.AddScoped<ITripPaymentNotifier, TripPaymentNotifier>();
else builder.Services.AddScoped<ITripPaymentNotifier, NoopTripPaymentNotifier>();
builder.Services.AddScoped<TripExceptionFilter>();
builder.Services.AddHostedService<ProposalExpiryWorker>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("Trip-db");
var app = builder.Build();
if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.MigrateAsync();
}
app.UseServiceDefaults();
app.MapHub<TripHub>("/api/trips/realtime");
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
