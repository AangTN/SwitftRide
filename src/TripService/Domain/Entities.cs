namespace SwiftRide.Trip.Domain;

public enum TripStatus { Requested, Matching, AwaitingRiderApproval, AwaitingDriverResponse, DriverAccepted, EnRoute, PickedUp, DroppedOff, PaymentPending, Paid, Cancelled, Failed }
public enum ProposalStatus { Searching, AwaitingRiderApproval, RiderRejected, RiderTimedOut, AwaitingDriverResponse, DriverRejected, DriverTimedOut, DriverAccepted, Cancelled, NoDriverFound }

public sealed class Trip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RiderId { get; set; }
    public string? RiderNameSnapshot { get; set; }
    public string? RiderPhoneSnapshot { get; set; }
    public Guid? DriverId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string PickupAddress { get; set; } = "";
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public string DestinationAddress { get; set; } = "";
    public string VehicleType { get; set; } = "Motorbike";
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Requested;
    public int StateVersion { get; set; }
    public string? QuoteId { get; set; }
    public decimal? AgreedAmount { get; set; }
    public string Currency { get; set; } = "VND";
    public string? DriverNameSnapshot { get; set; }
    public string? DriverAvatarImageKeySnapshot { get; set; }
    public string? PlateNumberSnapshot { get; set; }
    public string? VehicleNameSnapshot { get; set; }
    public string? VehicleImageKeySnapshot { get; set; }
    public string? PaymentId { get; set; }
    public string? PaymentStatus { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DriverAcceptedAt { get; set; }
    public DateTimeOffset? PickedUpAt { get; set; }
    public DateTimeOffset? DroppedOffAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<TripStatusHistory> History { get; set; } = [];
    public List<TripProposal> Proposals { get; set; } = [];
}

public sealed class TripProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Guid? DriverId { get; set; }
    public string? QuoteId { get; set; }
    public decimal? Amount { get; set; }
    public double? DistanceMeters { get; set; }
    public int? EstimatedDurationSeconds { get; set; }
    public string? PricingSnapshotJson { get; set; }
    public string Currency { get; set; } = "VND";
    public string? DriverNameSnapshot { get; set; }
    public string? DriverAvatarImageKeySnapshot { get; set; }
    public string? PlateNumberSnapshot { get; set; }
    public string? VehicleNameSnapshot { get; set; }
    public string? VehicleImageKeySnapshot { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Searching;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RiderApprovalDeadline { get; set; }
    public DateTimeOffset? RiderRespondedAt { get; set; }
    public DateTimeOffset? DriverResponseDeadline { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class TripStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public TripStatus? FromStatus { get; set; }
    public TripStatus ToStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
