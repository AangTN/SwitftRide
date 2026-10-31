using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SwiftRide.Payment.Infrastructure;
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__PaymentDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings__PaymentDb before running EF tools.");
        return new(new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(connection).Options);
    }
}
