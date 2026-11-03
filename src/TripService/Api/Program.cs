using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Infrastructure;
using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment(), configureBus: x =>
{
    x.AddConsumer<PaymentResultConsumer>(c => c.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(200))));
    x.AddConsumer<TripMatchingResultConsumer>(c => c.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(200))));
    x.AddConsumer<DriverGpsUpdatedConsumer>();
});
builder.Services.AddDbContext<TripDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("TripDb")
    ?? throw new InvalidOperationException("ConnectionStrings:TripDb is required.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TripWorkflowOptions>(builder.Configuration.GetSection("TripWorkflow"));
builder.Services.AddSignalR();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddScoped<ITripRealtimePublisher, TripRealtimePublisher>();
builder.Services.AddScoped<ITripMatchingNotifier, TripMatchingNotifier>();
builder.Services.AddScoped<ITripPaymentNotifier, TripPaymentNotifier>();
builder.Services.AddOptions<TripOutboxOptions>().Bind(builder.Configuration.GetSection("TripOutbox"))
    .Validate(x => x.RetryCount >= 0 && x.RetryCount <= 100 && x.RetryDelaySeconds > 0 && x.PublishTimeoutSeconds > 0,
        "Outbox retry count must be 0–100 and delay/timeout must be positive.").ValidateOnStart();
// Trip can persist requests while the broker reconnects in the background.
builder.Services.Configure<MassTransit.MassTransitHostOptions>(x => x.WaitUntilStarted = false);
if (builder.Configuration.GetValue("RabbitMq:Enabled", true))
{
    builder.Services.AddScoped<TripOutboxDispatcher>();
    builder.Services.AddHostedService<TripOutboxWorker>();
}
builder.Services.AddScoped<TripPaymentResultHandler>();
builder.Services.AddScoped<TripMatchingResultHandler>();
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
