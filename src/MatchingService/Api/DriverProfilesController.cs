using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Api;

[ApiController, Route("api/matching/drivers"), Authorize(Roles = "Driver")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DriverProfilesController(IDriverProfileReader profiles) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var id)) return Unauthorized();
        var profile = await profiles.FindAsync(id.ToString(), ct);
        if (profile is null) return Problem(statusCode: 404, title: "driver_profile_not_found", detail: "Driver profile is not available.");
        return Ok(new { driverId = profile.Id, profile.LegalName, profile.PlateNumber, profile.VehicleName,
            profile.VehicleType, profile.VehicleImageKey });
    }
}
