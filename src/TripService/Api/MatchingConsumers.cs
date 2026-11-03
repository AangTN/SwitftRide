using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

public sealed class TripMatchingResultConsumer(TripMatchingResultHandler handler) : IConsumer<TripMatchingResult>
{
    public async Task Consume(ConsumeContext<TripMatchingResult> context) =>
        await handler.ApplyAsync(context.Message, context.CancellationToken);
}

public sealed class DriverGpsUpdatedConsumer(TripDbContext db, IHubContext<TripHub> hub) : IConsumer<DriverGpsUpdated>
{
    public Task Consume(ConsumeContext<DriverGpsUpdated> context) => ForwardAsync(context.Message, context.CancellationToken);

    public async Task ForwardAsync(DriverGpsUpdated m, CancellationToken ct)
    {
        // Never trust just the trip id in a location event. Only the assigned driver's
        // accepted proposal can disclose a location to this trip's rider.
        var rider = await db.Trips.Where(t => t.Id == m.TripId && t.DriverId == m.DriverId &&
            (t.Status == TripStatus.DriverAccepted || t.Status == TripStatus.EnRoute || t.Status == TripStatus.PickedUp) &&
            t.Proposals.Any(p => p.Id == m.ProposalId && p.DriverId == m.DriverId && p.Status == ProposalStatus.DriverAccepted))
            .Select(t => (Guid?)t.RiderId).SingleOrDefaultAsync(ct);
        if (rider is Guid riderId)
            await hub.Clients.User(riderId.ToString()).SendAsync("DriverLocationUpdated", m, ct);
    }
}
