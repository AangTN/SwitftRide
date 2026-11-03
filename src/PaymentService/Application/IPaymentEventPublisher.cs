using SwiftRide.Contracts.Payments;

namespace SwiftRide.Payment.Application;

public interface IPaymentEventPublisher
{
    Task PublishSettledAsync(PaymentSettledEvent @event, CancellationToken ct = default);
    Task PublishFailedAsync(PaymentFailedEvent @event, CancellationToken ct = default);
    Task PublishRefundedAsync(PaymentRefundedEvent @event, CancellationToken ct = default);
}
