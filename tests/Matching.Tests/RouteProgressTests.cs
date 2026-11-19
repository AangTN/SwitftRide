using SwiftRide.Matching.Api;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class RouteProgressTests
{
    private const string Route = "_p~iF~ps|U_ulLnnqC_mqNvxq`@";

    [Fact]
    public void Remaining_fraction_decreases_along_route_and_refuses_off_route_position()
    {
        var start = RouteProgress.RemainingFraction(Route, 38.5, -120.2);
        var middle = RouteProgress.RemainingFraction(Route, 40.7, -120.95);
        var end = RouteProgress.RemainingFraction(Route, 43.252, -126.453);
        Assert.InRange(start!.Value, 0.999, 1);
        Assert.InRange(middle!.Value, 0.01, 0.99);
        Assert.InRange(end!.Value, 0, 0.001);
        Assert.Null(RouteProgress.RemainingFraction(Route, 10, 106));
        Assert.Null(RouteProgress.RemainingFraction("!", 10, 106));
    }
}
