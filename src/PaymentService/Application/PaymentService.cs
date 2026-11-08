using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.Payments;
using SwiftRide.Payment.Domain;
using SwiftRide.ServiceDefaults;

namespace SwiftRide.Payment.Application;

public sealed class PaymentService(
    IPaymentStore store,
    ILedgerService ledgerService,
    IPaymentGatewayFactory gatewayFactory,
    IPaymentEventPublisher eventPublisher,
    ILogger<PaymentService> logger) : IPaymentService
{
    private const decimal PlatformCommissionRate = 0.15m;
    private const int MaxGatewayRetries = 3;

    public async Task<PaymentDto> ProcessPaymentAsync(InitiatePaymentRequest request, CancellationToken ct = default)
    {
        if (request.TripId == Guid.Empty)
            throw new ApiFailure(400, "invalid_trip_id", "TripId cannot be empty.");
        if (request.RiderId == Guid.Empty)
            throw new ApiFailure(400, "invalid_rider_id", "RiderId cannot be empty.");
        if (request.DriverId == Guid.Empty)
            throw new ApiFailure(400, "invalid_driver_id", "DriverId cannot be empty.");
        if (request.Amount <= 0)
            throw new ApiFailure(400, "invalid_amount", "Payment amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ApiFailure(400, "invalid_idempotency_key", "IdempotencyKey is required.");

        request = request with { Currency = string.IsNullOrWhiteSpace(request.Currency) ? "VND" : request.Currency.Trim().ToUpperInvariant() };
        var key = request.IdempotencyKey.Trim();
        if (key.Length > 100 || request.Currency.Length != 3) throw new ApiFailure(400, "invalid_payment_request", "Invalid key or currency.");
        var gateway = gatewayFactory.GetGateway(request.PaymentMethod);
        await using var tx = await store.BeginTransactionAsync(ct);
        await store.LockTripAsync(request.TripId, ct);

        // 1. Check existing payment by idempotency key
        var existingByKey = await store.FindPaymentByIdempotencyKeyAsync(key, ct);
        if (existingByKey is not null)
        {
            // Verify payload matches
            if (existingByKey.TripId != request.TripId ||
                existingByKey.RiderId != request.RiderId ||
                existingByKey.DriverId != request.DriverId ||
                existingByKey.Amount != request.Amount ||
                !string.Equals(existingByKey.Method, gateway.Method, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingByKey.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Idempotency conflict for key {Key}: parameter mismatch.", key);
                throw new ApiFailure(409, "idempotency_mismatch",
                    "Idempotency key was previously used with different payment details.");
            }

            logger.LogInformation("Idempotent replay for key {Key}, payment {PaymentId}", key, existingByKey.Id);
            await tx.CommitAsync(ct);
            return MapToDto(existingByKey);
        }

        // 2. A trip owns one payment; a new key cannot silently create another charge.
        var existingByTrip = await store.FindPaymentByTripIdAsync(request.TripId, ct);
        if (existingByTrip is not null)
            throw new ApiFailure(409, "trip_payment_exists", "This trip already has a payment. Reuse the original request key.");

        if (gateway.Method == "Wallet") await store.LockWalletAsync(request.RiderId, request.Currency, ct);

        var now = UtcNow();
        var payment = new Domain.Payment
        {
            Id = Guid.NewGuid(),
            Method = gateway.Method,
            TripId = request.TripId,
            RiderId = request.RiderId,
            DriverId = request.DriverId,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "VND" : request.Currency.ToUpperInvariant(),
            Status = PaymentStatus.Processing,
            IdempotencyKey = key,
            CreatedAt = now,
            UpdatedAt = now
        };

        await store.AddPaymentAsync(payment, ct);
        await store.SaveChangesAsync(ct);

        // 4. Charge with retry policy for transient gateway errors
        var chargeRequest = new PaymentChargeRequest(
            payment.TripId,
            payment.RiderId,
            payment.DriverId,
            payment.Amount,
            payment.Currency,
            payment.IdempotencyKey);

        PaymentGatewayChargeResult? chargeResult = null;
        for (var attempt = 1; attempt <= MaxGatewayRetries; attempt++)
        {
            chargeResult = await gateway.ChargeAsync(chargeRequest, ct);
            if (chargeResult.IsSuccess || !chargeResult.IsTransient)
            {
                break;
            }

            logger.LogWarning("Payment attempt {Attempt}/{Max} failed with transient error: {Reason}",
                attempt, MaxGatewayRetries, chargeResult.FailureReason);

            if (attempt < MaxGatewayRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }

        chargeResult ??= new PaymentGatewayChargeResult(false, null, "charge_timeout", "Charge timed out.", false);

        if (!chargeResult.IsSuccess)
        {
            // Payment failed - record failure state in payment table, commit so it's not retried indefinitely
            payment.Status = PaymentStatus.Failed;
            payment.FailureCode = chargeResult.FailureCode ?? "charge_failed";
            payment.UpdatedAt = UtcNow();

            await store.UpdatePaymentAsync(payment, ct);
            await eventPublisher.QueueResultAsync(payment, ct);
            await store.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            throw new ApiFailure(400, payment.FailureCode, chargeResult.FailureReason ?? "Payment processing failed.");
        }

        // 5. Post double-entry ledger transaction
        var platformFee = Math.Round(payment.Amount * PlatformCommissionRate, 2);
        var ledgerTxKey = $"tx-payment-{payment.Id:N}";

        await ledgerService.PostTripPaymentAsync(payment, platformFee, ledgerTxKey, ct);

        payment.Status = PaymentStatus.Succeeded;
        payment.ProviderReference = chargeResult.ProviderReference;
        payment.PaidAt = UtcNow();
        payment.UpdatedAt = UtcNow();

        await store.UpdatePaymentAsync(payment, ct);
        await eventPublisher.QueueResultAsync(payment, ct);
        await store.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Payment {PaymentId} succeeded for Trip {TripId}, Amount: {Amount} {Currency}",
            payment.Id, payment.TripId, payment.Amount, payment.Currency);

        return MapToDto(payment);
    }

    public async Task<PaymentDto> RefundPaymentAsync(Guid paymentId, RefundPaymentRequest request, CancellationToken ct = default)
    {
        await using var tx = await store.BeginTransactionAsync(ct);

        var payment = await store.FindPaymentByIdAsync(paymentId, ct)
            ?? throw new ApiFailure(404, "payment_not_found", $"Payment '{paymentId}' was not found.");

        await store.LockTripAsync(payment.TripId, ct);
        payment = (await store.FindPaymentByIdAsync(paymentId, ct))!;
        if (payment.Method == "Wallet") await store.LockWalletAsync(payment.RiderId, payment.Currency, ct);

        if (payment.Status == PaymentStatus.Refunded)
        {
            logger.LogInformation("Payment {PaymentId} has already been refunded.", paymentId);
            return MapToDto(payment);
        }

        if (payment.Status != PaymentStatus.Succeeded)
        {
            throw new ApiFailure(400, "invalid_payment_status",
                $"Cannot refund payment with status '{payment.Status}'. Only Succeeded payments can be refunded.");
        }

        var refundKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"refund-{payment.Id:N}"
            : request.IdempotencyKey.Trim();

        var existingRefundTx = await store.FindLedgerTransactionByIdempotencyKeyAsync(refundKey, ct);
        if (existingRefundTx is not null)
        {
            if (existingRefundTx.PaymentId != payment.Id || existingRefundTx.Type != LedgerTransactionTypes.Refund)
                throw new ApiFailure(409, "refund_key_mismatch", "Refund key belongs to a different operation.");
            logger.LogInformation("Duplicate refund attempt with key {Key} for payment {PaymentId}", refundKey, paymentId);
            return MapToDto(payment);
        }

        var gateway = gatewayFactory.GetGateway(payment.Method);
        var refundResult = await gateway.RefundAsync(new PaymentRefundRequest(
            payment.Id,
            payment.TripId,
            payment.Amount,
            payment.Currency,
            payment.ProviderReference,
            refundKey), ct);

        if (!refundResult.IsSuccess)
        {
            throw new ApiFailure(400, refundResult.FailureCode ?? "refund_failed",
                refundResult.FailureReason ?? "Gateway refund failed.");
        }

        var platformFee = Math.Round(payment.Amount * PlatformCommissionRate, 2);
        var ledgerTx = await ledgerService.PostRefundAsync(payment, platformFee, refundKey, ct);

        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedAt = UtcNow();

        await store.UpdatePaymentAsync(payment, ct);
        await eventPublisher.QueueRefundedAsync(new PaymentRefundedEvent(
            payment.Id, payment.TripId, payment.Amount, payment.Currency, ledgerTx.Id, UtcNow()), ct);
        await store.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Payment {PaymentId} refunded successfully.", paymentId);
        return MapToDto(payment);
    }

    public async Task<PaymentDto> GetPaymentByTripIdAsync(
        Guid tripId,
        Guid currentUserId,
        IReadOnlyList<string> roles,
        CancellationToken ct = default)
    {
        var payment = await store.FindPaymentByTripIdAsync(tripId, ct)
            ?? throw new ApiFailure(404, "payment_not_found", $"Payment for trip '{tripId}' was not found.");

        ValidateAccess(payment, currentUserId, roles);
        return MapToDto(payment);
    }

    public async Task<PaymentDto> GetPaymentByIdAsync(
        Guid paymentId,
        Guid currentUserId,
        IReadOnlyList<string> roles,
        CancellationToken ct = default)
    {
        var payment = await store.FindPaymentByIdAsync(paymentId, ct)
            ?? throw new ApiFailure(404, "payment_not_found", $"Payment '{paymentId}' was not found.");

        ValidateAccess(payment, currentUserId, roles);
        return MapToDto(payment);
    }

    public async Task<IReadOnlyList<PaymentDto>> GetRiderHistoryAsync(
        Guid riderId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var payments = await store.GetRiderPaymentsAsync(riderId, page, pageSize, ct);
        return payments.Select(MapToDto).ToList();
    }

    public async Task<DriverEarningsDto> GetDriverEarningsAsync(Guid driverId, CancellationToken ct = default)
    {
        var payments = await store.GetDriverSucceededPaymentsAsync(driverId, ct);
        var totalEarnings = payments.Sum(p => p.Amount - Math.Round(p.Amount * PlatformCommissionRate, 2));

        var items = payments.Select(p => new DriverEarningItemDto(
            p.Id,
            p.TripId,
            p.Amount - Math.Round(p.Amount * PlatformCommissionRate, 2),
            p.PaidAt ?? p.UpdatedAt,
            p.Method,
            p.Amount,
            Math.Round(p.Amount * PlatformCommissionRate, 2))).ToList();
        var cash = payments.Where(p => p.Method == "Cash").ToList();
        var receivable = await store.FindAccountByCodeAndOwnerAsync(LedgerAccountCodes.DriverCashReceivable, driverId, "VND", ct);
        var cashDue = receivable is null ? 0 : Math.Max(0, -await store.GetAccountBalanceAsync(receivable.Id, ct));

        return new DriverEarningsDto(
            driverId,
            totalEarnings,
            payments.Count,
            "VND",
            items,
            cash.Sum(p => p.Amount),
            cash.Sum(p => p.Amount - Math.Round(p.Amount * PlatformCommissionRate, 2)),
            payments.Where(p => p.Method != "Cash").Sum(p => p.Amount - Math.Round(p.Amount * PlatformCommissionRate, 2)),
            cashDue);
    }

    public async Task<WalletBalanceDto> GetRiderWalletAsync(Guid riderId, string currency = "VND", CancellationToken ct = default)
    {
        var balance = await ledgerService.GetUserWalletBalanceAsync(riderId, currency, ct);
        return new WalletBalanceDto(riderId, balance, currency, UtcNow());
    }

    private static DateTimeOffset UtcNow()
    {
        // PostgreSQL stores microseconds; return the same timestamp on initial response and replay.
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static void ValidateAccess(Domain.Payment payment, Guid currentUserId, IReadOnlyList<string> roles)
    {
        if (roles.Contains("Admin", StringComparer.OrdinalIgnoreCase) ||
            roles.Contains("Service", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (payment.RiderId != currentUserId && payment.DriverId != currentUserId)
        {
            throw new ApiFailure(403, "forbidden", "You are not authorized to view this payment.");
        }
    }

    private static PaymentDto MapToDto(Domain.Payment p) =>
        new(
            p.Id,
            p.TripId,
            p.RiderId,
            p.DriverId,
            p.Amount,
            p.Currency,
            p.Status.ToString(),
            p.IdempotencyKey,
            p.ProviderReference,
            p.FailureCode,
            p.PaidAt,
            p.CreatedAt,
            p.UpdatedAt,
            p.Method);
}
