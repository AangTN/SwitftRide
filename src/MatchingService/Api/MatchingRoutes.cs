using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using StackExchange.Redis;
using SwiftRide.Contracts.Trips;
using SwiftRide.Matching.Application;
using SwiftRide.Matching.Domain;
using SwiftRide.Matching.Infrastructure;

namespace SwiftRide.Matching.Api;

public sealed class MatchingRouteCache(IConfiguration configuration, ILogger<MatchingRouteCache> logger) : IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>?> connection = new(() =>
        configuration.GetConnectionString("RouteCache") is { Length: > 0 } address
            ? ConnectionMultiplexer.ConnectAsync(Configure(ConfigurationOptions.Parse(address)))
            : null);

    private static ConfigurationOptions Configure(ConfigurationOptions options)
    {
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 1000;
        options.AsyncTimeout = 1000;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return options;
    }

    private async Task<T?> AccessAsync<T>(Func<IDatabase, Task<T>> action, CancellationToken ct)
    {
        try
        {
            if (connection.Value is { } pending)
                return await action((await pending.WaitAsync(ct)).GetDatabase()).WaitAsync(ct);
        }
        catch (RedisException e)
        {
            logger.LogDebug("Route cache unavailable: {ErrorType}", e.GetType().Name);
        }
        return default;
    }

    private static readonly TimeSpan RouteLifetime = TimeSpan.FromHours(1);

    public async Task<RouteLeg?> ReadAsync(string key, CancellationToken ct, bool refreshExpiry = false)
    {
        var value = await AccessAsync(db => refreshExpiry
            ? db.StringGetSetExpiryAsync(key, RouteLifetime)
            : db.StringGetAsync(key), ct);
        return value.IsNull ? null : JsonSerializer.Deserialize<RouteLeg>(value.ToString());
    }

    public Task WriteAsync(string key, RouteLeg route, CancellationToken ct) =>
        AccessAsync(db => db.StringSetAsync(key, JsonSerializer.Serialize(route), RouteLifetime), ct);

    public Task DeleteAsync(Guid tripId, CancellationToken ct) =>
        AccessAsync(db => db.KeyDeleteAsync([Key(tripId, TripRouteStage.ToPickup), Key(tripId, TripRouteStage.ToDestination)]), ct);

    public Task DeleteOfferAsync(Guid tripId, Guid proposalId, CancellationToken ct) =>
        AccessAsync(db => db.KeyDeleteAsync(OfferKey(tripId, proposalId)), ct);

    public Task DeleteLegAsync(Guid tripId, TripRouteStage stage, CancellationToken ct) =>
        AccessAsync(db => db.KeyDeleteAsync(Key(tripId, stage)), ct);

    public static string OfferKey(Guid tripId, Guid proposalId) =>
        $"matching:trip:{tripId}:proposal:{proposalId}:pickup";

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated && connection.Value is { } pending)
        {
            try { await (await pending).DisposeAsync(); }
            catch (RedisException) { }
        }
    }

    public static string Key(Guid tripId, TripRouteStage stage) => $"matching:trip:{tripId}:route:{stage}";
}

public sealed class MatchingRouteLocks
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public SemaphoreSlim For(Guid tripId) => gates[(int)((uint)tripId.GetHashCode() % gates.Length)];
}

