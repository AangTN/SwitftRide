using System.Security.Claims;
using MassTransit;
using SwiftRide.Contracts.Trips;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Api;

[ApiController, Route("api/matching")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[TypeFilter(typeof(MatchingExceptionFilter))]
public sealed class MatchingController(MatchingCoordinator coordinator, IMatchingStore store) : ControllerBase
{
    [HttpPut("drivers/me/availability"), Authorize(Roles = "Driver")]
    public async Task<IActionResult> UpdateAvailability(DriverLocationUpdate request, CancellationToken ct)
    {
        if (request.Online && (request.Latitude is null || request.Longitude is null))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["location"] = ["Latitude and longitude are required when going online."]
            }));
        if ((request.Latitude is null) != (request.Longitude is null))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["location"] = ["Latitude and longitude must be provided together."]
            }));
        if (request.Latitude is not null && request.Longitude is not null)
            MatchingCoordinator.ValidateCoordinate(request.Latitude.Value, request.Longitude.Value);

        var changed = await coordinator.UpdateAvailabilityAsync(UserId(), request, ct);
        return changed
            ? NoContent()
            : Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "driver_unavailable",
                Detail = "The driver profile is missing or the driver has an active reservation or trip."
            });
    }

    [HttpPut("drivers/me/location"), Authorize(Roles = "Driver")]
    public async Task<IActionResult> UpdateGps(DriverGpsRequest request, [FromServices] IPublishEndpoint bus, CancellationToken ct)
    {
        MatchingCoordinator.ValidateCoordinate(request.Latitude, request.Longitude);
        var now = DateTime.UtcNow;
        var driver = await store.UpdateGpsAsync(UserId(), request, now, ct);
        if (driver is null) return NotFound();
        if (driver.Status == "Busy" &&
            Guid.TryParse(driver.CurrentTripId, out var tripId) &&
            Guid.TryParse(driver.CurrentProposalId, out var proposalId))
            await bus.Publish(new DriverGpsUpdated(tripId, proposalId, Guid.Parse(driver.Id),
                request.Latitude, request.Longitude, new DateTimeOffset(now)), ct);
        return NoContent();
    }

    [HttpGet("places/geocode"), Authorize(Roles = "Rider,Driver")]
    public async Task<IActionResult> Geocode([FromQuery] string? address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length > 300)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["address"] = ["Address is required and must be at most 300 characters."]
            }));
        var result = await coordinator.GeocodeAsync(address, ct);
        return result is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "address_not_found",
                Detail = "The address provider could not find this address."
            })
            : Ok(result);
    }

    [HttpGet("places/search"), Authorize(Roles = "Rider,Driver")]
    public async Task<IActionResult> SearchPlaces([FromQuery] string? input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Trim().Length > 200)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["input"] = ["Search text is required and must be at most 200 characters."]
            }));
        return Ok(await coordinator.SearchPlacesAsync(input, ct));
    }

    [HttpGet("places/details"), Authorize(Roles = "Rider,Driver")]
    public async Task<IActionResult> PlaceDetails([FromQuery] string? placeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(placeId) || placeId.Trim().Length > 1024)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["placeId"] = ["Place ID is required and must be at most 1024 characters."]
            }));
        var result = await coordinator.GetPlaceDetailsAsync(placeId, ct);
        return result is null ? NotFound(new ProblemDetails
        {
            Status = 404, Title = "address_not_found", Detail = "The selected place is no longer available."
        }) : Ok(result);
    }

    [HttpGet("places/reverse"), Authorize(Roles = "Rider,Driver")]
    public async Task<IActionResult> ReverseGeocode(
        [FromQuery] double? latitude, [FromQuery] double? longitude, CancellationToken ct)
    {
        if (latitude is null || longitude is null)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["coordinates"] = ["Latitude and longitude are required."]
            }));
        MatchingCoordinator.ValidateCoordinate(latitude.Value, longitude.Value);
        var result = await coordinator.ReverseGeocodeAsync(latitude.Value, longitude.Value, ct);
        return result is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "address_not_found",
                Detail = "The address provider could not find this location."
            })
            : Ok(result);
    }

    private string UserId() => Guid.TryParse(User.FindFirstValue("sub"), out var id)
        ? id.ToString()
        : throw new MatchingFailureException(401, "invalid_identity", "A valid user identity is required.");
}

public sealed class MatchingExceptionFilter : Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not MatchingFailureException failure) return;
        var details = new ProblemDetails
        {
            Status = failure.StatusCode,
            Title = failure.Code,
            Detail = failure.Message
        };
        details.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(details) { StatusCode = failure.StatusCode };
        context.ExceptionHandled = true;
    }
}
