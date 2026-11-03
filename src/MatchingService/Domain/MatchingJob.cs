namespace SwiftRide.Matching.Domain;

// One document per proposal is a durable inbox and result outbox. Single-document
// atomic updates work on standalone MongoDB as well as replica sets.
public sealed class MatchingJob
{
    public string Id { get; set; } = "";
    public string TripId { get; set; } = "";
    public string? DriverId { get; set; }
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public bool ResultSent { get; set; }
    public bool Released { get; set; }
    public bool Confirmed { get; set; }
    public bool WorkPending { get; set; }
    public long Revision { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public string? LeaseId { get; set; }
}