public sealed class MatchingRoutes(MatchingDatabase database, IGoongClient goong, MatchingRouteCache cache,
    MatchingRouteLocks locks, IMemoryCache progress, IPublishEndpoint bus)
{
    public async Task<RouteMetrics> GetQuoteRouteAsync(FareQuoteRequest request, CancellationToken ct)
    {
        var gate = locks.For(request.TripId);
        await gate.WaitAsync(ct);
        try
        {
            var key = MatchingRouteCache.Key(request.TripId, TripRouteStage.ToDestination);
            var saved = await cache.ReadAsync(key, ct);
            if (saved is not null)
                return new(saved.DistanceMeters, saved.DurationSeconds, saved.Polyline);
            var route = await goong.GetRouteAsync(request.PickupLatitude, request.PickupLongitude,
                request.DestinationLatitude, request.DestinationLongitude, request.VehicleType, ct);
            if (ToLeg(route) is { } leg) await cache.WriteAsync(key, leg, ct);
            return route;
        }
        finally { gate.Release(); }
    }

    public async Task PrepareOfferPickupAsync(TripMatchingRequested request, string driverId, CancellationToken ct)
    {
        var gate = locks.For(request.TripId);
        await gate.WaitAsync(ct);
        try
        {
            var key = MatchingRouteCache.OfferKey(request.TripId, request.ProposalId);
            if (await cache.ReadAsync(key, ct) is not null) return;
            var driver = await database.Drivers.Find(d => d.Id == driverId && d.Status == "Reserved" &&
                d.CurrentTripId == request.TripId.ToString() && d.CurrentProposalId == request.ProposalId.ToString())
                .FirstOrDefaultAsync(ct);
            if (driver?.Location?.Coordinates is not { Length: 2 } location ||
                driver.LocationUpdatedAt is null || driver.LocationUpdatedAt <= DateTime.UtcNow.AddMinutes(-2)) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var metrics = await goong.GetRouteAsync(location[1], location[0], request.PickupLatitude,
                    request.PickupLongitude, request.VehicleType, timeout.Token);
                if (ToLeg(metrics) is { } leg) await cache.WriteAsync(key, leg, ct);
            }
            catch (MatchingFailureException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        finally { gate.Release(); }
    }

    public async Task CleanOfferAsync(Guid tripId, Guid proposalId, CancellationToken ct)
    {
        var gate = locks.For(tripId);
        await gate.WaitAsync(ct);
        try { await cache.DeleteOfferAsync(tripId, proposalId, ct); }
        finally { gate.Release(); }
    }

    public async Task<TripRoutesUpdated> PrepareAsync(TripRouteRequested route, CancellationToken ct)
    {
        var gate = locks.For(route.TripId);
        await gate.WaitAsync(ct);
        try
        {
            var assignment = Assignment(route);
            var previousDriver = await database.Drivers.Find(assignment).FirstOrDefaultAsync(ct);
            var f = Builders<DriverAvailability>.Filter;
            var stage = f.Lte(x => x.RouteStageVersion, route.StageVersion) | f.Exists(x => x.RouteStageVersion, false);
            var driver = await database.Drivers.FindOneAndUpdateAsync(assignment & stage,
                Builders<DriverAvailability>.Update.Set(x => x.RouteStageVersion, route.StageVersion)
                    .Set(x => x.ActiveRouteJson, JsonSerializer.Serialize(route)),
                new FindOneAndUpdateOptions<DriverAvailability> { ReturnDocument = ReturnDocument.After }, ct);
            if (driver is null) return Result(route, null, null);

            if (route.Stage == TripRouteStage.ToDestination && previousDriver?.RouteStageVersion < route.StageVersion)
                await cache.DeleteLegAsync(route.TripId, TripRouteStage.ToDestination, ct);

            RouteLeg? pickup = null;
            if (route.Stage == TripRouteStage.ToPickup)
                pickup = await GetLegAsync(route, TripRouteStage.ToPickup, driver, false, ct);
            var destination = await GetLegAsync(route, TripRouteStage.ToDestination, driver, false, ct);
            return Result(route, pickup, destination);
        }
        finally { gate.Release(); }
    }

    public async Task TrackAsync(DriverGpsUpdated gps, CancellationToken ct)
    {
        var gate = locks.For(gps.TripId);
        await gate.WaitAsync(ct);
        try
        {
            var driver = await database.Drivers.Find(x => x.Id == gps.DriverId.ToString() && x.Status == "Busy" &&
                x.CurrentTripId == gps.TripId.ToString() && x.CurrentProposalId == gps.ProposalId.ToString()).FirstOrDefaultAsync(ct);
            if (driver?.ActiveRouteJson is null || driver.LocationUpdatedAt is null ||
                gps.RecordedAt.UtcDateTime < driver.LocationUpdatedAt.Value.AddMilliseconds(-1)) return;
            var route = JsonSerializer.Deserialize<TripRouteRequested>(driver.ActiveRouteJson)!;
            var current = await cache.ReadAsync(MatchingRouteCache.Key(route.TripId, route.Stage), ct, refreshExpiry: true);
            var progressKey = (route.ProposalId, route.StageVersion);
            var previous = progress.Get<Progress>(progressKey);
            if (previous is not null && previous.RecordedAt >= gps.RecordedAt) return;
            var remaining = current is null ? null : RouteProgress.RemainingFraction(current.Polyline, gps.Latitude, gps.Longitude);
            await bus.Publish(new TripRouteProgress(route.TripId, route.ProposalId, route.DriverId,
                route.StageVersion, route.Stage, gps.Latitude, gps.Longitude, gps.RecordedAt,
                remaining is null ? null : current!.DistanceMeters * remaining,
                remaining is null ? null : (int)Math.Ceiling(current!.DurationSeconds * remaining.Value)), ct);
            var offRoute = current is null || RouteDeviation.IsOffRoute(current.Polyline, gps.Latitude, gps.Longitude);
            var samples = offRoute ? (previous?.Samples ?? 0) + 1 : 0;
            var lastAttempt = previous?.LastAttempt ?? DateTimeOffset.MinValue;
            var now = DateTimeOffset.UtcNow;
            var reroute = (current is null || samples >= 3) && now - lastAttempt >= TimeSpan.FromSeconds(20);
            progress.Set(progressKey, new Progress(samples, gps.RecordedAt, reroute ? now : lastAttempt), TimeSpan.FromMinutes(10));
            if (!reroute) return;
            var updated = await GetLegAsync(route, route.Stage, driver, true, ct);
            if (updated is null) return;
            var other = route.Stage == TripRouteStage.ToPickup
                ? await cache.ReadAsync(MatchingRouteCache.Key(route.TripId, TripRouteStage.ToDestination), ct)
                : null;
            await bus.Publish(route.Stage == TripRouteStage.ToPickup ? Result(route, updated, other)
                : Result(route, null, updated), ct);
        }
        finally { gate.Release(); }
    }

    public async Task CleanAsync(Guid tripId, CancellationToken ct)
    {
        var gate = locks.For(tripId);
        await gate.WaitAsync(ct);
        try { await cache.DeleteAsync(tripId, ct); }
        finally { gate.Release(); }
    }

    private async Task<RouteLeg?> GetLegAsync(TripRouteRequested route, TripRouteStage leg, DriverAvailability driver,
        bool force, CancellationToken ct)
    {
        var key = MatchingRouteCache.Key(route.TripId, leg);
        var saved = await cache.ReadAsync(key, ct, refreshExpiry: leg == route.Stage);
        var toPickup = leg == TripRouteStage.ToPickup;
        var location = driver.Location?.Coordinates;
        var useGps = toPickup || route.Stage == TripRouteStage.ToDestination;
        var hasGps = location is { Length: 2 } && driver.LocationUpdatedAt > DateTime.UtcNow.AddMinutes(-2);
        var farFromPickup = hasGps && !toPickup && route.Stage == TripRouteStage.ToDestination &&
            MatchingCoordinator.DistanceMeters(location![1], location[0], route.PickupLatitude, route.PickupLongitude) > 60;
        if (!force && saved is not null &&
            (!farFromPickup || !RouteDeviation.IsOffRoute(saved.Polyline, location![1], location[0]))) return saved;
        if ((toPickup || route.Stage == TripRouteStage.ToDestination) && !hasGps) return saved;
        try
        {
            var metrics = await goong.GetRouteAsync(useGps && hasGps ? location![1] : route.PickupLatitude,
                useGps && hasGps ? location![0] : route.PickupLongitude,
                toPickup ? route.PickupLatitude : route.DestinationLatitude,
                toPickup ? route.PickupLongitude : route.DestinationLongitude, route.VehicleType, ct);
            var result = ToLeg(metrics);
            if (result is null) return force ? null : saved;
            if (!await database.Drivers.Find(Assignment(route) &
                Builders<DriverAvailability>.Filter.Eq(x => x.RouteStageVersion, route.StageVersion)).AnyAsync(ct)) return null;
            await cache.WriteAsync(key, result, ct);
            return result;
        }
        catch (MatchingFailureException) { return force ? null : saved; }
    }

    private static FilterDefinition<DriverAvailability> Assignment(TripRouteRequested route) =>
        Builders<DriverAvailability>.Filter.Where(x => x.Id == route.DriverId.ToString() &&
            x.CurrentTripId == route.TripId.ToString() && x.CurrentProposalId == route.ProposalId.ToString() &&
            (x.Status == "Reserved" || x.Status == "Busy"));

    public async Task<TripRoutePreview> ReadPreviewAsync(GetTripRoutePreview request, CancellationToken ct)
    {
        var destination = await cache.ReadAsync(MatchingRouteCache.Key(request.TripId, TripRouteStage.ToDestination), ct);
        var pickup = await cache.ReadAsync(MatchingRouteCache.OfferKey(request.TripId, request.ProposalId), ct);
        return new(request.TripId, request.ProposalId, destination, EtaMinutes(pickup));
    }

    private static int? EtaMinutes(RouteLeg? route) =>
        route is null ? null : (int)Math.Ceiling(route.DurationSeconds / 60d);
    internal static RouteLeg? ToLeg(RouteMetrics metrics) => string.IsNullOrWhiteSpace(metrics.Polyline) ? null
        : new(metrics.Polyline, metrics.DistanceMeters, metrics.DurationSeconds, DateTimeOffset.UtcNow);
    private static TripRoutesUpdated Result(TripRouteRequested route, RouteLeg? pickup, RouteLeg? destination) =>
        new(route.TripId, route.ProposalId, route.DriverId, route.StageVersion, route.Stage, pickup, destination, EtaMinutes(pickup));

    private sealed record Progress(int Samples, DateTimeOffset RecordedAt, DateTimeOffset LastAttempt);
}

public sealed class MatchingRouteConsumer(MatchingRoutes routes) :
    IConsumer<TripRouteRequested>, IConsumer<TripRouteCleanup>, IConsumer<GetTripRoutes>, IConsumer<GetTripRoutePreview>, IConsumer<DriverGpsUpdated>
{
    public async Task Consume(ConsumeContext<TripRouteRequested> context)
    {
        var result = await routes.PrepareAsync(context.Message, context.CancellationToken);
        if (result.ToPickup is not null || result.ToDestination is not null)
            await context.Publish(result, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<TripRouteCleanup> context) =>
        routes.CleanAsync(context.Message.TripId, context.CancellationToken);

    public async Task Consume(ConsumeContext<GetTripRoutes> context) =>
        await context.RespondAsync(await routes.PrepareAsync(context.Message.Route, context.CancellationToken));

    public async Task Consume(ConsumeContext<GetTripRoutePreview> context) =>
        await context.RespondAsync(await routes.ReadPreviewAsync(context.Message, context.CancellationToken));

    public Task Consume(ConsumeContext<DriverGpsUpdated> context) =>
        routes.TrackAsync(context.Message, context.CancellationToken);
}
