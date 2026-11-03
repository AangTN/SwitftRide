using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Application;

/// <summary>Implemented by the integration with Matching. The API never trusts a driver id supplied by a rider.</summary>
public interface ITripMatcher
{
    Task<TripProposalCandidate?> FindNextAsync(TripEntity trip, IReadOnlySet<Guid> excludedDriverIds, CancellationToken ct);
    Task ReleaseDriverAsync(Guid driverId, Guid tripId, CancellationToken ct);
}

public interface IPaymentStarter
{
    Task<string> StartAsync(TripEntity trip, CancellationToken ct);
}

public sealed class NoMatchingCandidateException : Exception
{
    public NoMatchingCandidateException() : base("No eligible driver is currently available.") { }
}
