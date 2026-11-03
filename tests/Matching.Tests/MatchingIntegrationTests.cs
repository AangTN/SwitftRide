using System.Reflection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using MongoDB.Bson;
using SwiftRide.Contracts.Trips;
using SwiftRide.Matching.Api;
using SwiftRide.Matching.Application;
using SwiftRide.Matching.Domain;
using SwiftRide.Matching.Infrastructure;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_MONGO")))
            Skip = "Set SWIFTRIDE_TEST_MONGO to an isolated MongoDB test server.";
    }
}

public sealed class MatchingIntegrationTests
{
    [MongoFact]
    public async Task Avatar_upgrade_fills_missing_field_without_overwriting_existing_avatars()
    {
        await using var f = await Fixture.Create();
        await f.Db.Profiles.UpdateOneAsync(x => x.Id == f.Driver,
            Builders<DriverProfile>.Update.Set(x => x.AvatarImageKey, "avatars/existing.webp"));
        var legacyId = Guid.NewGuid().ToString();
        var rawProfiles = f.Db.Database.GetCollection<BsonDocument>("driver_profiles");
        await rawProfiles.InsertOneAsync(new BsonDocument { { "_id", legacyId }, { "plateNumber", "LEGACY-TEST" } });
        await f.Db.InitializeAsync(false);
        await f.Db.InitializeAsync(false);
        Assert.Equal("avatars/existing.webp", (await f.Db.FindAsync(f.Driver, default))!.AvatarImageKey);
        var legacy = await rawProfiles.Find(new BsonDocument("_id", legacyId)).SingleAsync();
        Assert.True(legacy.Contains("avatarImageKey"));
        Assert.True(legacy["avatarImageKey"].IsBsonNull);
    }

