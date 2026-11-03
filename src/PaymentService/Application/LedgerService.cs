using SwiftRide.Payment.Domain;
using SwiftRide.ServiceDefaults;

namespace SwiftRide.Payment.Application;

public interface ILedgerService
{
    Task<LedgerAccount> EnsureUserWalletAccountAsync(Guid userId, string currency, CancellationToken ct = default);
    Task<LedgerAccount> EnsureDriverPayableAccountAsync(Guid driverId, string currency, CancellationToken ct = default);
    Task<LedgerAccount> EnsurePlatformRevenueAccountAsync(string currency, CancellationToken ct = default);
    Task<LedgerAccount> EnsureClearingAccountAsync(string code, string currency, CancellationToken ct = default);

    Task<decimal> GetUserWalletBalanceAsync(Guid userId, string currency, CancellationToken ct = default);
    Task<decimal> GetDriverPayableBalanceAsync(Guid driverId, string currency, CancellationToken ct = default);

    Task<LedgerTransaction> PostTripPaymentAsync(
        Domain.Payment payment,
        decimal platformFeeAmount,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<LedgerTransaction> PostRefundAsync(
        Domain.Payment payment,
        decimal platformFeeAmount,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<LedgerTransaction> PostDepositAsync(
        Guid userId,
        decimal amount,
        string currency,
        string idempotencyKey,
        CancellationToken ct = default);
}

public sealed class LedgerService(IPaymentStore store) : ILedgerService
{
    public Task<LedgerAccount> EnsureUserWalletAccountAsync(Guid userId, string currency, CancellationToken ct = default) =>
        store.GetOrCreateAccountAsync(LedgerAccountCodes.RiderWallet, userId, currency, ct);

    public Task<LedgerAccount> EnsureDriverPayableAccountAsync(Guid driverId, string currency, CancellationToken ct = default) =>
        store.GetOrCreateAccountAsync(LedgerAccountCodes.DriverPayable, driverId, currency, ct);

    public Task<LedgerAccount> EnsurePlatformRevenueAccountAsync(string currency, CancellationToken ct = default) =>
        store.GetOrCreateAccountAsync(LedgerAccountCodes.PlatformRevenue, null, currency, ct);

    public Task<LedgerAccount> EnsureClearingAccountAsync(string code, string currency, CancellationToken ct = default) =>
        store.GetOrCreateAccountAsync(code, null, currency, ct);

    public async Task<decimal> GetUserWalletBalanceAsync(Guid userId, string currency, CancellationToken ct = default)
    {
        var account = await store.FindAccountByCodeAndOwnerAsync(
            LedgerAccountCodes.RiderWallet, userId, currency, ct);
        return account is null ? 0m : await store.GetAccountBalanceAsync(account.Id, ct);
    }

    public async Task<decimal> GetDriverPayableBalanceAsync(Guid driverId, string currency, CancellationToken ct = default)
    {
        var account = await store.FindAccountByCodeAndOwnerAsync(
            LedgerAccountCodes.DriverPayable, driverId, currency, ct);
        return account is null ? 0m : await store.GetAccountBalanceAsync(account.Id, ct);
    }

    public async Task<LedgerTransaction> PostTripPaymentAsync(
        Domain.Payment payment,
        decimal platformFeeAmount,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (payment.Amount <= 0)
            throw new ApiFailure(400, "invalid_amount", "Payment amount must be greater than zero.");

        if (platformFeeAmount < 0 || platformFeeAmount > payment.Amount)
            throw new ApiFailure(400, "invalid_platform_fee", "Platform fee cannot be negative or exceed total amount.");

        var riderAccount = payment.Method == "Wallet"
            ? await EnsureUserWalletAccountAsync(payment.RiderId, payment.Currency, ct)
            : await EnsureClearingAccountAsync(LedgerAccountCodes.PaymentClearing, payment.Currency, ct);
        var driverAccount = await EnsureDriverPayableAccountAsync(payment.DriverId, payment.Currency, ct);
        var driverAmount = payment.Amount - platformFeeAmount;

        var tx = new LedgerTransaction
        {
            PaymentId = payment.Id,
            Type = LedgerTransactionTypes.PaymentSettled,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Entries = []
        };

        // Debit Rider (balance decreases)
        tx.Entries.Add(new LedgerEntry
        {
            TransactionId = tx.Id,
            AccountId = riderAccount.Id,
            Side = EntrySide.Debit,
            Amount = payment.Amount
        });

        // Credit Driver
        if (driverAmount > 0)
        {
            tx.Entries.Add(new LedgerEntry
            {
                TransactionId = tx.Id,
                AccountId = driverAccount.Id,
                Side = EntrySide.Credit,
                Amount = driverAmount
            });
        }

        // Credit Platform Revenue
        if (platformFeeAmount > 0)
        {
            var platformAccount = await EnsurePlatformRevenueAccountAsync(payment.Currency, ct);
            tx.Entries.Add(new LedgerEntry
            {
                TransactionId = tx.Id,
                AccountId = platformAccount.Id,
                Side = EntrySide.Credit,
                Amount = platformFeeAmount
            });
        }

        ValidateBalance(tx);
        await store.AddLedgerTransactionAsync(tx, ct);
        return tx;
    }

    public async Task<LedgerTransaction> PostRefundAsync(
        Domain.Payment payment,
        decimal platformFeeAmount,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (payment.Amount <= 0)
            throw new ApiFailure(400, "invalid_amount", "Payment amount must be greater than zero.");

        var riderAccount = payment.Method == "Wallet"
            ? await EnsureUserWalletAccountAsync(payment.RiderId, payment.Currency, ct)
            : await EnsureClearingAccountAsync(LedgerAccountCodes.PaymentClearing, payment.Currency, ct);
        var driverAccount = await EnsureDriverPayableAccountAsync(payment.DriverId, payment.Currency, ct);
        var driverAmount = payment.Amount - platformFeeAmount;

        var tx = new LedgerTransaction
        {
            PaymentId = payment.Id,
            Type = LedgerTransactionTypes.Refund,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Entries = []
        };

        // Credit Rider (reversal - balance increases)
        tx.Entries.Add(new LedgerEntry
        {
            TransactionId = tx.Id,
            AccountId = riderAccount.Id,
            Side = EntrySide.Credit,
            Amount = payment.Amount
        });

        // Debit Driver
        if (driverAmount > 0)
        {
            tx.Entries.Add(new LedgerEntry
            {
                TransactionId = tx.Id,
                AccountId = driverAccount.Id,
                Side = EntrySide.Debit,
                Amount = driverAmount
            });
        }

        // Debit Platform Revenue
        if (platformFeeAmount > 0)
        {
            var platformAccount = await EnsurePlatformRevenueAccountAsync(payment.Currency, ct);
            tx.Entries.Add(new LedgerEntry
            {
                TransactionId = tx.Id,
                AccountId = platformAccount.Id,
                Side = EntrySide.Debit,
                Amount = platformFeeAmount
            });
        }

        ValidateBalance(tx);
        await store.AddLedgerTransactionAsync(tx, ct);
        return tx;
    }

    public async Task<LedgerTransaction> PostDepositAsync(
        Guid userId,
        decimal amount,
        string currency,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ApiFailure(400, "invalid_amount", "Deposit amount must be greater than zero.");

        var riderAccount = await EnsureUserWalletAccountAsync(userId, currency, ct);
        var seedAccount = await EnsureClearingAccountAsync(LedgerAccountCodes.SeedClearing, currency, ct);

        var tx = new LedgerTransaction
        {
            PaymentId = Guid.Empty, // Platform deposit has no payment
            Type = LedgerTransactionTypes.WalletDeposit,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Entries = []
        };

        // Debit Clearing
        tx.Entries.Add(new LedgerEntry
        {
            TransactionId = tx.Id,
            AccountId = seedAccount.Id,
            Side = EntrySide.Debit,
            Amount = amount
        });

        // Credit User Wallet
        tx.Entries.Add(new LedgerEntry
        {
            TransactionId = tx.Id,
            AccountId = riderAccount.Id,
            Side = EntrySide.Credit,
            Amount = amount
        });

        ValidateBalance(tx);
        await store.AddLedgerTransactionAsync(tx, ct);
        return tx;
    }

    private static void ValidateBalance(LedgerTransaction tx)
    {
        var debitSum = tx.Entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount);
        var creditSum = tx.Entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount);

        if (debitSum != creditSum)
        {
            throw new ApiFailure(500, "ledger_unbalanced",
                $"Ledger transaction {tx.Id} is unbalanced: Debits={debitSum}, Credits={creditSum}");
        }

        if (tx.Entries.Any(e => e.Amount <= 0))
        {
            throw new ApiFailure(500, "invalid_entry_amount", "All ledger entries must have amounts strictly greater than zero.");
        }
    }
}
