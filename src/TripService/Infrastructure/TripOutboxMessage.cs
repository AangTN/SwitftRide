using System.Text.Json;

namespace SwiftRide.Trip.Infrastructure;

public enum TripMessageKind { FindDriver, ReleaseDriver, RequestPayment, ConfirmDriver }
public enum TripMessageStatus { Pending, Sent, Failed, Cancelled }

public sealed class TripOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Guid? ProposalId { get; set; }
    public TripMessageKind Kind { get; set; }
    public string Payload { get; set; } = "";
    public TripMessageStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }

    // Enqueue only. The caller saves this with the aggregate in one SaveChanges transaction.
    public static void Enqueue<T>(TripDbContext db, TripMessageKind kind, Guid tripId,
        Guid? proposalId, T payload, DateTimeOffset now)
    {
        db.OutboxMessages.Add(new TripOutboxMessage
        {
            TripId = tripId, ProposalId = proposalId, Kind = kind,
            Payload = JsonSerializer.Serialize(payload), CreatedAt = now.UtcDateTime,
            NextAttemptAt = now.UtcDateTime
        });
    }
}
