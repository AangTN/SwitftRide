namespace SwiftRide.Trip.Domain;

/// <summary>State pattern for the aggregate. Invalid transitions fail before persistence.</summary>
public static class TripStateMachine
{
    private static readonly IReadOnlyDictionary<TripStatus, TripState> States = new Dictionary<TripStatus, TripState>
    {
        [TripStatus.Requested] = new(TripStatus.Matching, TripStatus.Cancelled, TripStatus.Failed),
        [TripStatus.Matching] = new(TripStatus.AwaitingRiderApproval, TripStatus.Cancelled, TripStatus.Failed),
        [TripStatus.AwaitingRiderApproval] = new(TripStatus.AwaitingDriverResponse, TripStatus.Cancelled),
        [TripStatus.AwaitingDriverResponse] = new(TripStatus.Matching, TripStatus.DriverAccepted, TripStatus.Cancelled, TripStatus.Failed),
        [TripStatus.DriverAccepted] = new(TripStatus.EnRoute, TripStatus.Cancelled),
        [TripStatus.EnRoute] = new(TripStatus.PickedUp, TripStatus.Cancelled),
        [TripStatus.PickedUp] = new(TripStatus.DroppedOff),
        [TripStatus.DroppedOff] = new(TripStatus.PaymentPending),
        [TripStatus.PaymentPending] = new(TripStatus.Paid, TripStatus.Failed),
        [TripStatus.Paid] = new(), [TripStatus.Cancelled] = new(), [TripStatus.Failed] = new()
    };

    public static void Move(Trip trip, TripStatus target, Guid? actorId, DateTimeOffset now, string? reason = null)
    {
        if (trip.Status == target) return;
        if (!States[trip.Status].CanMoveTo(target))
            throw new TripRuleViolation("invalid_trip_transition", $"Cannot change a trip from {trip.Status} to {target}.");
        var previous = trip.Status;
        trip.Status = target;
        trip.StateVersion++;
        trip.UpdatedAt = now;
        trip.History.Add(new() { TripId = trip.Id, FromStatus = previous, ToStatus = target, ActorUserId = actorId, Reason = reason, CreatedAt = now });
    }

    private sealed class TripState(params TripStatus[] next)
    {
        public bool CanMoveTo(TripStatus candidate) => next.Contains(candidate);
    }
}

public sealed class TripRuleViolation(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
