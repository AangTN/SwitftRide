using SwiftRide.Trip.Infrastructure;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

public interface ITripPaymentNotifier
{
    Task RequestAsync(TripEntity trip, CancellationToken ct);
}

public sealed class TripPaymentNotifier(TripDbContext db, TimeProvider clock) : ITripPaymentNotifier
{
    public Task RequestAsync(TripEntity trip, CancellationToken ct)
    {
        if (trip.PaymentId is null || trip.DriverId is null || trip.AgreedAmount is null)
            throw new InvalidOperationException("A completed trip must have payment, driver, and agreed amount snapshots.");
        TripOutboxMessage.Enqueue(db, TripMessageKind.RequestPayment, trip.Id, null,
            new PaymentRequested(trip.PaymentId, trip.Id, trip.RiderId, trip.DriverId.Value, trip.AgreedAmount.Value, trip.Currency), clock.GetUtcNow());
        return Task.CompletedTask;
    }
}
