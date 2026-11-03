using SwiftRide.Payment.Domain;

namespace SwiftRide.Payment.Application;

public interface IPaymentTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

public interface IPaymentStore
{
    Task<IPaymentTransaction> BeginTransactionAsync(CancellationToken ct = default);

    Task<Domain.Payment?> FindPaymentByIdAsync(Guid id, CancellationToken ct = default);
    Task<Domain.Payment?> FindPaymentByTripIdAsync(Guid tripId, CancellationToken ct = default);
    Task<Domain.Payment?> FindPaymentByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task AddPaymentAsync(Domain.Payment payment, CancellationToken ct = default);
    Task UpdatePaymentAsync(Domain.Payment payment, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Payment>> GetRiderPaymentsAsync(Guid riderId, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Payment>> GetDriverSucceededPaymentsAsync(Guid driverId, CancellationToken ct = default);

    Task<LedgerAccount?> FindAccountByCodeAndOwnerAsync(string code, Guid? ownerUserId, string currency, CancellationToken ct = default);
    Task<LedgerAccount> GetOrCreateAccountAsync(string code, Guid? ownerUserId, string currency, CancellationToken ct = default);
    Task<decimal> GetAccountBalanceAsync(Guid accountId, CancellationToken ct = default);

    Task<LedgerTransaction?> FindLedgerTransactionByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<LedgerTransaction?> FindLedgerTransactionByPaymentAndTypeAsync(Guid paymentId, string type, CancellationToken ct = default);
    Task AddLedgerTransactionAsync(LedgerTransaction transaction, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
