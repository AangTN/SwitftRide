using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftRide.Auth.Domain;
using SwiftRide.Auth.Infrastructure;

namespace SwiftRide.Auth.Tests;

public sealed class RoleTests : IAsyncLifetime
{
    private readonly AuthFactory factory = new();
    public Task InitializeAsync() => factory.InitializeAsync();
    public async Task DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task Duplicate_role_name_is_rejected_even_with_different_ids()
    {
        await factory.InDb(async db =>
        {
            db.Roles.Add(new() { Id = Guid.NewGuid(), Name = RoleNames.Rider });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Duplicate_user_role_assignment_is_rejected()
    {
        await factory.InDb(async db =>
        {
            db.UserRoles.Add(new() { UserId = Infrastructure.DevelopmentAccounts.RiderId, RoleId = RoleIds.Rider });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Unknown_role_id_is_rejected_by_foreign_key()
    {
        await factory.InDb(async db =>
        {
            db.UserRoles.Add(new() { UserId = Infrastructure.DevelopmentAccounts.RiderId, RoleId = Guid.NewGuid() });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Renaming_a_custom_role_preserves_user_assignment()
    {
        var roleId = Guid.NewGuid();
        await factory.InDb(async db =>
        {
            db.Roles.Add(new() { Id = roleId, Name = "Support" });
            db.UserRoles.Add(new() { UserId = Infrastructure.DevelopmentAccounts.RiderId, RoleId = roleId });
            await db.SaveChangesAsync();
        });
        await factory.InDb(async db =>
        {
            (await db.Roles.FindAsync(roleId))!.Name = "SupportAgent";
            await db.SaveChangesAsync();
        });
        await factory.InDb(async db =>
        {
            var assignment = await db.UserRoles.Include(x => x.Role).SingleAsync(x => x.RoleId == roleId);
            Assert.Equal("SupportAgent", assignment.Role.Name);
            Assert.Equal(roleId, assignment.RoleId);
        });
    }
}

public sealed class RoleMigrationTests
{
    [PostgresFact]
    public async Task Otp_app_migration_invalidates_unbound_challenges_but_preserves_quota_history()
    {
        const string previous = "20261027124132_MoveDriverProfilesToMatching";
        await using var factory = new AuthFactory();
        await factory.InitializeAsync(previous, seed: false);
        await factory.InDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO otp_challenges (id, phone_number, purpose, code_hash, request_ip, attempt_count, created_at, expires_at)
                VALUES ('99999999-9999-9999-9999-999999999999', '+84912345678', 'Login', 'old-hash', '192.0.2.1', 2, now(), now() + interval '5 minutes');
                """);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
            var old = await db.OtpChallenges.SingleAsync();
            Assert.Equal("", old.ClientApp);
            Assert.NotNull(old.InvalidatedAt);
            Assert.Equal(2, old.AttemptCount);
            Assert.Equal(1, await new EfAuthStore(db).CountIpPhoneRequestsAsync("192.0.2.1", "+84912345678", factory.Clock.GetUtcNow().AddHours(-1), default));
            await migrator.MigrateAsync(previous);
            db.ChangeTracker.Clear();
            await migrator.MigrateAsync();
            Assert.Equal(1, await db.OtpChallenges.CountAsync());
        });
    }

    [PostgresFact]
    public async Task Driver_profile_migration_archives_identity_and_removes_only_seeded_rider_once()
    {
        const string previous = "20261027112302_AddRoleIds";
        await using var factory = new AuthFactory();
        await factory.InitializeAsync(previous);
        await factory.InDb(async db => {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO driver_profiles (user_id, plate_number, vehicle_name, vehicle_image_key, created_at, updated_at)
                VALUES ('22222222-2222-2222-2222-222222222222', '59A1-12345', 'Honda Wave Alpha', 'seed/vehicle.svg', now(), now());
                INSERT INTO user_roles (user_id, role_id) VALUES ('22222222-2222-2222-2222-222222222222', '10000000-0000-0000-0000-000000000001');
                """);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(1, await db.UserRoles.CountAsync(x => x.UserId == DevelopmentAccounts.DriverId));
            var name = await db.Database.SqlQueryRaw<string>("SELECT legal_name AS \"Value\" FROM legacy_driver_profiles").SingleAsync();
            Assert.Equal("Driver Demo", name);
            var user = (await db.Users.FindAsync(DevelopmentAccounts.DriverId))!;
            user.FullName = "Tên khách mới";
            db.UserRoles.Add(new() { UserId = user.Id, RoleId = RoleIds.Rider });
            await db.SaveChangesAsync();
            await migrator.MigrateAsync();
            await AuthSeed.RunAsync(db);
            Assert.Equal(2, await db.UserRoles.CountAsync(x => x.UserId == user.Id));
            Assert.Equal("Driver Demo", await db.Database.SqlQueryRaw<string>("SELECT legal_name AS \"Value\" FROM legacy_driver_profiles").SingleAsync());
            await migrator.MigrateAsync(previous);
            Assert.Equal("Honda Wave Alpha", await db.Database.SqlQueryRaw<string>("SELECT vehicle_name AS \"Value\" FROM driver_profiles").SingleAsync());
        });
    }

    [PostgresFact]
    public async Task Upgrade_and_rollback_preserve_legacy_roles_and_assignments()
    {
        const string initial = "20261026163841_InitialAuth";
        await using var factory = new AuthFactory();
        await factory.InitializeAsync(initial, seed: false);
        await factory.InDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO users (id, phone_number, full_name, status, phone_verified_at, created_at, updated_at)
                VALUES ('99999999-9999-9999-9999-999999999999', '+84912345678', 'Legacy User', 'Active', now(), now(), now());
                INSERT INTO roles (name) VALUES ('Support');
                INSERT INTO user_roles (user_id, role_name) VALUES
                    ('99999999-9999-9999-9999-999999999999', 'Rider'),
                    ('99999999-9999-9999-9999-999999999999', 'Support');
                """);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
            Assert.Equal(4, await db.Roles.CountAsync());
            Assert.Equal(RoleIds.Rider, (await db.Roles.SingleAsync(x => x.Name == RoleNames.Rider)).Id);
            var grants = await db.UserRoles.Include(x => x.Role).OrderBy(x => x.Role.Name).ToListAsync();
            Assert.Equal(new[] { "Rider", "Support" }, grants.Select(x => x.Role.Name));
            Assert.All(grants, x => Assert.NotEqual(Guid.Empty, x.RoleId));
            await migrator.MigrateAsync(initial);
            await db.Database.OpenConnectionAsync();
            await using (var command = new NpgsqlCommand("SELECT role_name FROM user_roles ORDER BY role_name", (NpgsqlConnection)db.Database.GetDbConnection()))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var names = new List<string>();
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                Assert.Equal(new[] { "Rider", "Support" }, names);
            }
            // Restore the latest schema to verify repeatable upgrade, not just rollback.
            db.ChangeTracker.Clear();
            await migrator.MigrateAsync();
            Assert.Equal(2, await db.UserRoles.CountAsync());
        });
    }
}
