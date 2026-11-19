using System.Reflection;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using TripEntity = SwiftRide.Trip.Domain.Trip;
using Xunit;

namespace SwiftRide.Trip.Tests;

public sealed class TripOutboxTests
{
    [Fact]
    public async Task Existing_trip_can_persist_new_history_and_proposals()
    {
        await using var fixture = new Fixture();
        var trip = await fixture.EnqueueMatching();
        fixture.Db.ChangeTracker.Clear();
        trip = await fixture.Db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleAsync();
        TripStateMachine.Move(trip, TripStatus.AwaitingRiderApproval, null, fixture.Clock.GetUtcNow());
        trip.Proposals.Add(new TripProposal { TripId = trip.Id });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(2, await fixture.Db.TripProposals.CountAsync());
        Assert.Single(await fixture.Db.TripStatusHistory.ToListAsync());
    }

    [Fact]
    public async Task Late_matching_result_queues_release_instead_of_reopening_closed_trip()
    {
        await using var fixture = new Fixture();
        var trip = await fixture.EnqueueMatching();
        var proposal = Assert.Single(trip.Proposals);
        TripStateMachine.Move(trip, TripStatus.Failed, null, fixture.Clock.GetUtcNow(), "matching_delivery_failed");
        proposal.Status = ProposalStatus.Cancelled;
        await fixture.Db.SaveChangesAsync();
        var callback = new MatchingCallbacksController(fixture.Db, new SilentRealtime(), new TripMatchingNotifier(fixture.Db, fixture.Clock),
            fixture.Clock, Options.Create(new TripWorkflowOptions()));
        var result = await callback.Offer(trip.Id, new SwiftRide.Contracts.Trips.TripProposalCandidate(proposal.Id, Guid.NewGuid(), "quote", 100, "VND", "Driver", "Plate", "Bike", null), default);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.Equal(TripMessageKind.ReleaseDriver, (await fixture.Db.OutboxMessages.SingleAsync(x => x.Kind == TripMessageKind.ReleaseDriver)).Kind);
    }

    [Fact]
    public async Task Rollback_does_not_leave_a_trip_proposal_or_outbox_message()
    {
        await using var fixture = new Fixture();
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            var trip = await fixture.EnqueueMatching();
            Assert.NotEqual(Guid.Empty, trip.Id);
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.Trips.ToListAsync());
        Assert.Empty(await fixture.Db.TripProposals.ToListAsync());
        Assert.Empty(await fixture.Db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Three_retries_wait_five_seconds_and_then_fail_matching_without_creating_more_proposals()
    {
        await using var fixture = new Fixture();
        var trip = await fixture.EnqueueMatching();
        var id = (await fixture.Db.OutboxMessages.SingleAsync()).Id;
        fixture.Transport.Fail = true;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            fixture.Db.ChangeTracker.Clear(); // simulate a new worker scope/restart
            Assert.True(await fixture.Dispatcher().DispatchOneAsync(default));
            var message = await fixture.Db.OutboxMessages.SingleAsync(x => x.Id == id);
            Assert.Equal(id, message.Id);
            Assert.Equal(attempt, message.Attempts);
            Assert.Equal(attempt == 4 ? TripMessageStatus.Failed : TripMessageStatus.Pending, message.Status);
            if (attempt < 4)
            {
                Assert.False(await fixture.Dispatcher().DispatchOneAsync(default));
                fixture.Clock.Advance(4);
                Assert.False(await fixture.Dispatcher().DispatchOneAsync(default));
                fixture.Clock.Advance(1);
            }
        }
        var saved = await fixture.Db.Trips.Include(x => x.Proposals).SingleAsync(x => x.Id == trip.Id);
        Assert.Equal(TripStatus.Failed, saved.Status);
        Assert.Equal("matching_delivery_failed", saved.FailureReason);
        Assert.Equal(ProposalStatus.Cancelled, Assert.Single(saved.Proposals).Status);
        Assert.Equal(4, fixture.Transport.Calls);
        Assert.Single(await fixture.Db.OutboxMessages.Where(x => x.Kind == TripMessageKind.RouteCleanup).ToListAsync());
    }

