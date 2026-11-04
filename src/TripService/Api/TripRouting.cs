using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

internal static class TripRouting
{
    public static IQueryable<TripEntity> WithRoutePreview(this IQueryable<TripEntity> trips, Guid proposalId) =>
        trips.Where(t =>
            (t.Status == TripStatus.Matching || t.Status == TripStatus.AwaitingRiderApproval ||
                t.Status == TripStatus.AwaitingDriverResponse) &&
            t.Proposals.Any(p => p.Id == proposalId &&
                (p.Status == ProposalStatus.Searching || p.Status == ProposalStatus.AwaitingRiderApproval ||
                    p.Status == ProposalStatus.AwaitingDriverResponse)));

    public static bool IsCurrentStage(TripStatus status, TripRouteStage stage) => stage switch
    {
        TripRouteStage.ToPickup => status is TripStatus.DriverAccepted or TripStatus.EnRoute,
        TripRouteStage.ToDestination => status == TripStatus.PickedUp,
        _ => false
    };

    public static TripRouteRequested? ActiveStage(TripEntity trip)
    {
        var proposal = trip.Proposals.SingleOrDefault(p => p.Status == ProposalStatus.DriverAccepted);
        if (proposal is null || trip.DriverId is not Guid driver) return null;
        var stage = trip.Status == TripStatus.PickedUp ? TripRouteStage.ToDestination : TripRouteStage.ToPickup;
        if (!IsCurrentStage(trip.Status, stage)) return null;
        return new(trip.Id, proposal.Id, driver, stage == TripRouteStage.ToPickup ? 1 : 2, stage,
            trip.PickupLatitude, trip.PickupLongitude, trip.DestinationLatitude, trip.DestinationLongitude, trip.VehicleType);
    }

    public static void EnqueueStage(TripDbContext db, TripEntity trip, DateTimeOffset now)
    {
        if (ActiveStage(trip) is { } route)
            TripOutboxMessage.Enqueue(db, TripMessageKind.RouteStage, trip.Id, route.ProposalId, route, now);
    }

    public static void EnqueueCleanup(TripDbContext db, TripEntity trip, DateTimeOffset now) =>
        TripOutboxMessage.Enqueue(db, TripMessageKind.RouteCleanup, trip.Id, null, new TripRouteCleanup(trip.Id), now);
}

public sealed class TripRoutesUpdatedConsumer(TripDbContext db, IHubContext<TripHub> hub) : IConsumer<TripRoutesUpdated>
{
    public async Task Consume(ConsumeContext<TripRoutesUpdated> context)
    {
        var message = context.Message;
        var trip = await db.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.Id == message.TripId &&
            t.DriverId == message.DriverId &&
            t.Proposals.Any(p => p.Id == message.ProposalId && p.Status == ProposalStatus.DriverAccepted),
            context.CancellationToken);
        if (trip is null || !TripRouting.IsCurrentStage(trip.Status, message.Stage)) return;
        await hub.Clients.Users(trip.RiderId.ToString(), message.DriverId.ToString())
            .SendAsync("TripRoutesUpdated", message, context.CancellationToken);
    }
}

public sealed class TripRoutePreviewConsumer(TripDbContext db, IHubContext<TripHub> hub) : IConsumer<TripRoutePreview>
{
    public Task Consume(ConsumeContext<TripRoutePreview> context) => ForwardAsync(context.Message, context.CancellationToken);

    public async Task ForwardAsync(TripRoutePreview message, CancellationToken ct)
    {
        if (message.ToDestination is null) return;
        var riderId = await db.Trips.WithRoutePreview(message.ProposalId)
            .Where(t => t.Id == message.TripId).Select(t => (Guid?)t.RiderId).SingleOrDefaultAsync(ct);
        if (riderId is Guid rider)
            await hub.Clients.User(rider.ToString()).SendAsync("TripRoutePreview", message, ct);
    }
}
