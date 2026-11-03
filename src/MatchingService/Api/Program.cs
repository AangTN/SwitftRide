using MongoDB.Bson;
using MongoDB.Driver;
using SwiftRide.Matching.Infrastructure;
using SwiftRide.Matching.Application;
using SwiftRide.ServiceDefaults;
using MassTransit;
using SwiftRide.Matching.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment.IsDevelopment(), configureBus: x =>
{
    x.AddConsumer<TripMatchingRequestedConsumer>(c => c.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(200))));
    x.AddConsumer<DriverReservationConfirmedConsumer>(c => c.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(200))));
    x.AddConsumer<DriverReservationReleaseConsumer>(c => c.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(200))));
});
builder.Services.AddSingleton<MatchingJobStore>();
builder.Services.Configure<MassTransitHostOptions>(x => x.WaitUntilStarted = false);
if (builder.Configuration.GetValue("RabbitMq:Enabled", true))
{
    builder.Services.AddScoped<MatchingJobDispatcher>();
    builder.Services.AddHostedService<MatchingJobWorker>();
}
if (builder.Configuration.GetValue("Seed:Enabled", false) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Development seed cannot be enabled outside Development.");
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(builder.Configuration.GetConnectionString("MatchingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:MatchingDb is required.")));
builder.Services.AddSingleton(sp => new MatchingDatabase(sp.GetRequiredService<IMongoClient>(),
    builder.Configuration["Mongo:Database"] ?? "MatchingDB"));
builder.Services.AddSingleton<IDriverProfileReader>(sp => sp.GetRequiredService<MatchingDatabase>());
builder.Services.AddSingleton<IMatchingStore>(sp => sp.GetRequiredService<MatchingDatabase>());
builder.Services.AddOptions<MatchingOptions>()
    .Bind(builder.Configuration.GetSection("Matching"))
    .Validate(options => options.MaxSearchDistanceMeters > 0 && options.MaxCandidates > 0 &&
        options.LocationMaxAgeSeconds > 0 && options.ReservationSeconds > 0 && options.QuoteValiditySeconds > 0,
        "Matching limits and expiry durations must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<PricingOptions>()
    .Bind(builder.Configuration.GetSection("Pricing"))
    .Validate(options => options.PerKilometer >= 0 && options.PerMinute >= 0 && options.Toll >= 0 &&
        options.Surge > 0 && options.PromotionFactor > 0 && options.TaxRate is >= 0 and <= 1,
        "Pricing rates must be non-negative and factors must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<IStandardFareStrategy, StandardFareStrategy>();
builder.Services.AddSingleton<ISurgeFareStrategy, SurgeFareStrategy>();
builder.Services.AddSingleton<IPromotionFareStrategy, PromotionFareStrategy>();
builder.Services.AddScoped<FareCalculator>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MatchingOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PricingOptions>>().Value);
builder.Services.AddHttpClient<IGoongClient, GoongClient>(client =>
{
    client.BaseAddress = new Uri("https://rsapi.goong.io/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<MatchingCoordinator>();
builder.Services.AddHealthChecks().AddCheck<MatchingHealthCheck>("matching-db");
var app = builder.Build();
if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
    await app.Services.GetRequiredService<MatchingDatabase>().InitializeAsync(
        app.Environment.IsDevelopment() && app.Configuration.GetValue("Seed:Enabled", false));
app.UseServiceDefaults();
app.Run();

public partial class Program;

public sealed class MatchingHealthCheck(MatchingDatabase db) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await db.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch { return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("MongoDB unavailable."); }
    }
}
