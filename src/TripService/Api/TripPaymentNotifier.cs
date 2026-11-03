using MassTransit;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

public interface ITripPaymentNotifier
{
    Task RequestAsync(TripEntity trip, CancellationToken ct);
}

public sealed class TripPaymentNotifier(IPublishEndpoint bus) : ITripPaymentNotifier
{
    public Task RequestAsync(TripEntity trip, CancellationToken ct)
    {
        if (trip.PaymentId is null || trip.DriverId is null || trip.AgreedAmount is null)
            throw new InvalidOperationException("A completed trip must have payment, driver, and agreed amount snapshots.");
        return bus.Publish(new PaymentRequested(trip.PaymentId, trip.Id, trip.RiderId, trip.DriverId.Value, trip.AgreedAmount.Value, trip.Currency), ct);
    }
}

public sealed class NoopTripPaymentNotifier : ITripPaymentNotifier
{
    public Task RequestAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
}
