using System.Text.Json;
using SwiftRide.Contracts.Payments;
using SwiftRide.Contracts.Trips;
using SwiftRide.Payment.Application;

namespace SwiftRide.Payment.Infrastructure;

// Queue only; the caller commits business data and messages together.
public sealed class PaymentEventPublisher(PaymentDbContext db, TimeProvider clock) : IPaymentEventPublisher
{
    public Task QueueResultAsync(Domain.Payment payment, CancellationToken ct = default) =>
        QueueAsync("Result", new PaymentResult(payment.TripId, payment.IdempotencyKey, payment.Status.ToString()));
    public Task QueueRefundedAsync(PaymentRefundedEvent message, CancellationToken ct = default) => QueueAsync("Refunded", message);
    private Task QueueAsync<T>(string kind, T message)
    {
        db.OutboxMessages.Add(new PaymentOutboxMessage
        {
            Kind = kind, Payload = JsonSerializer.Serialize(message),
            CreatedAt = clock.GetUtcNow().UtcDateTime, NextAttemptAt = clock.GetUtcNow().UtcDateTime
        });
        return Task.CompletedTask;
    }
}
