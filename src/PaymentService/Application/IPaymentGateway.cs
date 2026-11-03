namespace SwiftRide.Payment.Application;

public record PaymentChargeRequest(
    Guid TripId,
    Guid RiderId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    string IdempotencyKey);

public record PaymentRefundRequest(
    Guid PaymentId,
    Guid TripId,
    decimal Amount,
    string Currency,
    string? ProviderReference,
    string IdempotencyKey);

public record PaymentGatewayChargeResult(
    bool IsSuccess,
    string? ProviderReference = null,
    string? FailureCode = null,
    string? FailureReason = null,
    bool IsTransient = false);

public interface IPaymentGateway
{
    string Method { get; }
    Task<PaymentGatewayChargeResult> ChargeAsync(PaymentChargeRequest request, CancellationToken ct = default);
    Task<PaymentGatewayChargeResult> RefundAsync(PaymentRefundRequest request, CancellationToken ct = default);
}

public interface IPaymentGatewayFactory
{
    IPaymentGateway GetGateway(string method);
}
