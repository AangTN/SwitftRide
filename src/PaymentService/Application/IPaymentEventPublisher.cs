using SwiftRide.Contracts.Payments;

namespace SwiftRide.Payment.Application;

public interface IPaymentEventPublisher
{
    Task QueueResultAsync(Domain.Payment payment, CancellationToken ct = default);
    Task QueueRefundedAsync(PaymentRefundedEvent message, CancellationToken ct = default);
}
