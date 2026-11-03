using SwiftRide.Trip.Infrastructure;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

public interface ITripMatchingNotifier
{
    Task RequestOpenProposalsAsync(TripEntity trip, CancellationToken ct);
    Task ReleaseAsync(TripProposal proposal, string reason, CancellationToken ct);
}

/// <summary>Queues messages in the same database transaction as the Trip changes.</summary>
public sealed class TripMatchingNotifier(TripDbContext db, TimeProvider clock) : ITripMatchingNotifier
{
    public Task RequestOpenProposalsAsync(TripEntity trip, CancellationToken ct)
    {
        var excluded = trip.Proposals.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).ToArray();
        foreach (var proposal in trip.Proposals.Where(x => x.Status == ProposalStatus.Searching))
            TripOutboxMessage.Enqueue(db, TripMessageKind.FindDriver, trip.Id, proposal.Id,
                new TripMatchingRequested(trip.Id, proposal.Id, excluded), clock.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(TripProposal proposal, string reason, CancellationToken ct)
    {
        if (proposal.DriverId is Guid driver)
            TripOutboxMessage.Enqueue(db, TripMessageKind.ReleaseDriver, proposal.TripId, proposal.Id,
                new DriverReservationReleaseRequested(proposal.TripId, proposal.Id, driver, reason), clock.GetUtcNow());
        return Task.CompletedTask;
    }
}
