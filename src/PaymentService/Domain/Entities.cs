namespace SwiftRide.Payment.Domain;

public enum PaymentStatus { Pending, Processing, Succeeded, Failed }
public enum EntrySide { Debit, Credit }

public sealed class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Guid RiderId { get; set; }
    public Guid DriverId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public PaymentStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LedgerAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    public string Currency { get; set; } = "VND";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LedgerTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public EntrySide Side { get; set; }
    public decimal Amount { get; set; }
}
