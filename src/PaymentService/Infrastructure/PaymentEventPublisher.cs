using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.Payments;
using SwiftRide.Payment.Application;

namespace SwiftRide.Payment.Infrastructure;

public sealed class PaymentEventPublisher(
    IServiceProvider serviceProvider,
    ILogger<PaymentEventPublisher> logger) : IPaymentEventPublisher
{
    public async Task PublishSettledAsync(PaymentSettledEvent @event, CancellationToken ct = default)
    {
        try
        {
            var publishEndpoint = serviceProvider.GetService<IPublishEndpoint>();
            if (publishEndpoint is not null)
            {
                await publishEndpoint.Publish(@event, ct);
                logger.LogInformation("Published PaymentSettledEvent for trip {TripId}", @event.TripId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish PaymentSettledEvent to message broker.");
        }
    }

    public async Task PublishFailedAsync(PaymentFailedEvent @event, CancellationToken ct = default)
    {
        try
        {
            var publishEndpoint = serviceProvider.GetService<IPublishEndpoint>();
            if (publishEndpoint is not null)
            {
                await publishEndpoint.Publish(@event, ct);
                logger.LogInformation("Published PaymentFailedEvent for trip {TripId}", @event.TripId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish PaymentFailedEvent to message broker.");
        }
    }

    public async Task PublishRefundedAsync(PaymentRefundedEvent @event, CancellationToken ct = default)
    {
        try
        {
            var publishEndpoint = serviceProvider.GetService<IPublishEndpoint>();
            if (publishEndpoint is not null)
            {
                await publishEndpoint.Publish(@event, ct);
                logger.LogInformation("Published PaymentRefundedEvent for payment {PaymentId}", @event.PaymentId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish PaymentRefundedEvent to message broker.");
        }
    }
}
