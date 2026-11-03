using MassTransit;
using SwiftRide.Contracts.Trips;
using SwiftRide.Matching.Infrastructure;

namespace SwiftRide.Matching.Api;

public sealed class TripMatchingRequestedConsumer(MatchingJobStore jobs) : IConsumer<TripMatchingRequested>
{
    public Task Consume(ConsumeContext<TripMatchingRequested> context) => jobs.EnqueueAsync(context.Message, context.CancellationToken);
}

public sealed class DriverReservationConfirmedConsumer(MatchingJobStore jobs) : IConsumer<DriverReservationConfirmed>
{
    public Task Consume(ConsumeContext<DriverReservationConfirmed> context)
    {
        var m = context.Message;
        return jobs.ControlAsync(m.TripId, m.ProposalId, m.DriverId, false, context.CancellationToken);
    }
}

public sealed class DriverReservationReleaseConsumer(MatchingJobStore jobs) : IConsumer<DriverReservationReleaseRequested>
{
    public Task Consume(ConsumeContext<DriverReservationReleaseRequested> context)
    {
        var m = context.Message;
        return jobs.ControlAsync(m.TripId, m.ProposalId, m.DriverId, true, context.CancellationToken);
    }
}
