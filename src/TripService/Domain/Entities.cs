namespace SwiftRide.Trip.Domain;

public enum TripStatus { Requested, AwaitingConfirmation, Matching, DriverAccepted, EnRoute, PickedUp, DroppedOff, PaymentPending, Paid, Cancelled, Failed }

public sealed class Trip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RiderId { get; set; }
    public Guid? DriverId { get; set; }
    public string PickupAddress { get; set; } = "";
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public string DestinationAddress { get; set; } = "";
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Requested;
    public string? QuoteId { get; set; }
    public decimal? AgreedAmount { get; set; }
    public string Currency { get; set; } = "VND";
    public string? DriverNameSnapshot { get; set; }
    public string? PlateNumberSnapshot { get; set; }
    public string? VehicleNameSnapshot { get; set; }
    public string? VehicleImageKeySnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DriverAcceptedAt { get; set; }
    public DateTimeOffset? PickedUpAt { get; set; }
    public DateTimeOffset? DroppedOffAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<TripStatusHistory> History { get; set; } = [];
}

public sealed class TripStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public TripStatus? FromStatus { get; set; }
    public TripStatus ToStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
