using SwiftRide.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment(), broker: false);
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHealthChecks().AddCheck<BackendHealthCheck>("backends");
builder.Services.AddHttpClient("readiness", c => c.Timeout = TimeSpan.FromSeconds(3));
var app = builder.Build();
app.UseServiceDefaults();
app.MapReverseProxy();
app.Run();

public partial class Program;

public sealed class BackendHealthCheck(IHttpClientFactory clients, IConfiguration config)
    : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            foreach (var cluster in config.GetSection("ReverseProxy:Clusters").GetChildren())
            {
                var address = cluster["Destinations:primary:Address"]!;
                using var response = await clients.CreateClient("readiness").GetAsync(address.TrimEnd('/') + "/health/ready", ct);
                response.EnsureSuccessStatusCode();
            }
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch { return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("A backend is unavailable."); }
    }
}
