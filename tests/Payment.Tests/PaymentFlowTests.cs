using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.Payments;
using SwiftRide.Payment.Domain;
using SwiftRide.Payment.Infrastructure;
using Xunit;

namespace SwiftRide.Payment.Tests;

public sealed class PaymentFlowTests : IAsyncLifetime
{
    private readonly PaymentFactory _factory = new();
    private HttpClient _serviceClient = null!;
    private HttpClient _adminClient = null!;
    private HttpClient _riderClient = null!;
    private HttpClient _driverClient = null!;
    private HttpClient _brokeRiderClient = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeAsync(seed: true);
        _serviceClient = _factory.CreateAuthorizedClient(Guid.NewGuid(), "Service");
        _adminClient = _factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.AdminId, "Admin");
        _riderClient = _factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.RiderId, "Rider");
        _driverClient = _factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.DriverId, "Driver");
        _brokeRiderClient = _factory.CreateAuthorizedClient(PaymentDevelopmentAccounts.BrokeRiderId, "Rider");
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Successful_wallet_payment_deducts_rider_funds_driver_and_settles()
    {
        var tripId = Guid.NewGuid();
        var key = $"idemp-{Guid.NewGuid():N}";
        var amount = 100_000m;

        var request = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: amount,
            Currency: "VND",
            IdempotencyKey: key,
            PaymentMethod: "Wallet");

        // Trusted service initiates payment
        var response = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payment = await response.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment);
        Assert.Equal(tripId, payment.TripId);
        Assert.Equal(amount, payment.Amount);
        Assert.Equal("Succeeded", payment.Status);
        Assert.NotNull(payment.PaidAt);

        // Verify rider wallet was deducted
        var walletResponse = await _riderClient.GetAsync("/api/payments/me/wallet");
        Assert.Equal(HttpStatusCode.OK, walletResponse.StatusCode);
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(wallet);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance - amount, wallet.AvailableBalance);

        // Verify driver earnings reflect payout (85% of 100k = 85k)
        var earningsResponse = await _driverClient.GetAsync("/api/payments/drivers/me/earnings");
        Assert.Equal(HttpStatusCode.OK, earningsResponse.StatusCode);
        var earnings = await earningsResponse.Content.ReadFromJsonAsync<DriverEarningsDto>();
        Assert.NotNull(earnings);
        Assert.True(earnings.TotalEarnings >= 85_000m);
        Assert.Contains(earnings.Transactions, t => t.TripId == tripId && t.Amount == 85_000m);

        // Verify lookup by tripId
        var tripPayResponse = await _riderClient.GetAsync($"/api/payments/trips/{tripId}");
        Assert.Equal(HttpStatusCode.OK, tripPayResponse.StatusCode);
        var tripPayment = await tripPayResponse.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(tripPayment);
        Assert.Equal(payment.Id, tripPayment.Id);
    }

    [Fact]
    public async Task Insufficient_funds_fails_payment_and_keeps_balance_unchanged()
    {
        var tripId = Guid.NewGuid();
        var key = $"idemp-broke-{Guid.NewGuid():N}";
        var amount = 50_000m;

        var request = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.BrokeRiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: amount,
            Currency: "VND",
            IdempotencyKey: key,
            PaymentMethod: "Wallet");

        // Trusted service charges the broke rider
        var response = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Check wallet balance is still 0
        var walletResponse = await _brokeRiderClient.GetAsync("/api/payments/me/wallet");
        Assert.Equal(HttpStatusCode.OK, walletResponse.StatusCode);
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(wallet);
        Assert.Equal(0m, wallet.AvailableBalance);

        // Check payment record is recorded as Failed
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var recordedPayment = await db.Payments.SingleOrDefaultAsync(p => p.TripId == tripId);
        Assert.NotNull(recordedPayment);
        Assert.Equal(PaymentStatus.Failed, recordedPayment.Status);
        Assert.Equal("insufficient_funds", recordedPayment.FailureCode);
    }

    [Fact]
    public async Task Idempotent_payment_requests_return_identical_result_without_double_charge()
    {
        var tripId = Guid.NewGuid();
        var key = $"idemp-dup-{Guid.NewGuid():N}";
        var amount = 80_000m;

        var request = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: amount,
            Currency: "VND",
            IdempotencyKey: key,
            PaymentMethod: "Wallet");

        // Send 1
        var response1 = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var payment1 = await response1.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment1);

        // Send 2 (Identical replay)
        var response2 = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var payment2 = await response2.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment2);

        // Results match
        Assert.Equal(payment1.Id, payment2.Id);
        Assert.Equal(payment1.PaidAt, payment2.PaidAt);

        // Wallet was only charged 80,000 once
        var walletResponse = await _riderClient.GetAsync("/api/payments/me/wallet");
        var wallet = await walletResponse.Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(wallet);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance - amount, wallet.AvailableBalance);
    }

    [Fact]
    public async Task Idempotency_key_reuse_with_different_parameters_is_rejected_with_conflict()
    {
        var tripId = Guid.NewGuid();
        var key = $"idemp-conflict-{Guid.NewGuid():N}";

        var request1 = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 50_000m,
            Currency: "VND",
            IdempotencyKey: key,
            PaymentMethod: "Wallet");

        var response1 = await _serviceClient.PostAsJsonAsync("/api/payments", request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Reusing same key with different amount
        var request2 = request1 with { Amount = 120_000m };
        var response2 = await _serviceClient.PostAsJsonAsync("/api/payments", request2);
        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
    }

    [Fact]
    public async Task Refund_succeeded_payment_reverses_ledger_entries_and_credits_rider()
    {
        var tripId = Guid.NewGuid();
        var key = $"idemp-refund-{Guid.NewGuid():N}";
        var amount = 150_000m;

        var request = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: amount,
            Currency: "VND",
            IdempotencyKey: key,
            PaymentMethod: "Wallet");

        // 1. Pay
        var payResponse = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, payResponse.StatusCode);
        var payment = await payResponse.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment);

        // Wallet balance after payment
        var walletAfterPay = await (await _riderClient.GetAsync("/api/payments/me/wallet")).Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(walletAfterPay);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance - amount, walletAfterPay.AvailableBalance);

        // 2. Admin refunds payment
        var refundResponse = await _adminClient.PostAsJsonAsync($"/api/payments/{payment.Id}/refund", new RefundPaymentRequest("Customer cancellation"));
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);
        var refunded = await refundResponse.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(refunded);
        Assert.Equal("Refunded", refunded.Status);

        // 3. Wallet balance restored
        var walletAfterRefund = await (await _riderClient.GetAsync("/api/payments/me/wallet")).Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(walletAfterRefund);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance, walletAfterRefund.AvailableBalance);

        // 4. Duplicate refund returns existing refunded state without extra credit
        var dupRefundResponse = await _adminClient.PostAsJsonAsync($"/api/payments/{payment.Id}/refund", new RefundPaymentRequest());
        Assert.Equal(HttpStatusCode.OK, dupRefundResponse.StatusCode);

        var walletAfterDupRefund = await (await _riderClient.GetAsync("/api/payments/me/wallet")).Content.ReadFromJsonAsync<WalletBalanceDto>();
        Assert.NotNull(walletAfterDupRefund);
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance, walletAfterDupRefund.AvailableBalance);
    }

    [Fact]
    public async Task Ledger_transactions_maintain_strict_debit_credit_balance()
    {
        // Execute a payment
        var request = new InitiatePaymentRequest(
            TripId: Guid.NewGuid(),
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 75_000m,
            Currency: "VND",
            IdempotencyKey: $"ledger-balance-{Guid.NewGuid():N}",
            PaymentMethod: "Wallet");

        var response = await _serviceClient.PostAsJsonAsync("/api/payments", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Query database directly and verify every single ledger transaction
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var transactions = await db.LedgerTransactions.Include(t => t.Entries).ToListAsync();

        Assert.NotEmpty(transactions);
        foreach (var tx in transactions)
        {
            var debits = tx.Entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount);
            var credits = tx.Entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount);
            Assert.Equal(debits, credits);
            Assert.All(tx.Entries, e => Assert.True(e.Amount > 0, "All entry amounts must be > 0"));
        }
    }

    [Fact]
    public async Task Security_checks_prevent_unauthorized_payment_tampering()
    {
        var anonymousClient = _factory.CreateClient();

        // 1. Unauthenticated requests are rejected
        var unauth1 = await anonymousClient.GetAsync("/api/payments/me");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth1.StatusCode);

        var unauth2 = await anonymousClient.GetAsync("/api/payments/drivers/me/earnings");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth2.StatusCode);

        // 2. Rider cannot access driver earnings endpoint
        var forbiddenEarnings = await _riderClient.GetAsync("/api/payments/drivers/me/earnings");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenEarnings.StatusCode);

        // 3. User cannot initiate payment with someone else's RiderId unless Admin
        var maliciousRequest = new InitiatePaymentRequest(
            TripId: Guid.NewGuid(),
            RiderId: PaymentDevelopmentAccounts.AdminId, // Different from rider client's ID
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 50_000m,
            Currency: "VND",
            IdempotencyKey: $"malicious-{Guid.NewGuid():N}");

        var forbiddenPay = await _riderClient.PostAsJsonAsync("/api/payments", maliciousRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPay.StatusCode);

        // 4. Regular rider cannot refund payments
        var refundAttempt = await _riderClient.PostAsJsonAsync($"/api/payments/{Guid.NewGuid()}/refund", new RefundPaymentRequest());
        Assert.Equal(HttpStatusCode.Forbidden, refundAttempt.StatusCode);
    }

    [Fact]
    public async Task Mock_card_payment_succeeds_and_declined_card_is_handled()
    {
        // 1. Successful card payment
        var tripId = Guid.NewGuid();
        var cardRequest = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 60_000m,
            Currency: "VND",
            IdempotencyKey: $"mockcard-{Guid.NewGuid():N}",
            PaymentMethod: "MockCard");

        var response = await _serviceClient.PostAsJsonAsync("/api/payments", cardRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payment = await response.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment);
        Assert.Equal("Succeeded", payment.Status);
        Assert.StartsWith("CARD-", payment.ProviderReference);
        var wallet = await _riderClient.GetFromJsonAsync<WalletBalanceDto>("/api/payments/me/wallet");
        Assert.Equal(PaymentDevelopmentAccounts.RiderDemoInitialBalance, wallet!.AvailableBalance);

        // 2. Card declined test (hook amount 888,888)
        var declinedRequest = cardRequest with
        {
            TripId = Guid.NewGuid(),
            Amount = 888_888m,
            IdempotencyKey = $"card-declined-{Guid.NewGuid():N}"
        };

        var declinedResponse = await _serviceClient.PostAsJsonAsync("/api/payments", declinedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, declinedResponse.StatusCode);
    }

    [Fact]
    public async Task Get_my_payments_returns_paginated_history()
    {
        // Make 2 payments
        for (var i = 1; i <= 2; i++)
        {
            var req = new InitiatePaymentRequest(
                TripId: Guid.NewGuid(),
                RiderId: PaymentDevelopmentAccounts.RiderId,
                DriverId: PaymentDevelopmentAccounts.DriverId,
                Amount: 20_000m * i,
                Currency: "VND",
                IdempotencyKey: $"history-{i}-{Guid.NewGuid():N}",
                PaymentMethod: "Wallet");

            var resp = await _serviceClient.PostAsJsonAsync("/api/payments", req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        var historyResponse = await _riderClient.GetAsync("/api/payments/me?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var items = await historyResponse.Content.ReadFromJsonAsync<List<PaymentDto>>();
        Assert.NotNull(items);
        Assert.True(items.Count >= 2);
    }

    [Fact]
    public async Task Refund_non_existent_payment_returns_404()
    {
        var notFoundResponse = await _adminClient.PostAsJsonAsync(
            $"/api/payments/{Guid.NewGuid()}/refund", new RefundPaymentRequest());
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
    }

    [Fact]
    public async Task Transient_gateway_error_retries_and_records_failure_state()
    {
        var tripId = Guid.NewGuid();
        var timeoutRequest = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 999_999m, // Hook for gateway_timeout
            Currency: "VND",
            IdempotencyKey: $"mockcard-timeout-{Guid.NewGuid():N}",
            PaymentMethod: "MockCard");

        var response = await _serviceClient.PostAsJsonAsync("/api/payments", timeoutRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify status in DB is recorded as Failed so timeout is distinguishable from pending/processing
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var recordedPayment = await db.Payments.SingleOrDefaultAsync(p => p.TripId == tripId);
        Assert.NotNull(recordedPayment);
        Assert.Equal(PaymentStatus.Failed, recordedPayment.Status);
        Assert.Equal("gateway_timeout", recordedPayment.FailureCode);
    }

    [Fact]
    public async Task Unauthorized_user_cannot_view_another_riders_payment()
    {
        var tripId = Guid.NewGuid();
        var payReq = new InitiatePaymentRequest(
            TripId: tripId,
            RiderId: PaymentDevelopmentAccounts.RiderId,
            DriverId: PaymentDevelopmentAccounts.DriverId,
            Amount: 50_000m,
            Currency: "VND",
            IdempotencyKey: $"auth-view-{Guid.NewGuid():N}",
            PaymentMethod: "Wallet");

        var payResp = await _serviceClient.PostAsJsonAsync("/api/payments", payReq);
        Assert.Equal(HttpStatusCode.OK, payResp.StatusCode);
        var payment = await payResp.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment);

        // Broke rider tries to view Rider 1's payment by tripId -> 403 Forbidden
        var forbiddenTripResp = await _brokeRiderClient.GetAsync($"/api/payments/trips/{tripId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenTripResp.StatusCode);

        // Broke rider tries to view Rider 1's payment by paymentId -> 403 Forbidden
        var forbiddenIdResp = await _brokeRiderClient.GetAsync($"/api/payments/{payment.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenIdResp.StatusCode);

        // Admin can view Rider 1's payment
        var adminViewTripResp = await _adminClient.GetAsync($"/api/payments/trips/{tripId}");
        Assert.Equal(HttpStatusCode.OK, adminViewTripResp.StatusCode);

        // Driver of the trip can view Rider 1's payment
        var driverViewTripResp = await _driverClient.GetAsync($"/api/payments/trips/{tripId}");
        Assert.Equal(HttpStatusCode.OK, driverViewTripResp.StatusCode);
    }
}
