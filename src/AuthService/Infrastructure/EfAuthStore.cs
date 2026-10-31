using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SwiftRide.Auth.Application;
using SwiftRide.Auth.Domain;

namespace SwiftRide.Auth.Infrastructure;

public sealed class EfAuthStore(AuthDbContext db) : IAuthStore
{
    public async Task<IAuthTransaction> BeginAsync(CancellationToken ct) => new Transaction(await db.Database.BeginTransactionAsync(ct));
    public async Task LockAsync(string resource, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({resource}, 0))", ct);
        // SQLite is used only by sequential HTTP tests; production concurrency is tested against PostgreSQL.
    }
    private IQueryable<User> UserQuery => db.Users.Include(x => x.UserRoles).ThenInclude(x => x.Role);
    public Task<User?> FindUserByPhoneAsync(string phone, CancellationToken ct) => UserQuery.SingleOrDefaultAsync(x => x.PhoneNumber == phone, ct);
    public Task<User?> FindUserAsync(Guid id, CancellationToken ct) => UserQuery.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Role?> FindRoleByNameAsync(string name, CancellationToken ct) => db.Roles.SingleOrDefaultAsync(x => x.Name == name, ct);
    public async Task<OtpChallenge?> FindChallengeAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.OtpChallenges.FindAsync([id], ct);
        if (entity is not null) await db.Entry(entity).ReloadAsync(ct);
        return entity;
    }
    public Task<OtpChallenge?> LatestChallengeAsync(string phone, string purpose, CancellationToken ct) =>
        db.OtpChallenges.Where(x => x.PhoneNumber == phone && x.Purpose == purpose).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    public Task<int> CountIpPhoneRequestsAsync(string ip, string phone, DateTimeOffset since, CancellationToken ct) =>
        db.OtpChallenges.CountAsync(x => x.RequestIp == ip && x.PhoneNumber == phone && x.CreatedAt >= since, ct);
    public async Task InvalidateChallengesAsync(string phone, string purpose, DateTimeOffset now, CancellationToken ct)
    {
        var challenges = await db.OtpChallenges.Where(x => x.PhoneNumber == phone && x.Purpose == purpose &&
            x.ConsumedAt == null && x.InvalidatedAt == null).ToListAsync(ct);
        foreach (var challenge in challenges) challenge.InvalidatedAt = now;
    }
    public async Task<RefreshToken?> FindRefreshAsync(string hash, CancellationToken ct)
    {
        var entity = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (entity is not null) await db.Entry(entity).ReloadAsync(ct);
        return entity;
    }
    public Task<AuthSession?> FindSessionAsync(Guid id, CancellationToken ct) => db.AuthSessions.SingleOrDefaultAsync(x => x.Id == id, ct);
    public void Add(User user) => db.Users.Add(user);
    public void Add(OtpChallenge challenge) => db.OtpChallenges.Add(challenge);
    public void Add(AuthSession session) => db.AuthSessions.Add(session);
    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    private sealed class Transaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
