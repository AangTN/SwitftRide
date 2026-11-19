using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftRide.Contracts.Payments;
using SwiftRide.Contracts.Trips;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Domain;
using SwiftRide.Payment.Infrastructure;
using Xunit;

namespace SwiftRide.Payment.Tests;

public sealed class CashPaymentTests
{
    [Fact]
    public async Task Cash_settles_without_wallet_and_replay_does_not_duplicate_commission_or_outbox()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync(seed: false);
        using var service = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var request = Request();
        var first = await service.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var payment = (await first.Content.ReadFromJsonAsync<PaymentDto>())!;
        Assert.Equal("Cash", payment.PaymentMethod);
        Assert.Equal("Succeeded", payment.Status);
        Assert.NotNull(payment.PaidAt);
        var replay = await service.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(payment.Id, (await replay.Content.ReadFromJsonAsync<PaymentDto>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await service.PostAsJsonAsync("/api/payments", request with { PaymentMethod = "Wallet" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await service.PostAsJsonAsync("/api/payments", request with { IdempotencyKey = "other" })).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var accounts = await db.LedgerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.Code is LedgerAccountCodes.RiderWallet or LedgerAccountCodes.DriverPayable or LedgerAccountCodes.PaymentClearing);
        var receivable = Assert.Single(accounts, a => a.Code == LedgerAccountCodes.DriverCashReceivable);
        Assert.Equal(request.DriverId, receivable.OwnerUserId);
        var entries = await db.LedgerEntries.ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(15000m, entries.Sum(e => e.Side == EntrySide.Debit ? e.Amount : 0));
        Assert.Equal(15000m, entries.Sum(e => e.Side == EntrySide.Credit ? e.Amount : 0));
        Assert.Single(await db.Payments.ToListAsync());
        Assert.Single(await db.LedgerTransactions.ToListAsync());
        var result = Assert.Single(await db.OutboxMessages.ToListAsync());
        var message = JsonSerializer.Deserialize<PaymentResult>(result.Payload)!;
        Assert.Equal(request.IdempotencyKey, message.PaymentId);
        Assert.Equal("Succeeded", message.Status);
    }

    [Fact]
    public async Task Mixed_cash_and_wallet_report_net_income_without_crediting_cash_again()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync();
        using var caller = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var cash = Request() with { RiderId = PaymentDevelopmentAccounts.RiderId, DriverId = PaymentDevelopmentAccounts.DriverId };
        Assert.Equal(HttpStatusCode.OK, (await caller.PostAsJsonAsync("/api/payments", cash)).StatusCode);
        var wallet = cash with { TripId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid().ToString(), PaymentMethod = "Wallet" };
        Assert.Equal(HttpStatusCode.OK, (await caller.PostAsJsonAsync("/api/payments", wallet)).StatusCode);
        using var driver = factory.CreateAuthorizedClient(cash.DriverId, "Driver");
        var earnings = (await driver.GetFromJsonAsync<DriverEarningsDto>("/api/payments/drivers/me/earnings"))!;
        Assert.Equal(170000m, earnings.TotalEarnings);
        Assert.Equal(100000m, earnings.CashCollected);
        Assert.Equal(85000m, earnings.CashNetEarnings);
        Assert.Equal(85000m, earnings.NonCashEarnings);
        Assert.Equal(15000m, earnings.CashCommissionDue);
        Assert.Equal(2, earnings.TripCount);
        Assert.Contains(earnings.Transactions, t => t.PaymentMethod == "Cash" && t.GrossAmount == 100000m && t.PlatformFee == 15000m && t.Amount == 85000m);
        await using var scope = factory.Services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedgerService>();
        Assert.Equal(900000m, await ledger.GetUserWalletBalanceAsync(cash.RiderId, "VND"));
        Assert.Equal(85000m, await ledger.GetDriverPayableBalanceAsync(cash.DriverId, "VND"));
    }

    [Fact]
    public async Task Cash_refund_cannot_invent_wallet_credit_or_reverse_commission_without_manual_confirmation()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync(seed: false);
        using var caller = factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        var created = await caller.PostAsJsonAsync("/api/payments", Request());
        var payment = (await created.Content.ReadFromJsonAsync<PaymentDto>())!;
        var refunded = await caller.PostAsJsonAsync($"/api/payments/{payment.Id}/refund", new RefundPaymentRequest("Cash return", "refund-1"));
        Assert.Equal(HttpStatusCode.BadRequest, refunded.StatusCode);
        var body = await refunded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual_cash_refund_required", body.GetProperty("title").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        Assert.Equal(PaymentStatus.Succeeded, (await db.Payments.SingleAsync()).Status);
        Assert.Single(await db.LedgerTransactions.ToListAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.False(await db.LedgerAccounts.AnyAsync(a => a.Code == LedgerAccountCodes.RiderWallet));
    }

    [Fact]
    public async Task Cash_outbox_failure_rolls_back_payment_fee_and_new_accounts_together()
    {
        await using var factory = new PaymentFactory();
        await factory.InitializeAsync(seed: false);
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var service = new SwiftRide.Payment.Application.PaymentService(sp.GetRequiredService<IPaymentStore>(), sp.GetRequiredService<ILedgerService>(),
            sp.GetRequiredService<IPaymentGatewayFactory>(), new FailingPublisher(), NullLogger<SwiftRide.Payment.Application.PaymentService>.Instance);
        await Assert.ThrowsAsync<IOException>(() => service.ProcessPaymentAsync(Request()));
        var db = sp.GetRequiredService<PaymentDbContext>();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Payments.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        Assert.Empty(await db.LedgerTransactions.ToListAsync());
        Assert.Empty(await db.LedgerAccounts.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public void Older_payment_messages_without_method_still_mean_wallet()
    {
        var json = JsonSerializer.Serialize(new { PaymentId = "old", TripId = Guid.NewGuid(), RiderId = Guid.NewGuid(), DriverId = Guid.NewGuid(), Amount = 100m, Currency = "VND" });
        Assert.Equal("Wallet", JsonSerializer.Deserialize<PaymentRequested>(json)!.PaymentMethod);
    }

    private static InitiatePaymentRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m, "VND", Guid.NewGuid().ToString(), "Cash");
    private sealed class FailingPublisher : IPaymentEventPublisher
    {
        public Task QueueResultAsync(Domain.Payment payment, CancellationToken ct = default) => throw new IOException("Outbox unavailable");
        public Task QueueRefundedAsync(PaymentRefundedEvent message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
