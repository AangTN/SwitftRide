using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftRide.Contracts.Payments;
using SwiftRide.Contracts.Trips;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Domain;
using SwiftRide.Payment.Infrastructure;
using SwiftRide.ServiceDefaults;
using Xunit;

namespace SwiftRide.Payment.Tests;

public sealed class PaymentResultInbox
{
    public Channel<PaymentResult> Messages { get; } = Channel.CreateUnbounded<PaymentResult>();
}

public sealed class PaymentResultCollector(PaymentResultInbox inbox) : IConsumer<PaymentResult>
{
    public Task Consume(ConsumeContext<PaymentResult> context) => inbox.Messages.Writer.WriteAsync(context.Message).AsTask();
}

public sealed class PaymentReliabilityTests
{
    [Fact]
    public async Task Rider_cannot_charge_even_their_own_wallet()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        using var rider = factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.RiderId, "Rider");
        var response = await rider.PostAsJsonAsync("/api/payments", Request());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Payments.AnyAsync(x => x.TripId != PaymentDevelopmentAccounts.SeedTripId));
    }

    [Theory]
    [InlineData(false, "Wallet")]
    [InlineData(true, "Wallet")]
    [InlineData(false, "Cash")]
    public async Task Broker_request_is_idempotent_and_outbox_returns_trip_request_id(bool insufficient, string method)
    {
        await using var factory = new PaymentFactory { Messaging = true };
        await factory.InitializeAsync();
        var bus = factory.Services.GetRequiredService<IBusControl>();
        var busHandle = await bus.StartAsync();
        await busHandle.Ready;
        var observer = new CompletionObserver();
        using var handle = bus.ConnectConsumeObserver(observer);
        var request = Request() with { RiderId = insufficient ? PaymentDevelopmentAccounts.BrokeRiderId : PaymentDevelopmentAccounts.RiderId };
        var message = new PaymentRequested(request.IdempotencyKey, request.TripId, request.RiderId, request.DriverId, request.Amount, "VND", method);
        for (var i = 0; i < 2; i++)
        {
            await bus.Publish(message);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Null(await observer.Done.Reader.ReadAsync(timeout.Token));
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = await db.Payments.SingleAsync(x => x.TripId == request.TripId);
        Assert.Equal(method, payment.Method);
        Assert.Equal(insufficient ? PaymentStatus.Failed : PaymentStatus.Succeeded, payment.Status);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.Equal(insufficient ? 0 : 1, await db.LedgerTransactions.CountAsync(x => x.PaymentId == payment.Id));
        Assert.True(await scope.ServiceProvider.GetRequiredService<PaymentOutboxDispatcher>().DispatchOneAsync(default));
        using var resultTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await factory.Services.GetRequiredService<PaymentResultInbox>().Messages.Reader.ReadAsync(resultTimeout.Token);
        Assert.Equal(request.IdempotencyKey, result.PaymentId);
        Assert.Equal(request.TripId, result.TripId);
        Assert.Equal(insufficient ? "Failed" : "Succeeded", result.Status);
    }

    [Fact]
    public async Task Failure_to_queue_result_rolls_back_charge_and_ledger()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var publisher = new FailingPublisher(sp.GetRequiredService<IPaymentEventPublisher>());
        var service = new SwiftRide.Payment.Application.PaymentService(sp.GetRequiredService<IPaymentStore>(),
            sp.GetRequiredService<ILedgerService>(), sp.GetRequiredService<IPaymentGatewayFactory>(),
            publisher, NullLogger<SwiftRide.Payment.Application.PaymentService>.Instance);
        var request = Request();
        await Assert.ThrowsAsync<IOException>(() => service.ProcessPaymentAsync(request));
        var db = sp.GetRequiredService<PaymentDbContext>();
        db.ChangeTracker.Clear();
        Assert.False(await db.Payments.AnyAsync(x => x.TripId == request.TripId));
        Assert.Empty(await db.OutboxMessages.ToListAsync());
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance,
            await sp.GetRequiredService<ILedgerService>().GetUserWalletBalanceAsync(request.RiderId, "VND"));
        publisher.Fail = false;
        Assert.Equal("Succeeded", (await service.ProcessPaymentAsync(request)).Status);
        Assert.Single(await db.Payments.Where(x => x.TripId == request.TripId).ToListAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Gateway_stops_after_three_attempts_and_replay_does_not_charge_again()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var gateway = new TransientGateway();
        var service = new SwiftRide.Payment.Application.PaymentService(sp.GetRequiredService<IPaymentStore>(),
            sp.GetRequiredService<ILedgerService>(), new PaymentGatewayFactory([gateway]),
            sp.GetRequiredService<IPaymentEventPublisher>(), NullLogger<SwiftRide.Payment.Application.PaymentService>.Instance);
        var request = Request() with { PaymentMethod = "Transient" };
        await Assert.ThrowsAsync<ApiFailure>(() => service.ProcessPaymentAsync(request));
        Assert.Equal(3, gateway.Calls);
        Assert.Equal("Failed", (await service.ProcessPaymentAsync(request)).Status);
        Assert.Equal(3, gateway.Calls);
        var message = await sp.GetRequiredService<PaymentDbContext>().OutboxMessages.SingleAsync();
        Assert.Equal("Failed", JsonSerializer.Deserialize<PaymentResult>(message.Payload)!.Status);
    }

    [Fact]
    public async Task Failed_outbox_send_is_retried_after_five_seconds_without_charging_again()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var request = Request();
        await sp.GetRequiredService<IPaymentService>().ProcessPaymentAsync(request);
        var db = sp.GetRequiredService<PaymentDbContext>();
        var originalId = (await db.OutboxMessages.SingleAsync()).Id;
        var bus = DispatchProxy.Create<IPublishEndpoint, Transport>();
        var transport = (Transport)bus;
        transport.Fail = true;
        var clock = new Clock();
        var dispatcher = new PaymentOutboxDispatcher(db, bus, clock, Options.Create(new PaymentOutboxOptions()), NullLogger<PaymentOutboxDispatcher>.Instance);
        Assert.True(await dispatcher.DispatchOneAsync(default));
        Assert.False(await dispatcher.DispatchOneAsync(default));
        clock.Advance(5);
        transport.Fail = false;
        Assert.True(await dispatcher.DispatchOneAsync(default));
        Assert.Equal(originalId, (await db.OutboxMessages.SingleAsync()).Id);
        Assert.Equal(PaymentDeliveryStatus.Sent, (await db.OutboxMessages.SingleAsync()).Status);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance - request.Amount,
            await sp.GetRequiredService<ILedgerService>().GetUserWalletBalanceAsync(request.RiderId, "VND"));
    }

    private static InitiatePaymentRequest Request() => new(Guid.NewGuid(), PaymentDevelopmentAccounts.RiderId,
        PaymentDevelopmentAccounts.DriverId, 100_000m, "VND", Guid.NewGuid().ToString("N"));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    public class Transport : DispatchProxy
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls++;
            return Fail ? Task.FromException(new IOException("Unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class CompletionObserver : IConsumeObserver
    {
        public Channel<Exception?> Done { get; } = Channel.CreateUnbounded<Exception?>();
        public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
        public Task PostConsume<T>(ConsumeContext<T> context) where T : class =>
            context.Message is PaymentRequested ? Done.Writer.WriteAsync(null).AsTask() : Task.CompletedTask;
        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class =>
            Done.Writer.WriteAsync(exception).AsTask();
    }

    private sealed class TransientGateway : IPaymentGateway
    {
        public string Method => "Transient";
        public int Calls { get; private set; }
        public Task<PaymentGatewayChargeResult> ChargeAsync(PaymentChargeRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PaymentGatewayChargeResult(false, FailureCode: "unavailable", IsTransient: true));
        }
        public Task<PaymentGatewayChargeResult> RefundAsync(PaymentRefundRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FailingPublisher(IPaymentEventPublisher inner) : IPaymentEventPublisher
    {
        public bool Fail { get; set; } = true;
        public Task QueueResultAsync(Domain.Payment payment, CancellationToken ct = default) =>
            Fail ? throw new IOException("Cannot persist outbox") : inner.QueueResultAsync(payment, ct);
        public Task QueueRefundedAsync(PaymentRefundedEvent message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
