using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Api;

[ApiController, Route("api/matching")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[TypeFilter(typeof(MatchingExceptionFilter))]
public sealed class MatchingController(MatchingCoordinator coordinator) : ControllerBase
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

    [HttpPost("proposals/assign"), Authorize(Roles = "Rider")]
    public async Task<IActionResult> FindAndReserveNearest(DriverMatchRequest request, CancellationToken ct)
    {
        if (request.TripId == Guid.Empty || request.ProposalId == Guid.Empty)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["ids"] = ["TripId and ProposalId must be non-empty UUIDs."]
            }));
        var match = await coordinator.FindAndReserveNearestAsync(request, ct);
        return match is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "no_driver_available",
                Detail = "No eligible nearby driver is currently available."
            })
            : Ok(match);
    }

    [HttpPost("reservations/{proposalId:guid}/accept"), Authorize(Roles = "Driver")]
    public async Task<IActionResult> ConfirmReservation(Guid proposalId, [FromQuery] Guid tripId, CancellationToken ct)
    {
        if (proposalId == Guid.Empty || tripId == Guid.Empty)
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "invalid_reservation",
                Detail = "TripId and ProposalId must be non-empty UUIDs."
            });
        var confirmed = await coordinator.ConfirmReservationAsync(UserId(), tripId, proposalId, ct);
        return confirmed
            ? NoContent()
            : Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "reservation_not_active",
                Detail = "The reservation is expired, belongs to another driver, or no longer matches this trip."
            });
    }

    [HttpPost("reservations/{proposalId:guid}/decline"), Authorize(Roles = "Driver")]
    public async Task<IActionResult> DeclineReservation(Guid proposalId, [FromQuery] Guid tripId, CancellationToken ct)
    {
        if (proposalId == Guid.Empty || tripId == Guid.Empty)
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "invalid_reservation",
                Detail = "TripId and ProposalId must be non-empty UUIDs."
            });
        var released = await coordinator.ReleaseReservationAsync(UserId(), tripId, proposalId, ct);
        return released
            ? NoContent()
            : Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "reservation_not_active",
                Detail = "The reservation is expired, belongs to another driver, or no longer matches this trip."
            });
    }

    [HttpPost("quotes"), Authorize(Roles = "Rider")]
    public async Task<IActionResult> CreateFareQuote(FareQuoteRequest request, CancellationToken ct)
    {
        if (request.TripId == Guid.Empty || request.ProposalId == Guid.Empty)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["ids"] = ["TripId and ProposalId must be non-empty UUIDs."]
            }));
        var quote = await coordinator.CreateFareQuoteAsync(request, ct);
        return Ok(quote);
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

public sealed class MatchingExceptionFilter : IExceptionFilter
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
