using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Infrastructure;
using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddDbContext<PaymentDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("ConnectionStrings:PaymentDb is required.")));

builder.Services.AddScoped<IPaymentStore, PaymentStore>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<IPaymentGateway, WalletPaymentGateway>();
builder.Services.AddScoped<IPaymentGateway, MockExternalCardGateway>();
builder.Services.AddScoped<IPaymentGatewayFactory, PaymentGatewayFactory>();
builder.Services.AddScoped<IPaymentEventPublisher, PaymentEventPublisher>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("Payment-db");

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Configuration.GetValue("Seed:Enabled", app.Environment.IsDevelopment()))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await PaymentSeed.RunAsync(db);
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
            return await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
        }
        catch { return HealthCheckResult.Unhealthy("Database unavailable."); }
    }
}
