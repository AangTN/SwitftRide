namespace SwiftRide.Contracts.Trips;

public sealed record TripProposalCandidate(Guid ProposalId, Guid DriverId, string QuoteId, decimal Amount, string Currency,
    string DriverName, string PlateNumber, string VehicleName, string? VehicleImageKey);

public sealed record PaymentResult(Guid TripId, string PaymentId, string Status);

public sealed record TripMatchingRequested(Guid TripId, Guid ProposalId, IReadOnlyCollection<Guid> ExcludedDriverIds,
    double PickupLatitude = 0, double PickupLongitude = 0, double DestinationLatitude = 0,
    double DestinationLongitude = 0, string VehicleType = "Motorbike");
public sealed record DriverReservationReleaseRequested(Guid TripId, Guid ProposalId, Guid DriverId, string Reason);
public sealed record DriverReservationConfirmed(Guid TripId, Guid ProposalId, Guid DriverId);
public sealed record TripMatchingResult(Guid TripId, Guid ProposalId, TripProposalCandidate? Candidate, string? FailureCode = null);
public sealed record DriverGpsUpdated(Guid TripId, Guid ProposalId, Guid DriverId, double Latitude, double Longitude, DateTimeOffset RecordedAt);
public sealed record PaymentRequested(string PaymentId, Guid TripId, Guid RiderId, Guid DriverId, decimal Amount, string Currency);
