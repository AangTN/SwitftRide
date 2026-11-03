namespace SwiftRide.Contracts.Realtime;

public sealed record TripChanged(Guid TripId, string Status, int StateVersion);
public sealed record DriverInvitation(Guid TripId, Guid ProposalId, DateTimeOffset ExpiresAt);
public sealed record RideCompleted(Guid TripId, string PaymentId);
