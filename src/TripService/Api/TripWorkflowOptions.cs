namespace SwiftRide.Trip.Api;

public sealed class TripWorkflowOptions
{
    public int MaxDriverAttempts { get; init; } = 3;
    public int RiderApprovalSeconds { get; init; } = 120;
    public int DriverResponseSeconds { get; init; } = 30;
}
