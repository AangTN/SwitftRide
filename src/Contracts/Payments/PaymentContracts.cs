namespace SwiftRide.Contracts.Payments;

public record InitiatePaymentRequest(
    Guid TripId,
    Guid RiderId,
    Guid DriverId,
    decimal Amount,
    string Currency = "VND",
    string IdempotencyKey = "",
    string PaymentMethod = "Wallet");

public record RefundPaymentRequest(
    string? Reason = null,
    string? IdempotencyKey = null);

public record PaymentDto(
    Guid Id,
    Guid TripId,
    Guid RiderId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    string Status,
    string IdempotencyKey,
    string? ProviderReference,
    string? FailureCode,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PaymentMethod = "Wallet");

public record WalletBalanceDto(
    Guid UserId,
    decimal AvailableBalance,
    string Currency,
    DateTimeOffset UpdatedAt);

public record DriverEarningItemDto(
    Guid PaymentId,
    Guid TripId,
    decimal Amount,
    DateTimeOffset PaidAt,
    string PaymentMethod = "Wallet",
    decimal GrossAmount = 0,
    decimal PlatformFee = 0);

public record DriverEarningsDto(
    Guid DriverId,
    decimal TotalEarnings,
    int TripCount,
    string Currency,
    IReadOnlyList<DriverEarningItemDto> Transactions,
    decimal CashCollected = 0,
    decimal CashNetEarnings = 0,
    decimal NonCashEarnings = 0,
    decimal CashCommissionDue = 0);

public record PaymentSettledEvent(
    Guid PaymentId,
    Guid TripId,
    Guid RiderId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    DateTimeOffset PaidAt,
    string? ProviderReference);

public record PaymentFailedEvent(
    Guid TripId,
    Guid RiderId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    string FailureCode,
    string Reason);

public record PaymentRefundedEvent(
    Guid PaymentId,
    Guid TripId,
    decimal Amount,
    string Currency,
    Guid RefundTransactionId,
    DateTimeOffset RefundedAt);