    [MongoFact]
    public async Task Consumers_deliver_find_confirm_and_release_through_a_message_bus()
    {
        await using var f = await Fixture.Create();
        var services = new ServiceCollection().AddLogging().AddSingleton(f.Jobs).AddSingleton<ResultSink>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<TripMatchingRequestedConsumer>();
            x.AddConsumer<DriverReservationConfirmedConsumer>();
            x.AddConsumer<DriverReservationReleaseConsumer>();
            x.AddConsumer<ResultConsumer>();
            x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            var request = f.Request();
            await bus.Publish(request);
            await WaitFor(async () => await f.Jobs.Jobs.Find(x => x.Id == request.ProposalId.ToString() && x.WorkPending).AnyAsync());
            await f.CreateDispatcher(bus).DispatchOneAsync(default);
            var result = await provider.GetRequiredService<ResultSink>().Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(f.Driver, result.Candidate!.DriverId.ToString());
            await bus.Publish(new DriverReservationConfirmed(request.TripId, request.ProposalId, Guid.Parse(f.Driver)));
            await WaitFor(async () => (await f.Jobs.ReloadAsync(request.ProposalId.ToString(), default)).Confirmed);
            await f.CreateDispatcher(bus).DispatchOneAsync(default);
            Assert.Equal("Busy", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
            await bus.Publish(new DriverReservationReleaseRequested(request.TripId, request.ProposalId, Guid.Parse(f.Driver), "trip_dropped_off"));
            await WaitFor(async () => (await f.Jobs.ReloadAsync(request.ProposalId.ToString(), default)).Released);
            await f.CreateDispatcher(bus).DispatchOneAsync(default);
            Assert.Equal("Available", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
        }
        finally { await bus.StopAsync(); }
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    public sealed class ResultSink
    {
        public TaskCompletionSource<TripMatchingResult> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class ResultConsumer(ResultSink sink) : IConsumer<TripMatchingResult>
    {
        public Task Consume(ConsumeContext<TripMatchingResult> context)
        { sink.Received.TrySetResult(context.Message); return Task.CompletedTask; }
    }

    [MongoFact]
    public async Task Busy_driver_updates_GPS_without_becoming_available_and_release_checks_both_ids()
    {
        await using var f = await Fixture.Create();
        var trip = Guid.NewGuid(); var proposal = Guid.NewGuid();
        await f.Reserve(trip, proposal);
        Assert.True(await f.Db.ConfirmReservationAsync(f.Driver, trip.ToString(), proposal.ToString(), DateTime.UtcNow, default));
        var updated = await f.Db.UpdateGpsAsync(f.Driver, new DriverGpsRequest(10.79, 106.7), DateTime.UtcNow, default);
        Assert.Equal("Busy", updated!.Status);
        Assert.Equal(trip.ToString(), updated.CurrentTripId);
        Assert.Equal(106.7, updated.Location!.Coordinates[0]);
        Assert.False(await f.Db.ReleaseReservationAsync(f.Driver, Guid.NewGuid().ToString(), proposal.ToString(), DateTime.UtcNow, default));
        Assert.False(await f.Db.ReleaseReservationAsync(f.Driver, trip.ToString(), Guid.NewGuid().ToString(), DateTime.UtcNow, default));
        Assert.Equal("Busy", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
        Assert.True(await f.Db.ReleaseReservationAsync(f.Driver, trip.ToString(), proposal.ToString(), DateTime.UtcNow, default));
        Assert.Equal("Available", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
    }

    [MongoFact]
    public async Task Concurrent_trips_cannot_reserve_the_same_driver()
    {
        await using var f = await Fixture.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            f.Db.TryReserveDriverAsync(f.Driver, "Motorbike", Fixture.Location, Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(), DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow, DateTime.UtcNow.AddSeconds(120), default)));
        Assert.Single(results, x => x);
    }

    [MongoFact]
    public async Task Matching_filters_stale_GPS_wrong_vehicle_and_drivers_outside_ten_km()
    {
        await using var f = await Fixture.Create();
        await f.Db.Drivers.UpdateOneAsync(x => x.Id == f.Driver,
            Builders<DriverAvailability>.Update.Set(x => x.LocationUpdatedAt, DateTime.UtcNow.AddSeconds(-121)));
        Assert.Empty(await f.Nearby());
        await f.Db.UpdateGpsAsync(f.Driver, new DriverGpsRequest(11.78, 106.69), DateTime.UtcNow, default);
        Assert.Empty(await f.Nearby());
        await f.Db.UpdateGpsAsync(f.Driver, new DriverGpsRequest(10.78, 106.69), DateTime.UtcNow, default);
        Assert.Single(await f.Nearby());
        Assert.Empty(await f.Nearby("Car4Seats"));
    }

    [MongoFact]
    public async Task Result_outbox_survives_broker_failure_and_duplicate_request_does_not_rematch()
    {
        await using var f = await Fixture.Create();
        var request = f.Request();
        await f.Jobs.EnqueueAsync(request, default);
        f.Transport.Fail = true;
        await f.Dispatcher.DispatchOneAsync(default);
        var job = await f.Jobs.ReloadAsync(request.ProposalId.ToString(), default);
        Assert.NotNull(job.ResultJson);
        Assert.False(job.ResultSent);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("Reserved", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
        f.Transport.Fail = false;
        await f.Jobs.Jobs.UpdateOneAsync(x => x.Id == job.Id,
            Builders<MatchingJob>.Update.Set(x => x.NextAttemptAt, DateTime.UnixEpoch));
        await f.Dispatcher.DispatchOneAsync(default);
        Assert.True((await f.Jobs.ReloadAsync(job.Id, default)).ResultSent);
        await f.Jobs.EnqueueAsync(request, default);
        Assert.False(await f.Dispatcher.DispatchOneAsync(default));
        Assert.Equal(1, f.Goong.Calls);
        Assert.Equal("avatars/test.webp", Assert.Single(f.Transport.Messages).Candidate!.DriverAvatarImageKey);
    }

    [MongoFact]
    public async Task Release_before_find_or_delayed_confirm_cannot_reopen_assignment()
    {
        await using var f = await Fixture.Create();
        var request = f.Request();
        await f.Jobs.ControlAsync(request.TripId, request.ProposalId, Guid.Parse(f.Driver), true, default);
        await f.Jobs.EnqueueAsync(request, default);
        await f.Jobs.ControlAsync(request.TripId, request.ProposalId, Guid.Parse(f.Driver), false, default);
        await f.Dispatcher.DispatchOneAsync(default);
        Assert.Equal("Available", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
        Assert.Empty(f.Transport.Messages);
        Assert.True((await f.Jobs.ReloadAsync(request.ProposalId.ToString(), default)).Released);
    }

    [MongoFact]
    public async Task Confirm_then_dropoff_releases_busy_driver_and_old_release_does_not_unlock_new_trip()
    {
        await using var f = await Fixture.Create();
        var request = f.Request();
        await f.Jobs.EnqueueAsync(request, default);
        await f.Dispatcher.DispatchOneAsync(default);
        await f.Jobs.ControlAsync(request.TripId, request.ProposalId, Guid.Parse(f.Driver), false, default);
        await f.Dispatcher.DispatchOneAsync(default);
        Assert.Equal("Busy", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
        await f.Jobs.ControlAsync(request.TripId, request.ProposalId, Guid.Parse(f.Driver), true, default);
        await f.Dispatcher.DispatchOneAsync(default);
        var next = f.Request();
        await f.Reserve(next.TripId, next.ProposalId);
        await f.Db.ReleaseReservationAsync(f.Driver, request.TripId.ToString(), request.ProposalId.ToString(), DateTime.UtcNow, default);
        var driver = await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync();
        Assert.Equal(next.TripId.ToString(), driver.CurrentTripId);
        Assert.Equal("Reserved", driver.Status);
    }

    [MongoFact]
    public async Task No_driver_is_persisted_and_published_as_failure_instead_of_waiting_forever()
    {
        await using var f = await Fixture.Create();
        await f.Db.Drivers.UpdateOneAsync(x => x.Id == f.Driver, Builders<DriverAvailability>.Update.Set(x => x.Status, "Offline"));
        await f.Jobs.EnqueueAsync(f.Request(), default);
        await f.Dispatcher.DispatchOneAsync(default);
        var result = Assert.Single(f.Transport.Messages);
        Assert.Null(result.Candidate);
        Assert.Equal("no_driver_available", result.FailureCode);
    }

    [MongoFact]
    public async Task Temporary_route_failure_retries_three_times_then_reports_failure()
    {
        await using var f = await Fixture.Create();
        f.Goong.Failures = 4;
        var request = f.Request();
        await f.Jobs.EnqueueAsync(request, default);
        for (var i = 0; i < 4; i++)
        {
            await f.Dispatcher.DispatchOneAsync(default);
            if (i < 3)
            {
                Assert.Empty(f.Transport.Messages);
                var job = await f.Jobs.ReloadAsync(request.ProposalId.ToString(), default);
                Assert.True(job.NextAttemptAt > DateTime.UtcNow.AddSeconds(3));
                await f.Jobs.Jobs.UpdateOneAsync(x => x.Id == job.Id,
                    Builders<MatchingJob>.Update.Set(x => x.NextAttemptAt, DateTime.UnixEpoch));
            }
        }
        Assert.Equal(4, f.Goong.Calls);
        Assert.Null(Assert.Single(f.Transport.Messages).Candidate);
        Assert.Equal("Available", (await f.Db.Drivers.Find(x => x.Id == f.Driver).SingleAsync()).Status);
    }

    [MongoFact]
    public async Task Expired_local_hold_does_not_unlock_driver_while_Trip_confirmation_is_in_transit()
    {
        await using var f = await Fixture.Create();
        var trip = Guid.NewGuid(); var proposal = Guid.NewGuid();
        await f.Reserve(trip, proposal);
        await f.Db.Drivers.UpdateOneAsync(x => x.Id == f.Driver, Builders<DriverAvailability>.Update.Set(x => x.ReservedUntil, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Empty(await f.Nearby());
        Assert.True(await f.Db.ConfirmReservationAsync(f.Driver, trip.ToString(), proposal.ToString(), DateTime.UtcNow, default));
    }

    public class Transport : DispatchProxy
    {
        public bool Fail { get; set; }
        public List<TripMatchingResult> Messages { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Fail) return Task.FromException(new IOException("Test broker unavailable"));
            if (args?[0] is TripMatchingResult result) Messages.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class StubGoong : IGoongClient
    {
        public int Calls { get; private set; }
        public int Failures { get; set; }
        public Task<RouteMetrics> GetRouteAsync(double a, double b, double c, double d, string vehicleType, CancellationToken ct)
        {
            Calls++;
            if (Calls <= Failures) throw new MatchingFailureException(502, "route_unavailable", "Test route failure");
            return Task.FromResult(new RouteMetrics(1000, 120));
        }
        public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct) => throw new NotSupportedException();
        public Task<GeocodedAddress?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaceSuggestion>> SearchPlacesAsync(string input, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MongoClient client;
        public MatchingDatabase Db { get; }
        public MatchingJobStore Jobs { get; }
        public MatchingJobDispatcher Dispatcher { get; }
        public StubGoong Goong { get; } = new();
        public string Driver { get; } = Guid.NewGuid().ToString();
        public Transport Transport { get; }
        public static GeoPoint Location => new() { Coordinates = [106.69, 10.78] };
        private Fixture()
        {
            client = new MongoClient(Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_MONGO"));
            Db = new MatchingDatabase(client, "matching_test_" + Guid.NewGuid().ToString("N"));
            Jobs = new MatchingJobStore(Db);
            var bus = DispatchProxy.Create<IPublishEndpoint, Transport>(); Transport = (Transport)bus;
            Dispatcher = CreateDispatcher(bus);
        }
        public MatchingJobDispatcher CreateDispatcher(IPublishEndpoint bus) => new(Jobs, new MatchingCoordinator(Db, Db, Goong,
                new FareCalculator(new StandardFareStrategy(), new SurgeFareStrategy(), new PromotionFareStrategy()),
                new MatchingOptions(), new PricingOptions()), Db, bus);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Db.InitializeAsync(false);
            await f.Db.Profiles.InsertOneAsync(new DriverProfile { Id = f.Driver, LegalName = "Driver", PlateNumber = "59-Test", VehicleName = "Bike", AvatarImageKey = "avatars/test.webp" });
            await f.Db.UpdateAvailabilityAsync(f.Driver, "Motorbike", new DriverLocationUpdate(true, 10.78, 106.69), DateTime.UtcNow, default);
            return f;
        }
        public TripMatchingRequested Request() => new(Guid.NewGuid(), Guid.NewGuid(), [], 10.78, 106.69, 10.79, 106.70);
        public async Task Reserve(Guid trip, Guid proposal) => Assert.True(await Db.TryReserveDriverAsync(Driver, "Motorbike", Location,
            trip.ToString(), proposal.ToString(), DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow, DateTime.UtcNow.AddSeconds(120), default));
        public Task<IReadOnlyList<DriverAvailability>> Nearby(string vehicle = "Motorbike") => Db.FindNearbyAvailableDriversAsync(Location,
            vehicle, DateTime.UtcNow.AddSeconds(-120), 10000, 50, DateTime.UtcNow, [], default);
        public async ValueTask DisposeAsync() => await client.DropDatabaseAsync(Db.Database.DatabaseNamespace.DatabaseName);
    }
}
