using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

[ApiController, Route("internal/trips"), Authorize(Roles = "Service"), TypeFilter(typeof(TripExceptionFilter))]
public sealed class PaymentCallbacksController(TripPaymentResultHandler handler) : ControllerBase
{
    [HttpPost("{tripId:guid}/payment-result")]
    public async Task<IActionResult> PaymentResult(Guid tripId, PaymentResultRequest request, CancellationToken ct)
    {
        await handler.ApplyAsync(new SwiftRide.Contracts.Trips.PaymentResult(tripId, request.PaymentId, request.Status), ct);
        return Ok();
    }
}

public sealed record PaymentResultRequest(string PaymentId, string Status);
