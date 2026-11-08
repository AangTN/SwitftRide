using System.Security.Claims;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using Xunit;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Tests;

public sealed class MatchingWorkflowTests
{
    [Fact]
    public async Task GPS_is_forwarded_only_to_the_rider_of_the_correct_active_assignment()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip(); var p = Assert.Single(trip.Proposals);
        var hub = new RecordingHub();
        var consumer = new DriverGpsUpdatedConsumer(f.Db, hub);
        var gps = new DriverGpsUpdated(trip.Id, p.Id, f.Driver, 10.78, 106.69, DateTimeOffset.UtcNow);
        await consumer.ForwardAsync(gps with { DriverId = Guid.NewGuid() }, default);
        await consumer.ForwardAsync(gps with { ProposalId = Guid.NewGuid() }, default);
        await consumer.ForwardAsync(gps with { TripId = Guid.NewGuid() }, default);
        Assert.Empty(hub.Events);
        await consumer.ForwardAsync(gps, default);
        Assert.Equal((f.Rider.ToString(), "DriverLocationUpdated"), Assert.Single(hub.Events));
        trip.Status = TripStatus.PaymentPending; await f.Db.SaveChangesAsync();
        await consumer.ForwardAsync(gps, default);
        Assert.Single(hub.Events);
    }

    [Fact]
    public async Task Route_preview_is_sent_only_to_the_rider_while_the_proposal_is_current()
    {
        await using var f = new Fixture();
        var trip = await f.MatchingTrip();
        var proposal = Assert.Single(trip.Proposals);
        var hub = new RecordingHub();
        var consumer = new TripRoutePreviewConsumer(f.Db, hub);
        var preview = new TripRoutePreview(trip.Id, proposal.Id, new RouteLeg("encoded", 1200, 360, f.Clock.GetUtcNow()));

        await consumer.ForwardAsync(preview with { ProposalId = Guid.NewGuid() }, default);
        Assert.Empty(hub.Events);
        await consumer.ForwardAsync(preview, default);
        Assert.Equal((f.Rider.ToString(), "TripRoutePreview"), Assert.Single(hub.Events));

        trip.Status = TripStatus.Cancelled;
        await f.Db.SaveChangesAsync();
        await consumer.ForwardAsync(preview, default);
        Assert.Single(hub.Events);
    }

    [Fact]
    public async Task Rider_confirms_driver_and_price_within_90_seconds_before_driver_gets_30_seconds()
    {
        await using var f = new Fixture();
        var trip = await f.MatchingTrip();
        var p = Assert.Single(trip.Proposals);
        var candidate = new TripProposalCandidate(p.Id, f.Driver, "quote", 25000, "VND", "Verified driver", "59A", "Bike", null, "avatars/driver.webp");
        var handler = f.Handler();
        Assert.True(await handler.ApplyAsync(new TripMatchingResult(trip.Id, p.Id, candidate), default));
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(90), p.RiderApprovalDeadline);
        Assert.Null(p.DriverResponseDeadline);
        Assert.Equal("Verified driver", p.DriverNameSnapshot);
        Assert.Equal(25000, p.Amount);
        f.Clock.Advance(80);
        await handler.ApplyAsync(new TripMatchingResult(trip.Id, p.Id, candidate), default);
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(10), p.RiderApprovalDeadline); // Replay cannot reset the clock.
        await f.Controller(f.Rider, "Rider").Approve(trip.Id, p.Id, new VersionRequest(trip.StateVersion), default);
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(30), p.DriverResponseDeadline);
        Assert.Equal(1, f.Realtime.Invitations);
        await f.Controller(f.Driver, "Driver").DriverAccept(trip.Id, new DriverProposalRequest(p.Id, trip.StateVersion), default);
        Assert.Equal(TripStatus.EnRoute, trip.Status);
        Assert.Equal(f.Driver, trip.DriverId);
        Assert.Equal(25000, trip.AgreedAmount);
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.Trips.Include(x => x.Proposals).SingleAsync();
        Assert.Equal("avatars/driver.webp", saved.DriverAvatarImageKeySnapshot);
        Assert.Equal("avatars/driver.webp", Assert.Single(saved.Proposals).DriverAvatarImageKeySnapshot);
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.ConfirmDriver).ToListAsync());
    }

    [Fact]
    public async Task Legacy_accepted_trip_can_pick_up_without_separate_en_route_action()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip();
        await f.Controller(f.Driver, "Driver").Pickup(trip.Id, new VersionRequest(trip.StateVersion), default);
        Assert.Equal(TripStatus.PickedUp, trip.Status);
        Assert.NotNull(trip.PickedUpAt);
        Assert.Contains(trip.History, x => x.ToStatus == TripStatus.EnRoute);
        Assert.Contains(trip.History, x => x.ToStatus == TripStatus.PickedUp);
    }

    [Fact]
    public async Task Driver_timeout_after_30_seconds_releases_and_finds_another_candidate()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip(); var p = Assert.Single(trip.Proposals);
        trip.DriverId = null; trip.Status = TripStatus.AwaitingDriverResponse;
        p.Status = ProposalStatus.AwaitingDriverResponse; p.DriverResponseDeadline = f.Clock.GetUtcNow().AddSeconds(30);
        await f.Db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(f.Db).AddSingleton<ITripRealtimePublisher>(f.Realtime)
            .AddSingleton<ITripMatchingNotifier>(new TripMatchingNotifier(f.Db, f.Clock));
        await using var provider = services.BuildServiceProvider();
        var worker = new ProposalExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Clock,
            NullLogger<ProposalExpiryWorker>.Instance, Options.Create(new TripWorkflowOptions()));
        f.Clock.Advance(29); await worker.ExpireAsync(default);
        Assert.Equal(TripStatus.AwaitingDriverResponse, trip.Status);
        f.Clock.Advance(1); await worker.ExpireAsync(default);
        Assert.Equal(TripStatus.Matching, trip.Status);
        Assert.Equal(ProposalStatus.DriverTimedOut, p.Status);
        Assert.Single(trip.Proposals, x => x.Status == ProposalStatus.Searching);
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.ReleaseDriver).ToListAsync());
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.FindDriver).ToListAsync());
    }

    [Fact]
    public async Task Rider_timeout_cancels_without_trying_another_driver()
    {
        await using var f = new Fixture();
        var trip = await f.MatchingTrip();
        var p = Assert.Single(trip.Proposals);
        await f.Handler().ApplyAsync(new TripMatchingResult(trip.Id, p.Id,
            new TripProposalCandidate(p.Id, f.Driver, "quote", 25000, "VND", "Driver", "59A", "Bike", null)), default);
        var services = new ServiceCollection().AddSingleton(f.Db).AddSingleton<ITripRealtimePublisher>(f.Realtime)
            .AddSingleton<ITripMatchingNotifier>(new TripMatchingNotifier(f.Db, f.Clock));
        await using var provider = services.BuildServiceProvider();
        var worker = new ProposalExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), f.Clock,
            NullLogger<ProposalExpiryWorker>.Instance, Options.Create(new TripWorkflowOptions()));

        f.Clock.Advance(90);
        await worker.ExpireAsync(default);

        Assert.Equal(TripStatus.Cancelled, trip.Status);
        Assert.Equal(ProposalStatus.RiderTimedOut, Assert.Single(trip.Proposals).Status);
        Assert.DoesNotContain(await f.Db.OutboxMessages.ToListAsync(), x => x.Kind == TripMessageKind.FindDriver);
        Assert.Contains(await f.Db.OutboxMessages.ToListAsync(), x => x.Kind == TripMessageKind.ReleaseDriver);
        Assert.Contains(await f.Db.OutboxMessages.ToListAsync(), x => x.Kind == TripMessageKind.RouteCleanup);
    }

    [Fact]
    public async Task Rider_rejection_retries_until_three_offers_and_then_fails()
    {
        await using var f = new Fixture();
        var trip = await f.MatchingTrip();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var p = Assert.Single(trip.Proposals, x => x.Status == ProposalStatus.Searching);
            await f.Handler().ApplyAsync(new TripMatchingResult(trip.Id, p.Id,
                new TripProposalCandidate(p.Id, Guid.NewGuid(), "quote", 25000, "VND", "Driver", "59A", "Bike", null)), default);
            await f.Controller(f.Rider, "Rider").Reject(trip.Id, p.Id, new VersionRequest(trip.StateVersion), default);
            Assert.Equal(ProposalStatus.RiderRejected, p.Status);
            Assert.Equal(attempt == 3 ? TripStatus.Failed : TripStatus.Matching, trip.Status);
        }
        Assert.Equal(3, trip.Proposals.Count);
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.RouteCleanup).ToListAsync());
    }

    [Fact]
    public async Task No_driver_failure_updates_trip_and_notifies_rider_idempotently()
    {
        await using var f = new Fixture();
        var trip = await f.MatchingTrip(); var p = Assert.Single(trip.Proposals);
        var result = new TripMatchingResult(trip.Id, p.Id, null, "no_driver_available");
        await f.Handler().ApplyAsync(result, default);
        var version = trip.StateVersion;
        await f.Handler().ApplyAsync(result, default);
        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.Equal("no_driver_available", trip.FailureReason);
        Assert.Equal(ProposalStatus.NoDriverFound, p.Status);
        Assert.Equal(version, trip.StateVersion);
        Assert.Equal(1, f.Realtime.Changes);
    }

    [Fact]
    public async Task Cancellation_after_driver_accept_queues_release_for_the_accepted_assignment()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip();
        await f.Controller(f.Rider, "Rider").Cancel(trip.Id, new VersionRequest(trip.StateVersion), default);
        Assert.Equal(TripStatus.Cancelled, trip.Status);
        var release = JsonSerializer.Deserialize<DriverReservationReleaseRequested>((await f.Db.OutboxMessages.SingleAsync(x => x.Kind == TripMessageKind.ReleaseDriver)).Payload)!;
        Assert.Equal(f.Driver, release.DriverId);
        Assert.Equal(trip.Id, release.TripId);
        Assert.Equal(Assert.Single(trip.Proposals).Id, release.ProposalId);
    }

    [Fact]
    public async Task Dropoff_releases_driver_in_same_commit_as_payment_without_waiting_for_payment_success()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip();
        trip.Status = TripStatus.PickedUp;
        await f.Db.SaveChangesAsync();
        await f.Controller(f.Driver, "Driver").Dropoff(trip.Id, new VersionRequest(trip.StateVersion), default);
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.Equal("Pending", trip.PaymentStatus);
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.ReleaseDriver).ToListAsync());
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.RequestPayment).ToListAsync());
    }

    [Theory]
    [InlineData("Wallet")]
    [InlineData("Cash")]
    public async Task Dropoff_queues_the_saved_method_and_releases_driver_before_payment_result(string method)
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip(); trip.Status = TripStatus.PickedUp; trip.PaymentMethod = method;
        await f.Db.SaveChangesAsync();
        await f.Controller(f.Driver, "Driver").Dropoff(trip.Id, new VersionRequest(trip.StateVersion), default);
        var queued = await f.Db.OutboxMessages.SingleAsync(x => x.Kind == TripMessageKind.RequestPayment);
        var message = System.Text.Json.JsonSerializer.Deserialize<PaymentRequested>(queued.Payload)!;
        Assert.Equal(method, message.PaymentMethod);
        Assert.Equal(trip.AgreedAmount, message.Amount);
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.Single(await f.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.ReleaseDriver).ToListAsync());
    }

    [Fact]
    public async Task Driver_reject_requires_a_new_candidate_and_new_rider_confirmation()
    {
        await using var f = new Fixture();
        var trip = await f.AcceptedTrip();
        var p = Assert.Single(trip.Proposals);
        trip.DriverId = null; trip.Status = TripStatus.AwaitingDriverResponse;
        p.Status = ProposalStatus.AwaitingDriverResponse; p.DriverResponseDeadline = f.Clock.GetUtcNow().AddSeconds(30);
        await f.Db.SaveChangesAsync();
        await f.Controller(f.Driver, "Driver").DriverReject(trip.Id, new DriverProposalRequest(p.Id, trip.StateVersion), default);
        Assert.Equal(TripStatus.Matching, trip.Status);
        var request = JsonSerializer.Deserialize<TripMatchingRequested>((await f.Db.OutboxMessages.SingleAsync(x => x.Kind == TripMessageKind.FindDriver)).Payload)!;
        Assert.Contains(f.Driver, request.ExcludedDriverIds);
        var next = trip.Proposals.Single(x => x.Status == ProposalStatus.Searching);
        await f.Handler().ApplyAsync(new TripMatchingResult(trip.Id, next.Id,
            new TripProposalCandidate(next.Id, Guid.NewGuid(), "quote2", 30000, "VND", "Other driver", "Other plate", "Other bike", null)), default);
        Assert.Equal(TripStatus.AwaitingRiderApproval, trip.Status);
        Assert.Null(next.DriverResponseDeadline);
        Assert.Equal(30000, next.Amount);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class RecordingHub : IHubContext<TripHub>
    {
        public List<(string User, string Event)> Events { get; } = [];
        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();
        public RecordingHub()
        {
            Clients = DispatchProxy.Create<IHubClients, ClientsProxy>();
            ((ClientsProxy)Clients).Events = Events;
        }
    }
    public class ClientsProxy : DispatchProxy
    {
        public List<(string User, string Event)> Events { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "User") throw new NotSupportedException();
            var proxy = DispatchProxy.Create<IClientProxy, ClientProxy>();
            ((ClientProxy)proxy).User = (string)args![0]!;
            ((ClientProxy)proxy).Events = Events;
            return proxy;
        }
    }
    public class ClientProxy : DispatchProxy
    {
        public string User { get; set; } = "";
        public List<(string User, string Event)> Events { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        { Events.Add((User, (string)args![0]!)); return Task.CompletedTask; }
    }
    private sealed class Realtime : ITripRealtimePublisher
    {
        public int Changes { get; private set; }
        public int Invitations { get; private set; }
        public Task PublishTripChangedAsync(TripEntity trip, CancellationToken ct) { Changes++; return Task.CompletedTask; }
        public Task PublishDriverInvitationAsync(TripEntity trip, TripProposal p, CancellationToken ct) { Invitations++; return Task.CompletedTask; }
        public Task PublishRideCompletedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TripDbContext Db { get; }
        public Clock Clock { get; } = new();
        public Realtime Realtime { get; } = new();
        public Guid Driver { get; } = Guid.NewGuid();
        public Guid Rider { get; } = Guid.NewGuid();
        public Fixture()
        {
            connection.Open();
            Db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, SqliteUtcModelCustomizer>().Options);
            Db.Database.EnsureCreated();
        }
        public TripMatchingResultHandler Handler() => new(Db, Realtime, new TripMatchingNotifier(Db, Clock), Clock, Options.Create(new TripWorkflowOptions()));
        public TripsController Controller(Guid user, string role) => new(Db, Realtime, new TripMatchingNotifier(Db, Clock), new TripPaymentNotifier(Db, Clock), Clock, Options.Create(new TripWorkflowOptions()))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString()), new Claim(ClaimTypes.Role, role)], "test")) } } };
        public async Task<TripEntity> MatchingTrip()
        {
            var trip = new TripEntity { RiderId = Rider, Status = TripStatus.Matching };
            trip.Proposals.Add(new TripProposal { TripId = trip.Id });
            Db.Add(trip); await Db.SaveChangesAsync(); return trip;
        }
        public async Task<TripEntity> AcceptedTrip()
        {
            var trip = await MatchingTrip(); trip.Status = TripStatus.DriverAccepted; trip.DriverId = Driver; trip.AgreedAmount = 25000;
            var p = Assert.Single(trip.Proposals); p.DriverId = Driver; p.Status = ProposalStatus.DriverAccepted;
            await Db.SaveChangesAsync(); return trip;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    // SQLite cannot compare DateTimeOffset natively. Production SQL Server can;
    // use UTC ticks only in this test provider so the deadline query is exercised.
    public sealed class SqliteUtcModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var converter = new ValueConverter<DateTimeOffset, long>(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties()))
                if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                    property.SetValueConverter(converter);
        }
    }
}
