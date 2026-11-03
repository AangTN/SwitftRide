using System.Threading.Channels;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using TripEntity = SwiftRide.Trip.Domain.Trip;
using Xunit;

namespace SwiftRide.Trip.Tests;

public sealed class PaymentResultConsumerTests
{
    [Fact]
    public async Task Broker_results_keep_failed_payment_pending_then_settle_and_notify_after_saving()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var notifications = new Notifications();
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<TripDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ITripRealtimePublisher>(notifications);
        services.AddScoped<TripPaymentResultHandler>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<PaymentResultConsumer>();
            x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });
        await using var provider = services.BuildServiceProvider();
        var id = Guid.NewGuid();
        const string requestId = "trip-payment-key";
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Trips.Add(new TripEntity { Id = id, Status = TripStatus.PaymentPending, PaymentId = requestId, PaymentStatus = "Pending" });
            await db.SaveChangesAsync();
        }
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            foreach (var status in new[] { "Failed", "Succeeded" })
            {
                await bus.Publish(new PaymentResult(id, requestId, status));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Assert.Equal(status, await notifications.Received.Reader.ReadAsync(timeout.Token));
                await using var scope = provider.CreateAsyncScope();
                var saved = await scope.ServiceProvider.GetRequiredService<TripDbContext>().Trips.SingleAsync();
                Assert.Equal(status, saved.PaymentStatus);
                Assert.Equal(status == "Succeeded" ? TripStatus.Paid : TripStatus.PaymentPending, saved.Status);
            }
            await using var finalScope = provider.CreateAsyncScope();
            var handler = finalScope.ServiceProvider.GetRequiredService<TripPaymentResultHandler>();
            await handler.ApplyAsync(new PaymentResult(id, requestId, "Failed"), default);
            Assert.Equal(TripStatus.Paid, (await finalScope.ServiceProvider.GetRequiredService<TripDbContext>().Trips.SingleAsync()).Status);
            Assert.Empty(await finalScope.ServiceProvider.GetRequiredService<TripDbContext>().TripStatusHistory.Where(x => x.ToStatus == TripStatus.Failed).ToListAsync());
        }
        finally { await bus.StopAsync(); }
    }

    private sealed class Notifications : ITripRealtimePublisher
    {
        public Channel<string> Received { get; } = Channel.CreateUnbounded<string>();
        public Task PublishTripChangedAsync(TripEntity trip, CancellationToken ct) => Received.Writer.WriteAsync(trip.PaymentStatus!, ct).AsTask();
        public Task PublishDriverInvitationAsync(TripEntity trip, TripProposal proposal, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRideCompletedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
    }
}
