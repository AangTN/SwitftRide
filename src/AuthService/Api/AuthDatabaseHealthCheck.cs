using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwiftRide.Auth.Infrastructure;

namespace SwiftRide.Auth.Api;

public sealed class AuthDatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Auth database unavailable.");
        }
        catch { return HealthCheckResult.Unhealthy("Auth database unavailable."); }
    }
}
