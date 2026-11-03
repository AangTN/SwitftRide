using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

public sealed class TripOutboxOptions
{
    public int RetryCount { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 5;
    public int PublishTimeoutSeconds { get; init; } = 5;
}

public sealed class TripOutboxDispatcher(TripDbContext db, IPublishEndpoint bus, TimeProvider clock, ITripRealtimePublisher realtime,
    IOptions<TripOutboxOptions> options, ILogger<TripOutboxDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var settings = options.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        // SQL Server row locks keep concurrent dispatchers from publishing the same pending row.
        // A crash after broker acknowledgement may still redeliver: consumers must deduplicate.
        var message = db.Database.IsSqlServer()
            ? await db.OutboxMessages.FromSqlInterpolated($"SELECT TOP (1) * FROM outbox_messages WITH (UPDLOCK, READPAST, ROWLOCK) WHERE status = 'Pending' AND next_attempt_at <= {now.UtcDateTime} ORDER BY next_attempt_at, created_at").FirstOrDefaultAsync(ct)
            : await db.OutboxMessages.Where(x => x.Status == TripMessageStatus.Pending && x.NextAttemptAt <= now.UtcDateTime)
                .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (message is null) return false;

        var trip = await db.Trips.Include(x => x.Proposals).Include(x => x.History)
            .SingleAsync(x => x.Id == message.TripId, ct);
        var proposal = trip.Proposals.SingleOrDefault(x => x.Id == message.ProposalId);
        var originalVersion = trip.StateVersion;
        if ((message.Kind == TripMessageKind.FindDriver &&
                (trip.Status != TripStatus.Matching || proposal?.Status != ProposalStatus.Searching)) ||
            (message.Kind == TripMessageKind.RequestPayment &&
                (trip.Status != TripStatus.PaymentPending || trip.PaymentStatus is "Succeeded" or "Failed")))
        {
            message.Status = TripMessageStatus.Cancelled;
        }
        else
        {
            message.Attempts++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.PublishTimeoutSeconds));
            try
            {
                switch (message.Kind)
                {
                    case TripMessageKind.FindDriver:
                        // Hydrate route data from Trip as well, so pending outbox rows
                        // written by the older contract (ids only) survive this upgrade.
                        var request = JsonSerializer.Deserialize<TripMatchingRequested>(message.Payload)!;
                        await bus.Publish(request with
                        {
                            PickupLatitude = trip.PickupLatitude, PickupLongitude = trip.PickupLongitude,
                            DestinationLatitude = trip.DestinationLatitude, DestinationLongitude = trip.DestinationLongitude,
                            VehicleType = trip.VehicleType
                        }, c => c.MessageId = message.Id, timeout.Token);
                        break;
                    case TripMessageKind.ReleaseDriver:
                        await PublishAsync<DriverReservationReleaseRequested>(message, timeout.Token);
                        break;
                    case TripMessageKind.ConfirmDriver:
                        await PublishAsync<DriverReservationConfirmed>(message, timeout.Token);
                        break;
                    case TripMessageKind.RequestPayment:
                        await PublishAsync<PaymentRequested>(message, timeout.Token);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown outbox message kind.");
                }
                message.Status = TripMessageStatus.Sent;
                message.SentAt = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Persist only exception type; transport error messages can contain credentials.
                message.LastError = ex.GetType().Name;
                message.NextAttemptAt = clock.GetUtcNow().UtcDateTime.AddSeconds(settings.RetryDelaySeconds);
                if (message.Attempts >= 1 + settings.RetryCount)
                {
                    message.Status = TripMessageStatus.Failed;
                    if (message.Kind == TripMessageKind.FindDriver)
                    {
                        proposal!.Status = ProposalStatus.Cancelled;
                        proposal.ResolvedAt = clock.GetUtcNow();
                        TripStateMachine.Move(trip, TripStatus.Failed, null, clock.GetUtcNow(), "matching_delivery_failed");
                    }
                    else if (message.Kind == TripMessageKind.RequestPayment && trip.PaymentStatus == "Pending")
                    {
                        // No broker acknowledgement does not prove that Payment has not charged.
                        trip.PaymentStatus = "DeliveryUnknown";
                        trip.StateVersion++;
                        trip.UpdatedAt = clock.GetUtcNow();
                    }
                }
                logger.LogWarning("Outbox {MessageId} for trip {TripId}: attempt {Attempt}, status {Status}, error {ErrorType}",
                    message.Id, message.TripId, message.Attempts, message.Status, message.LastError);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (trip.StateVersion != originalVersion)
        {
            try { await realtime.PublishTripChangedAsync(trip, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Trip {TripId} notification failed: {ErrorType}; clients can reload the snapshot", trip.Id, ex.GetType().Name);
            }
        }
        return true;
    }

    private Task PublishAsync<T>(TripOutboxMessage message, CancellationToken ct) where T : class =>
        bus.Publish(JsonSerializer.Deserialize<T>(message.Payload) ?? throw new InvalidOperationException("Empty outbox payload."),
            context => context.MessageId = message.Id, ct);
}

public sealed class TripOutboxWorker(IServiceScopeFactory scopes, ILogger<TripOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<TripOutboxDispatcher>().DispatchOneAsync(stoppingToken))
                    continue;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Outbox dispatcher error {ErrorType}", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }
}
