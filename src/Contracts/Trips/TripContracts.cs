namespace SwiftRide.Contracts.Trips;

public sealed record TripProposalCandidate(Guid ProposalId, Guid DriverId, string QuoteId, decimal Amount, string Currency,
    string DriverName, string PlateNumber, string VehicleName, string? VehicleImageKey, string? DriverAvatarImageKey = null, FareBreakdown? Fare = null);

public sealed record FareBreakdown(double DistanceMeters, int DurationSeconds, decimal PerKilometer,
    decimal PerMinute, decimal Surge, decimal Toll, decimal PromotionFactor, decimal TaxRate);

public sealed record PaymentResult(Guid TripId, string PaymentId, string Status);

public sealed record TripMatchingRequested(Guid TripId, Guid ProposalId, IReadOnlyCollection<Guid> ExcludedDriverIds,
    double PickupLatitude = 0, double PickupLongitude = 0, double DestinationLatitude = 0,
    double DestinationLongitude = 0, string VehicleType = "Motorbike");
public sealed record DriverReservationReleaseRequested(Guid TripId, Guid ProposalId, Guid DriverId, string Reason);
public sealed record DriverReservationConfirmed(Guid TripId, Guid ProposalId, Guid DriverId);
public sealed record TripMatchingResult(Guid TripId, Guid ProposalId, TripProposalCandidate? Candidate, string? FailureCode = null);
public sealed record DriverGpsUpdated(Guid TripId, Guid ProposalId, Guid DriverId, double Latitude, double Longitude, DateTimeOffset RecordedAt);
public sealed record PaymentRequested(string PaymentId, Guid TripId, Guid RiderId, Guid DriverId, decimal Amount, string Currency);

public enum TripRouteStage { ToPickup, ToDestination }

public sealed record TripRouteRequested(Guid TripId, Guid ProposalId, Guid DriverId, int StageVersion,
    TripRouteStage Stage, double PickupLatitude, double PickupLongitude, double DestinationLatitude,
    double DestinationLongitude, string VehicleType);

public sealed record TripRouteCleanup(Guid TripId);

public sealed record RouteLeg(string Polyline, double DistanceMeters, int DurationSeconds, DateTimeOffset CalculatedAt);

public sealed record TripRoutesUpdated(Guid TripId, Guid ProposalId, Guid DriverId, int StageVersion,
    TripRouteStage Stage, RouteLeg? ToPickup, RouteLeg? ToDestination, int? PickupEtaMinutes = null);

public sealed record GetTripRoutes(TripRouteRequested Route);

public sealed record GetTripRoutePreview(Guid TripId, Guid ProposalId);
public sealed record TripRoutePreview(Guid TripId, Guid ProposalId, RouteLeg? ToDestination, int? PickupEtaMinutes = null);
