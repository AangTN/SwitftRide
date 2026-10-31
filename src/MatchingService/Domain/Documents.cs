namespace SwiftRide.Matching.Domain;

public sealed class DriverProfile
{
    public string Id { get; set; } = ""; // Auth user UUID; no cross-database FK.
    public string LegalName { get; set; } = "";
    public string PlateNumber { get; set; } = "";
    public string VehicleName { get; set; } = "";
    public string VehicleType { get; set; } = "Motorbike";
    public string? VehicleImageKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class DriverAvailability
{
    public string Id { get; set; } = ""; // Auth user UUID in canonical string form.
    public string Status { get; set; } = "Offline";
    public GeoPoint? Location { get; set; }
    public DateTime? LocationUpdatedAt { get; set; }
    public string? CurrentTripId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class GeoPoint
{
    public string Type { get; set; } = "Point";
    public double[] Coordinates { get; set; } = []; // GeoJSON order: longitude, latitude.
}

public sealed class FareQuote
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TripId { get; set; } = "";
    public double DistanceMeters { get; set; }
    public int EstimatedDurationSeconds { get; set; }
    public PricingSnapshot PricingSnapshot { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "VND";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PricingSnapshot
{
    public decimal PerKilometer { get; set; }
    public decimal PerMinute { get; set; }
    public decimal Surge { get; set; } = 1;
    public decimal Toll { get; set; }
    public decimal PromotionFactor { get; set; } = 1;
    public decimal TaxRate { get; set; }
}
