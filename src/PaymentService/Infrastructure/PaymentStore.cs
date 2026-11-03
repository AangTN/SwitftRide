using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Domain;

namespace SwiftRide.Payment.Infrastructure;

public sealed class PaymentDbContextTransaction(IDbContextTransaction transaction) : IPaymentTransaction
{
    public Task CommitAsync(CancellationToken ct = default) => transaction.CommitAsync(ct);
    public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

public sealed class PaymentStore(PaymentDbContext db) : IPaymentStore
{
    public async Task<IPaymentTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new PaymentDbContextTransaction(tx);
    }

    public Task<Domain.Payment?> FindPaymentByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Payments.SingleOrDefaultAsync(p => p.Id == id, ct);

    public Task<Domain.Payment?> FindPaymentByTripIdAsync(Guid tripId, CancellationToken ct = default) =>
        db.Payments.SingleOrDefaultAsync(p => p.TripId == tripId, ct);

    public Task<Domain.Payment?> FindPaymentByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        db.Payments.SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, ct);

    public async Task AddPaymentAsync(Domain.Payment payment, CancellationToken ct = default) =>
        await db.Payments.AddAsync(payment, ct);

    public Task UpdatePaymentAsync(Domain.Payment payment, CancellationToken ct = default)
    {
        db.Payments.Update(payment);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Domain.Payment>> GetRiderPaymentsAsync(
        Guid riderId, int page, int pageSize, CancellationToken ct = default)
    {
        return await db.Payments
            .Where(p => p.RiderId == riderId)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Domain.Payment>> GetDriverSucceededPaymentsAsync(
        Guid driverId, CancellationToken ct = default)
    {
        return await db.Payments
            .Where(p => p.DriverId == driverId && p.Status == PaymentStatus.Succeeded)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<LedgerAccount?> FindAccountByCodeAndOwnerAsync(
        string code, Guid? ownerUserId, string currency, CancellationToken ct = default)
    {
        return db.LedgerAccounts.SingleOrDefaultAsync(
            a => a.Code == code && a.OwnerUserId == ownerUserId && a.Currency == currency, ct);
    }

    public async Task<LedgerAccount> GetOrCreateAccountAsync(
        string code, Guid? ownerUserId, string currency, CancellationToken ct = default)
    {
        var existing = await FindAccountByCodeAndOwnerAsync(code, ownerUserId, currency, ct);
        if (existing is not null)
            return existing;

        var account = new LedgerAccount
        {
            Id = Guid.NewGuid(),
            Code = code,
            OwnerUserId = ownerUserId,
            Currency = currency,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.LedgerAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(ct);
            return account;
        }
        catch (DbUpdateException)
        {
            // Concurrent creation handling
            db.Entry(account).State = EntityState.Detached;
            var retrieved = await FindAccountByCodeAndOwnerAsync(code, ownerUserId, currency, ct);
            if (retrieved is not null)
                return retrieved;
            throw;
        }
    }

    public async Task<decimal> GetAccountBalanceAsync(Guid accountId, CancellationToken ct = default)
    {
        var credits = await db.LedgerEntries
            .Where(e => e.AccountId == accountId && e.Side == EntrySide.Credit)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var debits = await db.LedgerEntries
            .Where(e => e.AccountId == accountId && e.Side == EntrySide.Debit)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        return credits - debits;
    }

    public Task<LedgerTransaction?> FindLedgerTransactionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        return db.LedgerTransactions
            .Include(t => t.Entries)
            .SingleOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);
    }

    public Task<LedgerTransaction?> FindLedgerTransactionByPaymentAndTypeAsync(
        Guid paymentId, string type, CancellationToken ct = default)
    {
        return db.LedgerTransactions
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.PaymentId == paymentId && t.Type == type, ct);
    }

    public async Task AddLedgerTransactionAsync(LedgerTransaction transaction, CancellationToken ct = default) =>
        await db.LedgerTransactions.AddAsync(transaction, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