    [Fact]
    public async Task Recovery_sends_once_and_cancelled_proposal_is_never_sent()
    {
        await using var fixture = new Fixture();
        await fixture.EnqueueMatching();
        fixture.Transport.Fail = true;
        await fixture.Dispatcher().DispatchOneAsync(default);
        fixture.Transport.Fail = false;
        fixture.Clock.Advance(5);
        await fixture.Dispatcher().DispatchOneAsync(default);
        Assert.Equal(TripMessageStatus.Sent, (await fixture.Db.OutboxMessages.SingleAsync()).Status);
        Assert.False(await fixture.Dispatcher().DispatchOneAsync(default));
        var cancelled = await fixture.EnqueueMatching();
        TripStateMachine.Move(cancelled, TripStatus.Cancelled, null, fixture.Clock.GetUtcNow());
        await fixture.Db.SaveChangesAsync();
        await fixture.Dispatcher().DispatchOneAsync(default);
        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Equal(TripMessageStatus.Cancelled, (await fixture.Db.OutboxMessages.SingleAsync(x => x.TripId == cancelled.Id)).Status);
    }

    [Fact]
    public async Task Exhausted_payment_delivery_is_unknown_then_payment_failure_keeps_ride_awaiting_settlement()
    {
        await using var fixture = new Fixture();
        var trip = new TripEntity { Status = TripStatus.PaymentPending, PaymentStatus = "Pending", PaymentId = "payment-1", DriverId = Guid.NewGuid(), AgreedAmount = 100 };
        fixture.Db.Trips.Add(trip);
        await new TripPaymentNotifier(fixture.Db, fixture.Clock).RequestAsync(trip, default);
        await fixture.Db.SaveChangesAsync();
        fixture.Transport.Fail = true;
        for (var i = 0; i < 4; i++)
        {
            await fixture.Dispatcher().DispatchOneAsync(default);
            fixture.Clock.Advance(5);
        }
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.Equal("DeliveryUnknown", trip.PaymentStatus);
        var callback = new PaymentCallbacksController(new TripPaymentResultHandler(fixture.Db, new SilentRealtime(), fixture.Clock));
        await callback.PaymentResult(trip.Id, new PaymentResultRequest(trip.PaymentId, "Failed"), default);
        Assert.Equal("Failed", trip.PaymentStatus);
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.IsType<OkResult>(await callback.PaymentResult(trip.Id, new PaymentResultRequest(trip.PaymentId, "Succeeded"), default));
        Assert.Equal(TripStatus.Paid, trip.Status);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    public class Transport : DispatchProxy
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            return Fail ? Task.FromException(new IOException("Broker unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class SilentRealtime : ITripRealtimePublisher
    {
        public Task PublishTripChangedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
        public Task PublishDriverInvitationAsync(TripEntity trip, TripProposal proposal, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRideCompletedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TripDbContext Db { get; }
        public Clock Clock { get; } = new();
        private readonly IPublishEndpoint bus = DispatchProxy.Create<IPublishEndpoint, Transport>();
        public Transport Transport => (Transport)bus;

        public Fixture()
        {
            connection.Open();
            Db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
        }

        public async Task<TripEntity> EnqueueMatching()
        {
            var trip = new TripEntity { RiderId = Guid.NewGuid(), Status = TripStatus.Matching };
            trip.Proposals.Add(new TripProposal { TripId = trip.Id, Status = ProposalStatus.Searching });
            Db.Trips.Add(trip);
            await new TripMatchingNotifier(Db, Clock).RequestOpenProposalsAsync(trip, default);
            await Db.SaveChangesAsync();
            return trip;
        }

        public TripOutboxDispatcher Dispatcher() => new(Db, bus, Clock, new SilentRealtime(), Options.Create(new TripOutboxOptions()), NullLogger<TripOutboxDispatcher>.Instance);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
