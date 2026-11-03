using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.Payments;
using SwiftRide.Payment.Domain;
using SwiftRide.Payment.Infrastructure;
using Xunit;

namespace SwiftRide.Payment.Tests;

public sealed class PaymentPostgresFactAttribute : FactAttribute
{
    public PaymentPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTRIDE_TEST_POSTGRES")))
            Skip = "Requires SWIFTRIDE_TEST_POSTGRES.";
    }
}

public sealed class PaymentConcurrencyTests
{
    [PaymentPostgresFact]
    public async Task Two_trips_cannot_spend_the_same_wallet_balance()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var first = Request();
        var second = Request();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/payments", first), client.PostAsJsonAsync("/api/payments", second));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
        using var rider = factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.RiderId, "Rider");
        var wallet = await rider.GetFromJsonAsync<WalletBalanceDto>("/api/payments/me/wallet");
        Assert.Equal(300_000m, wallet!.AvailableBalance);
    }

    [PaymentPostgresFact]
    public async Task Duplicate_concurrent_requests_create_one_payment_and_one_result()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var request = Request();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/payments", request), client.PostAsJsonAsync("/api/payments", request));
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var first = await responses[0].Content.ReadFromJsonAsync<PaymentDto>();
        var second = await responses[1].Content.ReadFromJsonAsync<PaymentDto>();
        Assert.Equal(first!.Id, second!.Id);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        Assert.Single(await db.Payments.Where(x => x.TripId == request.TripId).ToListAsync());
        Assert.Single(await db.LedgerTransactions.Where(x => x.PaymentId == first.Id).ToListAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [PaymentPostgresFact]
    public async Task Concurrent_refunds_reverse_the_ledger_only_once()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var payment = await (await client.PostAsJsonAsync("/api/payments", Request())).Content.ReadFromJsonAsync<PaymentDto>();
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/payments/{payment!.Id}/refund", new RefundPaymentRequest()),
            client.PostAsJsonAsync($"/api/payments/{payment.Id}/refund", new RefundPaymentRequest()));
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        Assert.Single(await db.LedgerTransactions.Where(x => x.PaymentId == payment.Id && x.Type == LedgerTransactionTypes.Refund).ToListAsync());
    }

    private static InitiatePaymentRequest Request() => new(Guid.NewGuid(), PaymentDevelopmentAccounts.RiderId,
        PaymentDevelopmentAccounts.DriverId, 700_000m, "VND", Guid.NewGuid().ToString("N"));
}
