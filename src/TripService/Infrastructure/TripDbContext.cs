using Microsoft.EntityFrameworkCore;
using SwiftRide.Trip.Domain;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Infrastructure;

public sealed class TripDbContext(DbContextOptions<TripDbContext> options) : DbContext(options)
{
    public DbSet<TripEntity> Trips => Set<TripEntity>();
    public DbSet<TripStatusHistory> TripStatusHistory => Set<TripStatusHistory>();
    public DbSet<TripProposal> TripProposals => Set<TripProposal>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TripEntity>(e =>
        {
            e.ToTable("trips", t => t.HasCheckConstraint("ck_trip_amount", "[agreed_amount] IS NULL OR [agreed_amount] >= 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.StateVersion).IsConcurrencyToken();
            e.Property(x => x.AgreedAmount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.PickupAddress).HasMaxLength(500); e.Property(x => x.DestinationAddress).HasMaxLength(500);
            e.Property(x => x.QuoteId).HasMaxLength(36);
            e.Property(x => x.DriverNameSnapshot).HasMaxLength(150); e.Property(x => x.PlateNumberSnapshot).HasMaxLength(20);
            e.Property(x => x.VehicleNameSnapshot).HasMaxLength(150); e.Property(x => x.VehicleImageKeySnapshot).HasMaxLength(500);
            e.Property(x => x.PaymentId).HasMaxLength(100); e.Property(x => x.PaymentStatus).HasMaxLength(30);
            e.HasIndex(x => new { x.RiderId, x.CreatedAt }); e.HasIndex(x => new { x.DriverId, x.Status });
            e.HasIndex(x => new { x.RiderId, x.IdempotencyKey }).IsUnique().HasFilter("[idempotency_key] <> ''");
            e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Proposals).WithOne().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<TripStatusHistory>(e =>
        {
            e.ToTable("trip_status_history"); e.HasKey(x => x.Id);
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(x => new { x.TripId, x.CreatedAt });
        });
        b.Entity<TripProposal>(e =>
        {
            e.ToTable("trip_proposals"); e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.QuoteId).HasMaxLength(100); e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.DriverNameSnapshot).HasMaxLength(150);
            e.Property(x => x.PlateNumberSnapshot).HasMaxLength(20); e.Property(x => x.VehicleNameSnapshot).HasMaxLength(150);
            e.Property(x => x.VehicleImageKeySnapshot).HasMaxLength(500);
            e.HasIndex(x => new { x.TripId, x.DriverId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.DriverResponseDeadline });
        });
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties())
            p.SetColumnName(string.Concat(p.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}
