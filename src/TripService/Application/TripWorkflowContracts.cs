using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;

namespace SwiftRide.Trip.Application;

/// <summary>Implemented by the integration with Matching. The API never trusts a driver id supplied by a rider.</summary>
public interface ITripMatcher
{
    Task<TripProposalCandidate?> FindNextAsync(Trip trip, IReadOnlySet<Guid> excludedDriverIds, CancellationToken ct);
    Task ReleaseDriverAsync(Guid driverId, Guid tripId, CancellationToken ct);
}

public interface IPaymentStarter
{
    Task<string> StartAsync(Trip trip, CancellationToken ct);
}

public sealed class NoMatchingCandidateException : Exception
{
    public NoMatchingCandidateException() : base("No eligible driver is currently available.") { }
}
