using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SwiftRide.Auth.Infrastructure;
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__AuthDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings__AuthDb before running EF tools.");
        return new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    }
}
