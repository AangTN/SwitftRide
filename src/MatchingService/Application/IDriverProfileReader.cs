using SwiftRide.Matching.Domain;

namespace SwiftRide.Matching.Application;

public interface IDriverProfileReader
{
    Task<DriverProfile?> FindAsync(string userId, CancellationToken ct);
}
