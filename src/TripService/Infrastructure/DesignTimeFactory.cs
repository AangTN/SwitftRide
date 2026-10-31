using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SwiftRide.Trip.Infrastructure;
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TripDbContext>
{
    public TripDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__TripDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings__TripDb before running EF tools.");
        return new(new DbContextOptionsBuilder<TripDbContext>().UseSqlServer(connection).Options);
    }
}
