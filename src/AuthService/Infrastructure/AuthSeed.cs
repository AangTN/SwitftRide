using Microsoft.EntityFrameworkCore;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Infrastructure;

public static class DevelopmentAccounts
{
    public static readonly Guid RiderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DriverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
}

public static class AuthSeed
{
    public static async Task RunAsync(AuthDbContext db, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(83149001)", ct);
        var now = DateTimeOffset.UtcNow;
        var roleLookup = await db.Roles.ToDictionaryAsync(x => x.Name, ct);
        foreach (var (id, phone, name, roles) in new[]
        {
            (DevelopmentAccounts.RiderId, "+84900000001", "Rider Demo", new[] { RoleNames.Rider }),
            (DevelopmentAccounts.DriverId, "+84900000002", "Driver Demo", new[] { RoleNames.Driver }),
            (DevelopmentAccounts.AdminId, "+84900000003", "Admin Demo", new[] { RoleNames.Admin })
        })
        {
            var user = await db.Users.Include(x => x.UserRoles)
                .SingleOrDefaultAsync(x => x.PhoneNumber == phone, ct);
            if (user is null)
            {
                user = new() { Id = id, PhoneNumber = phone, FullName = name, Status = "Active",
                    PhoneVerifiedAt = now, CreatedAt = now, UpdatedAt = now };
                db.Users.Add(user);
            }
            // Never grant seeded privileges to a pre-existing account with a different identity.
            if (user.Id != id) throw new InvalidOperationException("A development seed phone is already in use.");
            foreach (var role in roles.Select(name => roleLookup[name]).Where(role => user.UserRoles.All(r => r.RoleId != role.Id)))
                user.UserRoles.Add(new() { UserId = user.Id, RoleId = role.Id, Role = role });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
