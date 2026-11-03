using Microsoft.EntityFrameworkCore;
using SwiftRide.Payment.Domain;

namespace SwiftRide.Payment.Infrastructure;

public static class PaymentDevelopmentAccounts
{
    public static readonly Guid RiderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DriverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid BrokeRiderId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static readonly Guid SeedPaymentId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid SeedTripId = Guid.Parse("11111111-0000-0000-0000-000000000002");
    public const decimal RiderDemoInitialBalance = 1_000_000m; // 1,000,000 VND
}

public static class PaymentSeed
{
    public static async Task RunAsync(PaymentDbContext db, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(83149002)", ct);

        var now = DateTimeOffset.UtcNow;

        // 1. Ensure platform accounts
        var seedClearing = await GetOrCreateAccountAsync(db, LedgerAccountCodes.SeedClearing, null, "VND", now, ct);
        var paymentClearing = await GetOrCreateAccountAsync(db, LedgerAccountCodes.PaymentClearing, null, "VND", now, ct);
        var platformRevenue = await GetOrCreateAccountAsync(db, LedgerAccountCodes.PlatformRevenue, null, "VND", now, ct);

        // 2. Ensure driver payable account
        var driverPayable = await GetOrCreateAccountAsync(db, LedgerAccountCodes.DriverPayable, PaymentDevelopmentAccounts.DriverId, "VND", now, ct);

        // 3. Ensure broke rider wallet (0 VND)
        var brokeRiderWallet = await GetOrCreateAccountAsync(db, LedgerAccountCodes.RiderWallet, PaymentDevelopmentAccounts.BrokeRiderId, "VND", now, ct);

        // 4. Ensure demo rider wallet and fund with demo balance
        var riderWallet = await GetOrCreateAccountAsync(db, LedgerAccountCodes.RiderWallet, PaymentDevelopmentAccounts.RiderId, "VND", now, ct);

        var existingSeedPayment = await db.Payments.SingleOrDefaultAsync(p => p.Id == PaymentDevelopmentAccounts.SeedPaymentId, ct);
        if (existingSeedPayment is null)
        {
            var seedPayment = new Domain.Payment
            {
                Id = PaymentDevelopmentAccounts.SeedPaymentId,
                TripId = PaymentDevelopmentAccounts.SeedTripId,
                RiderId = PaymentDevelopmentAccounts.RiderId,
                DriverId = PaymentDevelopmentAccounts.RiderId,
                Amount = PaymentDevelopmentAccounts.RiderDemoInitialBalance,
                Currency = "VND",
                Status = PaymentStatus.Succeeded,
                IdempotencyKey = "seed-deposit-rider-demo",
                ProviderReference = "SEED-INITIAL-BALANCE",
                PaidAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            var seedTx = new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                PaymentId = seedPayment.Id,
                Type = LedgerTransactionTypes.WalletDeposit,
                IdempotencyKey = "seed-tx-rider-demo",
                CreatedAt = now,
                Entries =
                [
                    new LedgerEntry
                    {
                        AccountId = seedClearing.Id,
                        Side = EntrySide.Debit,
                        Amount = PaymentDevelopmentAccounts.RiderDemoInitialBalance
                    },
                    new LedgerEntry
                    {
                        AccountId = riderWallet.Id,
                        Side = EntrySide.Credit,
                        Amount = PaymentDevelopmentAccounts.RiderDemoInitialBalance
                    }
                ]
            };

            db.Payments.Add(seedPayment);
            db.LedgerTransactions.Add(seedTx);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<LedgerAccount> GetOrCreateAccountAsync(
        PaymentDbContext db, string code, Guid? ownerUserId, string currency, DateTimeOffset now, CancellationToken ct)
    {
        var account = await db.LedgerAccounts.SingleOrDefaultAsync(
            a => a.Code == code && a.OwnerUserId == ownerUserId && a.Currency == currency, ct);

        if (account is not null) return account;

        account = new LedgerAccount
        {
            Id = Guid.NewGuid(),
            Code = code,
            OwnerUserId = ownerUserId,
            Currency = currency,
            CreatedAt = now
        };

        db.LedgerAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }
}
