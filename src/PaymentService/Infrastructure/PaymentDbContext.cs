using Microsoft.EntityFrameworkCore;
using SwiftRide.Payment.Domain;
using PaymentEntity = SwiftRide.Payment.Domain.Payment;

namespace SwiftRide.Payment.Infrastructure;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentEntity> Payments => Set<PaymentEntity>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PaymentOutboxMessage> OutboxMessages => Set<PaymentOutboxMessage>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PaymentOutboxMessage>(e =>
        {
            e.ToTable("outbox_messages"); e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
        b.Entity<PaymentEntity>(e =>
        {
            e.ToTable("payments", t => t.HasCheckConstraint("ck_payment_amount", "amount >= 0")); e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100); e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.TripId).IsUnique(); e.HasIndex(x => new { x.RiderId, x.CreatedAt });
            e.HasIndex(x => new { x.DriverId, x.CreatedAt });
            e.Property(x => x.Method).HasMaxLength(30);
            e.Property(x => x.ProviderReference).HasMaxLength(200); e.Property(x => x.FailureCode).HasMaxLength(100);
        });
        b.Entity<LedgerAccount>(e =>
        {
            e.ToTable("ledger_accounts"); e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50); e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => new { x.Code, x.OwnerUserId, x.Currency }).IsUnique().AreNullsDistinct(false);
        });
        b.Entity<LedgerTransaction>(e =>
        {
            e.ToTable("ledger_transactions"); e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(50); e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasOne<PaymentEntity>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_entry_amount", "amount > 0")); e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Side).HasConversion<string>().HasMaxLength(6);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties())
            p.SetColumnName(string.Concat(p.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}
