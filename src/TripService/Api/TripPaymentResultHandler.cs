using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Trips;
using SwiftRide.ServiceDefaults;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

public sealed class TripPaymentResultHandler(TripDbContext db, ITripRealtimePublisher realtime, TimeProvider clock)
{
    public async Task ApplyAsync(PaymentResult result, CancellationToken ct)
    {
        var trip = await db.Trips.Include(x => x.History).SingleOrDefaultAsync(x => x.Id == result.TripId, ct)
            ?? throw new ApiFailure(404, "trip_not_found", "Trip was not found.");
        if (trip.PaymentId != result.PaymentId) throw new ApiFailure(409, "payment_mismatch", "Payment does not belong to this trip.");
        var status = result.Status?.ToLowerInvariant() switch
        {
            "succeeded" => "Succeeded", "failed" => "Failed", "pending" => "Pending", "processing" => "Processing",
            _ => throw new ApiFailure(400, "invalid_payment_status", "Payment status is invalid.")
        };
        if (trip.Status == TripStatus.Paid) return; // Late messages cannot undo settlement.
        if (trip.Status != TripStatus.PaymentPending) throw new ApiFailure(409, "payment_not_expected", "Trip is not awaiting payment.");
        if (trip.PaymentStatus == status || (trip.PaymentStatus == "Failed" && status is "Pending" or "Processing")) return;
        trip.PaymentStatus = status;
        if (status == "Succeeded") TripStateMachine.Move(trip, TripStatus.Paid, null, clock.GetUtcNow(), "payment_succeeded");
        else { trip.StateVersion++; trip.UpdatedAt = clock.GetUtcNow(); }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw;
        }
        await realtime.PublishTripChangedAsync(trip, ct);
    }
}

public sealed class PaymentResultConsumer(TripPaymentResultHandler handler) : IConsumer<PaymentResult>
{
    public Task Consume(ConsumeContext<PaymentResult> context) => handler.ApplyAsync(context.Message, context.CancellationToken);
}
