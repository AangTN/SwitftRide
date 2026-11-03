using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

[ApiController, Route("internal/trips"), Authorize(Roles = "Service"), TypeFilter(typeof(TripExceptionFilter))]
public sealed class PaymentCallbacksController(TripDbContext db, ITripRealtimePublisher realtime, TimeProvider clock) : ControllerBase
{
    [HttpPost("{tripId:guid}/payment-result")]
    public async Task<IActionResult> PaymentResult(Guid tripId, PaymentResultRequest request, CancellationToken ct)
    {
        var trip = await db.Trips.Include(x => x.History).SingleOrDefaultAsync(x => x.Id == tripId, ct)
            ?? throw new SwiftRide.ServiceDefaults.ApiFailure(404, "trip_not_found", "Trip was not found.");
        if (trip.Status == TripStatus.Paid && request.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)) return Ok();
        if (trip.Status != TripStatus.PaymentPending) throw new SwiftRide.ServiceDefaults.ApiFailure(409, "payment_not_expected", "Trip is not awaiting payment.");
        if (!string.Equals(trip.PaymentId, request.PaymentId, StringComparison.Ordinal)) throw new SwiftRide.ServiceDefaults.ApiFailure(409, "payment_mismatch", "Payment does not belong to this trip.");
        trip.PaymentStatus = request.Status;
        if (request.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)) TripStateMachine.Move(trip, TripStatus.Paid, null, clock.GetUtcNow(), "payment_succeeded");
        else if (request.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) TripStateMachine.Move(trip, TripStatus.Failed, null, clock.GetUtcNow(), "payment_failed");
        else if (!request.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) && !request.Status.Equals("Processing", StringComparison.OrdinalIgnoreCase)) throw new SwiftRide.ServiceDefaults.ApiFailure(400, "invalid_payment_status", "Payment status is invalid.");
        await db.SaveChangesAsync(ct); await realtime.PublishTripChangedAsync(trip, ct); return Ok();
    }
}

public sealed record PaymentResultRequest(string PaymentId, string Status);
