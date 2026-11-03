using MassTransit;
using SwiftRide.Contracts.Payments;
using SwiftRide.Contracts.Trips;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Domain;
using SwiftRide.ServiceDefaults;

namespace SwiftRide.Payment.Api;

public sealed class PaymentRequestedConsumer(IPaymentService payments, IPaymentStore store) : IConsumer<PaymentRequested>
{
    public async Task Consume(ConsumeContext<PaymentRequested> context)
    {
        var message = context.Message;
        try
        {
            await payments.ProcessPaymentAsync(new InitiatePaymentRequest(message.TripId, message.RiderId,
                message.DriverId, message.Amount, message.Currency, message.PaymentId), context.CancellationToken);
        }
        catch (ApiFailure failure) when (failure.Status == 400)
        {
            // Persisted business failures have queued their result; do not restart gateway attempts.
            var payment = await store.FindPaymentByIdempotencyKeyAsync(message.PaymentId, context.CancellationToken);
            if (payment is null || payment.TripId != message.TripId || payment.Status != PaymentStatus.Failed) throw;
        }
    }
}
