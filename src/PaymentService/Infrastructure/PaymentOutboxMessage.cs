namespace SwiftRide.Payment.Infrastructure;

public enum PaymentDeliveryStatus { Pending, Sent, Failed }

public sealed class PaymentOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "Result";
    public string Payload { get; set; } = "";
    public PaymentDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
}
