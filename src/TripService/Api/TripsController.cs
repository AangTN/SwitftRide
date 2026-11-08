using System.Security.Claims;
using SwiftRide.Contracts.Trips;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

[ApiController, Route("api/trips"), Authorize, TypeFilter(typeof(TripExceptionFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TripsController(TripDbContext db, ITripRealtimePublisher realtime, ITripMatchingNotifier matching, ITripPaymentNotifier payment, TimeProvider clock, IOptions<TripWorkflowOptions> workflow) : ControllerBase
{
    private static readonly TripStatus[] InServiceStatuses =
    [
        TripStatus.Requested, TripStatus.Matching, TripStatus.AwaitingRiderApproval,
        TripStatus.AwaitingDriverResponse, TripStatus.DriverAccepted, TripStatus.EnRoute, TripStatus.PickedUp
    ];

    [HttpPost]
    public async Task<IActionResult> Create(CreateTripRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var riderId = UserId(); RequireRole("Rider");
        if (request.VehicleType is not ("Motorbike" or "Car4Seats" or "Car7Seats")) throw Failure(400, "invalid_vehicle_type", "Invalid vehicle type.");
        ValidateCoordinate(request.PickupLatitude, request.PickupLongitude);
        ValidateCoordinate(request.DestinationLatitude, request.DestinationLongitude);
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw Failure(400, "idempotency_key_required", "Idempotency-Key is required.");
        var existing = await db.Trips.Include(x => x.Proposals).SingleOrDefaultAsync(x => x.RiderId == riderId && x.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return Ok(Snapshot(existing));
        var active = await ActiveTripForRider(riderId, ct);
        if (active is not null) return ActiveTripConflict(active.Id);
        var profile = await HttpContext.RequestServices.GetRequiredService<RiderProfileClient>()
            .GetAsync(riderId, Request.Headers.Authorization.ToString(), ct);
        var now = clock.GetUtcNow();
        var trip = new TripEntity
        {
            RiderId = riderId, RiderNameSnapshot = profile.FullName, RiderPhoneSnapshot = profile.PhoneNumber, IdempotencyKey = idempotencyKey, PickupAddress = request.PickupAddress,
            PickupLatitude = request.PickupLatitude, PickupLongitude = request.PickupLongitude,
            DestinationAddress = request.DestinationAddress, DestinationLatitude = request.DestinationLatitude,
            DestinationLongitude = request.DestinationLongitude, VehicleType = request.VehicleType, CreatedAt = now, UpdatedAt = now
        };
        TripStateMachine.Move(trip, TripStatus.Matching, riderId, now, "matching_requested");
        trip.Proposals.Add(new TripProposal { TripId = trip.Id, CreatedAt = now, Status = ProposalStatus.Searching });
        db.Trips.Add(trip);
        await matching.RequestOpenProposalsAsync(trip, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var duplicate = await db.Trips.Include(x => x.Proposals).SingleOrDefaultAsync(x => x.RiderId == riderId && x.IdempotencyKey == idempotencyKey, ct);
            if (duplicate is not null) return Ok(Snapshot(duplicate));
            active = await ActiveTripForRider(riderId, ct);
            if (active is not null) return ActiveTripConflict(active.Id);
            throw;
        }
        await realtime.PublishTripChangedAsync(trip, ct);
        return CreatedAtAction(nameof(Get), new { tripId = trip.Id }, Snapshot(trip));
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var userId = UserId();
        var trip = await db.Trips.Include(x => x.Proposals).Where(x => (x.RiderId == userId || x.DriverId == userId || x.Proposals.Any(p => p.DriverId == userId && p.Status == ProposalStatus.AwaitingDriverResponse)) &&
            InServiceStatuses.Contains(x.Status)).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        return trip is null ? NoContent() : Ok(Snapshot(trip));
    }

    private Task<TripEntity?> ActiveTripForRider(Guid riderId, CancellationToken ct) =>
        db.Trips.AsNoTracking().FirstOrDefaultAsync(x => x.RiderId == riderId && InServiceStatuses.Contains(x.Status), ct);

    private static ConflictObjectResult ActiveTripConflict(Guid tripId) => new(new ProblemDetails
    {
        Status = StatusCodes.Status409Conflict,
        Title = "active_trip_exists",
        Detail = "Finish or cancel the current trip before booking another.",
        Extensions = { ["tripId"] = tripId }
    });

    [HttpGet("{tripId:guid}")]
    public async Task<IActionResult> Get(Guid tripId, CancellationToken ct)
    {
        var trip = await LoadForUserOrDriverInvitation(tripId, ct);
        return Ok(Snapshot(trip));
    }

    [HttpGet("{tripId:guid}/route")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RouteForTrip(Guid tripId, CancellationToken ct)
    {
        var trip = await LoadForUserOrDriverInvitation(tripId, ct);
        var route = TripRouting.ActiveStage(trip);
        if (route is null) return await RoutePreviewAsync(trip, ct);
        var client = HttpContext.RequestServices.GetService<MassTransit.IRequestClient<SwiftRide.Contracts.Trips.GetTripRoutes>>();
        if (client is null) throw Failure(503, "trip_route_unavailable", "The trip route is temporarily unavailable.");
        try
        {
            var response = await client.GetResponse<SwiftRide.Contracts.Trips.TripRoutesUpdated>(
                new SwiftRide.Contracts.Trips.GetTripRoutes(route), ct, MassTransit.RequestTimeout.After(s: 15));
            var latest = await db.Trips.AsNoTracking().SingleAsync(x => x.Id == tripId, ct);
            if (latest.DriverId != route.DriverId || !TripRouting.IsCurrentStage(latest.Status, route.Stage))
                return NoContent();
            var routes = response.Message;
            return routes.ToPickup is null && routes.ToDestination is null ? NoContent() : Ok(routes);
        }
        catch (MassTransit.RequestException)
        {
            throw Failure(503, "trip_route_unavailable", "The trip route is temporarily unavailable.");
        }
    }

    private async Task<IActionResult> RoutePreviewAsync(TripEntity trip, CancellationToken ct)
    {
        if (trip.Status is not (TripStatus.Matching or TripStatus.AwaitingRiderApproval or TripStatus.AwaitingDriverResponse))
            return NoContent();
        var proposal = trip.Proposals.SingleOrDefault(p => p.Status is ProposalStatus.Searching or
            ProposalStatus.AwaitingRiderApproval or ProposalStatus.AwaitingDriverResponse);
        if (proposal is null) return NoContent();
        var userId = UserId();
        if (trip.RiderId != userId &&
            (trip.Status != TripStatus.AwaitingDriverResponse || proposal.DriverId != userId ||
                proposal.Status != ProposalStatus.AwaitingDriverResponse || proposal.DriverResponseDeadline is null ||
                proposal.DriverResponseDeadline <= clock.GetUtcNow()))
            return NoContent();

        var client = HttpContext.RequestServices.GetService<MassTransit.IRequestClient<GetTripRoutePreview>>();
        if (client is null) return NoContent();
        try
        {
            var response = await client.GetResponse<TripRoutePreview>(new GetTripRoutePreview(trip.Id, proposal.Id),
                ct, MassTransit.RequestTimeout.After(s: 3));
            var now = clock.GetUtcNow();
            if ((response.Message.ToDestination is null && response.Message.PickupEtaMinutes is null) ||
                !await db.Trips.WithRoutePreview(proposal.Id).AnyAsync(t => t.Id == trip.Id &&
                    (t.RiderId == userId || (t.Status == TripStatus.AwaitingDriverResponse &&
                        t.Proposals.Any(p => p.Id == proposal.Id && p.DriverId == userId &&
                            p.Status == ProposalStatus.AwaitingDriverResponse && p.DriverResponseDeadline > now))), ct))
                return NoContent();
            return Ok(response.Message);
        }
        catch (MassTransit.RequestException)
        {
            return NoContent();
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var user = UserId(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Trips.Where(x => x.RiderId == user || x.DriverId == user).OrderByDescending(x => x.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Include(x => x.Proposals).ToListAsync(ct);
        return Ok(new { page, pageSize, total, items = items.Select(x => Snapshot(x)) });
    }

    [HttpPost("{tripId:guid}/proposals/{proposalId:guid}/approve")]
    public Task<IActionResult> Approve(Guid tripId, Guid proposalId, VersionRequest request, CancellationToken ct) => RiderProposalAction(tripId, proposalId, request, true, ct);
    [HttpPost("{tripId:guid}/proposals/{proposalId:guid}/reject")]
    public Task<IActionResult> Reject(Guid tripId, Guid proposalId, VersionRequest request, CancellationToken ct) => RiderProposalAction(tripId, proposalId, request, false, ct);

    [HttpPost("{tripId:guid}/driver-accept")]
    public Task<IActionResult> DriverAccept(Guid tripId, DriverProposalRequest request, CancellationToken ct) => DriverAnswer(tripId, request, true, ct);
    [HttpPost("{tripId:guid}/driver-reject")]
    public Task<IActionResult> DriverReject(Guid tripId, DriverProposalRequest request, CancellationToken ct) => DriverAnswer(tripId, request, false, ct);

    [HttpPost("{tripId:guid}/en-route")]
    public Task<IActionResult> EnRoute(Guid tripId, VersionRequest request, CancellationToken ct) => DriverTransition(tripId, request, TripStatus.EnRoute, ct);
    [HttpPost("{tripId:guid}/pickup")]
    public Task<IActionResult> Pickup(Guid tripId, VersionRequest request, CancellationToken ct) => DriverTransition(tripId, request, TripStatus.PickedUp, ct);

    [HttpPost("{tripId:guid}/dropoff")]
    public async Task<IActionResult> Dropoff(Guid tripId, VersionRequest request, CancellationToken ct)
    {
        var trip = await DriverTrip(tripId, request.StateVersion, ct); var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        TripStateMachine.Move(trip, TripStatus.DroppedOff, UserId(), now);
        trip.DroppedOffAt = now; trip.CompletedAt = now; trip.PaymentId = Guid.NewGuid().ToString("N"); trip.PaymentStatus = "Pending";
        TripStateMachine.Move(trip, TripStatus.PaymentPending, UserId(), now, "payment_requested");
        await payment.RequestAsync(trip, ct);
        foreach (var proposal in trip.Proposals.Where(x => x.Status == ProposalStatus.DriverAccepted))
            await matching.ReleaseAsync(proposal, "trip_dropped_off", ct);
        TripRouting.EnqueueCleanup(db, trip, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await realtime.PublishTripChangedAsync(trip, ct); await realtime.PublishRideCompletedAsync(trip, ct);
        return Ok(Snapshot(trip));
    }

    [HttpPost("{tripId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid tripId, VersionRequest request, CancellationToken ct)
    {
        var trip = await LoadForUser(tripId, ct); EnsureVersion(trip, request.StateVersion);
        if (trip.Status is TripStatus.PickedUp or TripStatus.DroppedOff or TripStatus.PaymentPending) throw Failure(409, "trip_cannot_be_cancelled", "This trip can no longer be cancelled.");
        var now = clock.GetUtcNow();
        var release = trip.Proposals.Where(x => x.Status is ProposalStatus.AwaitingDriverResponse or ProposalStatus.AwaitingRiderApproval or ProposalStatus.DriverAccepted).ToList();
        TripStateMachine.Move(trip, TripStatus.Cancelled, UserId(), now, "cancelled_by_user");
        foreach (var proposal in trip.Proposals.Where(x => x.Status is ProposalStatus.AwaitingDriverResponse or ProposalStatus.AwaitingRiderApproval or ProposalStatus.Searching)) { proposal.Status = ProposalStatus.Cancelled; proposal.ResolvedAt = now; }
        foreach (var proposal in release) await matching.ReleaseAsync(proposal, "trip_cancelled", ct);
        TripRouting.EnqueueCleanup(db, trip, now);
        await db.SaveChangesAsync(ct);
        await realtime.PublishTripChangedAsync(trip, ct); return Ok(Snapshot(trip));
    }

    private async Task<IActionResult> RiderProposalAction(Guid tripId, Guid proposalId, VersionRequest request, bool approve, CancellationToken ct)
    {
        RequireRole("Rider"); var trip = await LoadForUser(tripId, ct);
        if (trip.RiderId != UserId()) throw Failure(403, "trip_forbidden", "Only the rider can perform this action.");
        var proposal = trip.Proposals.SingleOrDefault(x => x.Id == proposalId) ?? throw Failure(404, "proposal_not_found", "Proposal does not belong to this trip.");
        if (approve && proposal.Status == ProposalStatus.AwaitingDriverResponse) return Ok(Snapshot(trip));
        if (!approve && proposal.Status == ProposalStatus.RiderRejected) return Ok(Snapshot(trip));
        EnsureVersion(trip, request.StateVersion);
        if (proposal.Status != ProposalStatus.AwaitingRiderApproval || proposal.RiderApprovalDeadline <= clock.GetUtcNow()) throw Failure(409, "proposal_not_actionable", "Proposal is no longer awaiting the rider.");
        var now = clock.GetUtcNow(); proposal.Status = approve ? ProposalStatus.AwaitingDriverResponse : ProposalStatus.RiderRejected; proposal.ResolvedAt = now; proposal.RiderRespondedAt = now;
        if (approve) { proposal.DriverResponseDeadline = now.AddSeconds(workflow.Value.DriverResponseSeconds); TripStateMachine.Move(trip, TripStatus.AwaitingDriverResponse, UserId(), now, "rider_approved_quote"); }
        else
        {
            TripProposalRetry.Advance(trip, workflow.Value.MaxDriverAttempts, UserId(), now, "rider_rejected_quote");
            await matching.ReleaseAsync(proposal, "rider_rejected_quote", ct);
            if (trip.Status == TripStatus.Failed) TripRouting.EnqueueCleanup(db, trip, now);
            await matching.RequestOpenProposalsAsync(trip, ct);
        }
        await db.SaveChangesAsync(ct);
        await realtime.PublishTripChangedAsync(trip, ct); if (approve) await realtime.PublishDriverInvitationAsync(trip, proposal, ct);
        return Ok(Snapshot(trip));
    }

    private async Task<IActionResult> DriverAnswer(Guid tripId, DriverProposalRequest request, bool accept, CancellationToken ct)
    {
        RequireRole("Driver"); var trip = await LoadForUserOrDriverInvitation(tripId, ct); var proposal = trip.Proposals.SingleOrDefault(x => x.Id == request.ProposalId && x.DriverId == UserId()) ?? throw Failure(404, "proposal_not_found", "No invitation is available for this driver.");
        if (accept && proposal.Status == ProposalStatus.DriverAccepted && trip.DriverId == UserId()) return Ok(Snapshot(trip));
        EnsureVersion(trip, request.StateVersion); var now = clock.GetUtcNow();
        if (proposal.Status != ProposalStatus.AwaitingDriverResponse || proposal.DriverResponseDeadline <= now) throw Failure(409, "proposal_expired", "The invitation is no longer active.");
        proposal.Status = accept ? ProposalStatus.DriverAccepted : ProposalStatus.DriverRejected; proposal.ResolvedAt = now;
        if (accept) { trip.DriverId = UserId(); trip.QuoteId = proposal.QuoteId; trip.AgreedAmount = proposal.Amount; trip.Currency = proposal.Currency; trip.DriverNameSnapshot = proposal.DriverNameSnapshot; trip.DriverAvatarImageKeySnapshot = proposal.DriverAvatarImageKeySnapshot; trip.PlateNumberSnapshot = proposal.PlateNumberSnapshot; trip.VehicleNameSnapshot = proposal.VehicleNameSnapshot; trip.VehicleImageKeySnapshot = proposal.VehicleImageKeySnapshot; trip.DriverAcceptedAt = now; TripStateMachine.Move(trip, TripStatus.DriverAccepted, UserId(), now); }
        else TripProposalRetry.Advance(trip, workflow.Value.MaxDriverAttempts, UserId(), now, "driver_rejected");
        if (!accept) { await matching.ReleaseAsync(proposal, "driver_rejected", ct); await matching.RequestOpenProposalsAsync(trip, ct); }
        else
        {
            await matching.ConfirmAsync(proposal, ct);
            TripRouting.EnqueueStage(db, trip, now);
        }
        if (trip.Status == TripStatus.Failed) TripRouting.EnqueueCleanup(db, trip, now);
        await db.SaveChangesAsync(ct);
        await realtime.PublishTripChangedAsync(trip, ct); return Ok(Snapshot(trip));
    }

    private async Task<IActionResult> DriverTransition(Guid tripId, VersionRequest request, TripStatus target, CancellationToken ct)
    {
        var trip = await DriverTrip(tripId, request.StateVersion, ct); var now = clock.GetUtcNow(); TripStateMachine.Move(trip, target, UserId(), now);
        if (target == TripStatus.PickedUp)
        {
            trip.PickedUpAt = now;
            TripRouting.EnqueueStage(db, trip, now);
        }
        await db.SaveChangesAsync(ct); await realtime.PublishTripChangedAsync(trip, ct); return Ok(Snapshot(trip));
    }

    private async Task<TripEntity> RiderTrip(Guid id, int version, CancellationToken ct) { var trip = await LoadForUser(id, ct); if (trip.RiderId != UserId()) throw Failure(403, "trip_forbidden", "Only the rider can perform this action."); EnsureVersion(trip, version); return trip; }
    private async Task<TripEntity> DriverTrip(Guid id, int version, CancellationToken ct) { RequireRole("Driver"); var trip = await LoadForUser(id, ct); if (trip.DriverId != UserId()) throw Failure(403, "trip_forbidden", "Only the assigned driver can perform this action."); EnsureVersion(trip, version); return trip; }
    private async Task<TripEntity> LoadForUser(Guid id, CancellationToken ct) { var user = UserId(); return await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id && (x.RiderId == user || x.DriverId == user), ct) ?? throw Failure(404, "trip_not_found", "Trip was not found."); }
    private async Task<TripEntity> LoadForUserOrDriverInvitation(Guid id, CancellationToken ct) { var user = UserId(); return await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id && (x.RiderId == user || x.DriverId == user || x.Proposals.Any(p => p.DriverId == user && p.Status == ProposalStatus.AwaitingDriverResponse)), ct) ?? throw Failure(404, "trip_not_found", "Trip was not found."); }
    private Guid UserId() => Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : throw Failure(401, "invalid_subject", "Token subject is invalid.");
    private void RequireRole(string role) { if (!User.IsInRole(role)) throw Failure(403, "role_forbidden", $"The {role} role is required."); }
    private static void ValidateCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw Failure(400, "invalid_coordinates", "Invalid pickup or destination coordinates.");
    }
    private static void EnsureVersion(TripEntity trip, int version) { if (trip.StateVersion != version) throw Failure(409, "stale_trip_version", "Reload the trip before retrying."); }
    private static Exception Failure(int status, string code, string detail) => new SwiftRide.ServiceDefaults.ApiFailure(status, code, detail);
    private object Snapshot(TripEntity x) => new { x.Id, x.RiderId, x.RiderNameSnapshot,
        RiderPhoneSnapshot = x.DriverId == UserId() && x.Status is TripStatus.DriverAccepted or TripStatus.EnRoute or TripStatus.PickedUp
            ? x.RiderPhoneSnapshot : null,
        x.DriverId, x.DriverAvatarImageKeySnapshot, x.VehicleType, x.PickupAddress, x.PickupLatitude, x.PickupLongitude, x.DestinationAddress, x.DestinationLatitude, x.DestinationLongitude, status = x.Status.ToString(), x.StateVersion, x.AgreedAmount, x.Currency, x.PaymentId, x.PaymentStatus, x.FailureReason, x.CreatedAt, x.UpdatedAt, x.DriverAcceptedAt, x.PickedUpAt, x.DroppedOffAt, x.CompletedAt, proposals = x.Proposals.Select(p => new { p.Id, p.DriverId, p.QuoteId, p.Amount, p.Currency, p.DistanceMeters, p.EstimatedDurationSeconds, status = p.Status.ToString(), p.RiderApprovalDeadline, p.RiderRespondedAt, p.DriverResponseDeadline, p.DriverNameSnapshot, p.DriverAvatarImageKeySnapshot, p.PlateNumberSnapshot, p.VehicleNameSnapshot, p.VehicleImageKeySnapshot }) };
}

public sealed record CreateTripRequest(string PickupAddress, double PickupLatitude, double PickupLongitude, string DestinationAddress, double DestinationLatitude, double DestinationLongitude, string VehicleType = "Motorbike");
public sealed record VersionRequest(int StateVersion);
public sealed record DriverProposalRequest(Guid ProposalId, int StateVersion);
