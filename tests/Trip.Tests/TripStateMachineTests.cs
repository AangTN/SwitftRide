using SwiftRide.Trip.Domain;
using Xunit;

namespace SwiftRide.Trip.Tests;

public sealed class TripStateMachineTests
{
    [Fact]
    public void Valid_transition_records_history_and_increments_version()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Status = TripStatus.Matching };
        var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        TripStateMachine.Move(trip, TripStatus.AwaitingRiderApproval, actor, now);
        Assert.Equal(TripStatus.AwaitingRiderApproval, trip.Status);
        Assert.Equal(1, trip.StateVersion);
        var history = Assert.Single(trip.History);
        Assert.Equal(TripStatus.Matching, history.FromStatus);
        Assert.Equal(actor, history.ActorUserId);
    }

    [Fact]
    public void Invalid_transition_is_rejected()
    {
        var trip = new Trip { Status = TripStatus.Requested };
        var error = Assert.Throws<TripRuleViolation>(() => TripStateMachine.Move(trip, TripStatus.Paid, null, DateTimeOffset.UtcNow));
        Assert.Equal("invalid_trip_transition", error.Code);
    }

    [Fact]
    public void Driver_response_path_cannot_skip_pickup_or_payment()
    {
        var trip = new Trip { Status = TripStatus.AwaitingDriverResponse };
        var now = DateTimeOffset.UtcNow;
        TripStateMachine.Move(trip, TripStatus.DriverAccepted, Guid.NewGuid(), now);
        Assert.Throws<TripRuleViolation>(() => TripStateMachine.Move(trip, TripStatus.PickedUp, Guid.NewGuid(), now));
        TripStateMachine.Move(trip, TripStatus.EnRoute, Guid.NewGuid(), now);
        TripStateMachine.Move(trip, TripStatus.PickedUp, Guid.NewGuid(), now);
        TripStateMachine.Move(trip, TripStatus.DroppedOff, Guid.NewGuid(), now);
        Assert.Throws<TripRuleViolation>(() => TripStateMachine.Move(trip, TripStatus.Paid, null, now));
    }
}
