using Microsoft.EntityFrameworkCore;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Infrastructure;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users"); e.HasKey(x => x.Id);
            e.Property(x => x.PhoneNumber).HasMaxLength(16); e.HasIndex(x => x.PhoneNumber).IsUnique();
            e.Property(x => x.FullName).HasMaxLength(150);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasMany(x => x.UserRoles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasData(new Role { Id = RoleIds.Rider, Name = RoleNames.Rider },
                new Role { Id = RoleIds.Driver, Name = RoleNames.Driver },
                new Role { Id = RoleIds.Admin, Name = RoleNames.Admin });
        });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles"); e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<OtpChallenge>(e =>
        {
            e.ToTable("otp_challenges"); e.HasKey(x => x.Id);
            e.Property(x => x.PhoneNumber).HasMaxLength(16); e.Property(x => x.Purpose).HasMaxLength(20);
            e.Property(x => x.ClientApp).HasMaxLength(20);
            e.Property(x => x.RegistrationFullName).HasMaxLength(150); e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.RequestIp).HasMaxLength(64);
            e.HasIndex(x => new { x.PhoneNumber, x.CreatedAt });
            e.HasIndex(x => new { x.RequestIp, x.PhoneNumber, x.CreatedAt });
        });
        b.Entity<AuthSession>(e =>
        {
            e.ToTable("auth_sessions"); e.HasKey(x => x.Id);
            e.Property(x => x.ClientApp).HasMaxLength(20); e.Property(x => x.DeviceName).HasMaxLength(200);
            e.Property(x => x.RevocationReason).HasMaxLength(50);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.ExpiresAt });
        });
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens"); e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64); e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Id }).IsUnique();
            // Composite FK ensures replacements cannot point into another login session.
            e.HasOne<RefreshToken>().WithMany().HasForeignKey(x => new { x.SessionId, x.ReplacedByTokenId })
                .HasPrincipalKey(x => new { x.SessionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(Snake(property.Name));
    }

    private static string Snake(string name) => string.Concat(name.Select((c, i) =>
        char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
