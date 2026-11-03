using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftRide.Contracts.Payments;
using SwiftRide.Contracts.Trips;

namespace SwiftRide.Payment.Infrastructure;

public sealed class PaymentOutboxOptions
{
    public int RetryCount { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 5;
    public int PublishTimeoutSeconds { get; init; } = 5;
}

public sealed class PaymentOutboxDispatcher(PaymentDbContext db, IPublishEndpoint bus, TimeProvider clock,
    IOptions<PaymentOutboxOptions> options, ILogger<PaymentOutboxDispatcher> logger)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var message = db.Database.IsNpgsql()
            ? (await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE status = 'Pending' AND next_attempt_at <= {now} ORDER BY next_attempt_at, created_at LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(ct)).SingleOrDefault()
            : await db.OutboxMessages.Where(x => x.Status == PaymentDeliveryStatus.Pending && x.NextAttemptAt <= now)
                .OrderBy(x => x.NextAttemptAt).FirstOrDefaultAsync(ct);
        if (message is null) return false;
        message.Attempts++;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
        try
        {
            if (message.Kind == "Result") await PublishAsync<PaymentResult>(message, timeout.Token);
            else if (message.Kind == "Refunded") await PublishAsync<PaymentRefundedEvent>(message, timeout.Token);
            else throw new InvalidOperationException("Unknown payment outbox kind.");
            message.Status = PaymentDeliveryStatus.Sent;
            message.SentAt = clock.GetUtcNow().UtcDateTime;
            message.LastError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            message.LastError = ex.GetType().Name;
            message.NextAttemptAt = clock.GetUtcNow().UtcDateTime.AddSeconds(options.Value.RetryDelaySeconds);
            if (message.Attempts >= 1 + options.Value.RetryCount) message.Status = PaymentDeliveryStatus.Failed;
            logger.LogWarning("Payment outbox {MessageId}: attempt {Attempt}, status {Status}, error {ErrorType}",
                message.Id, message.Attempts, message.Status, message.LastError);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private Task PublishAsync<T>(PaymentOutboxMessage message, CancellationToken ct) where T : class =>
        bus.Publish(JsonSerializer.Deserialize<T>(message.Payload) ?? throw new InvalidOperationException("Empty outbox payload."),
            context => context.MessageId = message.Id, ct);
}

public sealed class PaymentOutboxWorker(IServiceScopeFactory scopes, ILogger<PaymentOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<PaymentOutboxDispatcher>().DispatchOneAsync(stoppingToken)) continue;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Payment dispatcher error {ErrorType}", ex.GetType().Name);
            }
            await Task.Delay(250, stoppingToken);
        }
    }
}
