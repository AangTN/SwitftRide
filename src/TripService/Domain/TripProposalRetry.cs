namespace SwiftRide.Trip.Domain;

public static class TripProposalRetry
{
    public static void Advance(Trip trip, int maxAttempts, Guid? actor, DateTimeOffset now, string reason)
    {
        if (trip.Proposals.Count(p => p.DriverId is not null) >= maxAttempts)
        {
            TripStateMachine.Move(trip, TripStatus.Failed, actor, now, "driver_attempt_limit_reached");
            return;
        }

        TripStateMachine.Move(trip, TripStatus.Matching, actor, now, reason);
        trip.Proposals.Add(new TripProposal { TripId = trip.Id, CreatedAt = now, Status = ProposalStatus.Searching });
    }
}
