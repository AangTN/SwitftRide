namespace SwiftRide.Contracts.Trips;

public sealed record TripProposalCandidate(Guid ProposalId, Guid DriverId, string QuoteId, decimal Amount, string Currency,
    string DriverName, string PlateNumber, string VehicleName, string? VehicleImageKey);

public sealed record PaymentResult(Guid TripId, string PaymentId, string Status);

public sealed record TripMatchingRequested(Guid TripId, Guid ProposalId, IReadOnlyCollection<Guid> ExcludedDriverIds);
public sealed record DriverReservationReleaseRequested(Guid TripId, Guid ProposalId, Guid DriverId, string Reason);
public sealed record PaymentRequested(string PaymentId, Guid TripId, Guid RiderId, Guid DriverId, decimal Amount, string Currency);
