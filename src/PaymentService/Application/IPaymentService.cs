using SwiftRide.Contracts.Payments;

namespace SwiftRide.Payment.Application;

public interface IPaymentService
{
    Task<PaymentDto> ProcessPaymentAsync(InitiatePaymentRequest request, CancellationToken ct = default);
    Task<PaymentDto> RefundPaymentAsync(Guid paymentId, RefundPaymentRequest request, CancellationToken ct = default);
    Task<PaymentDto> GetPaymentByTripIdAsync(Guid tripId, Guid currentUserId, IReadOnlyList<string> roles, CancellationToken ct = default);
    Task<PaymentDto> GetPaymentByIdAsync(Guid paymentId, Guid currentUserId, IReadOnlyList<string> roles, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentDto>> GetRiderHistoryAsync(Guid riderId, int page = 1, int pageSize = 20, CancellationToken ct = default);
    Task<DriverEarningsDto> GetDriverEarningsAsync(Guid driverId, CancellationToken ct = default);
    Task<WalletBalanceDto> GetRiderWalletAsync(Guid riderId, string currency = "VND", CancellationToken ct = default);
}
