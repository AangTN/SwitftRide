using MassTransit;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;

namespace SwiftRide.Trip.Api;

public interface ITripMatchingNotifier
{
    Task RequestOpenProposalsAsync(Trip trip, CancellationToken ct);
    Task ReleaseAsync(TripProposal proposal, string reason, CancellationToken ct);
}

/// <summary>Publishes only after the Trip transaction commits. Matching treats ProposalId as an idempotency key.</summary>
public sealed class TripMatchingNotifier(IPublishEndpoint bus) : ITripMatchingNotifier
{
    public Task RequestOpenProposalsAsync(Trip trip, CancellationToken ct)
    {
        var excluded = trip.Proposals.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).ToArray();
        return Task.WhenAll(trip.Proposals.Where(x => x.Status == ProposalStatus.Searching)
            .Select(x => bus.Publish(new TripMatchingRequested(trip.Id, x.Id, excluded), ct)));
    }

    public Task ReleaseAsync(TripProposal proposal, string reason, CancellationToken ct) => proposal.DriverId is Guid driver
        ? bus.Publish(new DriverReservationReleaseRequested(proposal.TripId, proposal.Id, driver, reason), ct)
        : Task.CompletedTask;
}

public sealed class NoopTripMatchingNotifier : ITripMatchingNotifier
{
    public Task RequestOpenProposalsAsync(Trip trip, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseAsync(TripProposal proposal, string reason, CancellationToken ct) => Task.CompletedTask;
}
