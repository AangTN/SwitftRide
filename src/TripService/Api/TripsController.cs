using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Api;

[ApiController, Route("api/trips"), Authorize, TypeFilter(typeof(TripExceptionFilter))]
public sealed class TripsController(TripDbContext db, ITripRealtimePublisher realtime, ITripMatchingNotifier matching, ITripPaymentNotifier payment, TimeProvider clock, IOptions<TripWorkflowOptions> workflow) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateTripRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var riderId = UserId(); RequireRole("Rider");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw Failure(400, "idempotency_key_required", "Idempotency-Key is required.");
        var existing = await db.Trips.Include(x => x.Proposals).SingleOrDefaultAsync(x => x.RiderId == riderId && x.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return Ok(Snapshot(existing));
        var now = clock.GetUtcNow();
        var trip = new TripEntity
        {
            RiderId = riderId, IdempotencyKey = idempotencyKey, PickupAddress = request.PickupAddress,
            PickupLatitude = request.PickupLatitude, PickupLongitude = request.PickupLongitude,
            DestinationAddress = request.DestinationAddress, DestinationLatitude = request.DestinationLatitude,
            DestinationLongitude = request.DestinationLongitude, CreatedAt = now, UpdatedAt = now
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
            x.Status != TripStatus.Paid && x.Status != TripStatus.Cancelled && x.Status != TripStatus.Failed).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        return trip is null ? NoContent() : Ok(Snapshot(trip));
    }

    [HttpGet("{tripId:guid}")]
    public async Task<IActionResult> Get(Guid tripId, CancellationToken ct)
    {
        var trip = await LoadForUserOrDriverInvitation(tripId, ct);
        return Ok(Snapshot(trip));
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var user = UserId(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Trips.Where(x => x.RiderId == user || x.DriverId == user).OrderByDescending(x => x.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Include(x => x.Proposals).ToListAsync(ct);
        return Ok(new { page, pageSize, total, items = items.Select(Snapshot) });
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
        var release = trip.Proposals.Where(x => x.Status is ProposalStatus.AwaitingDriverResponse or ProposalStatus.AwaitingRiderApproval).ToList();
        TripStateMachine.Move(trip, TripStatus.Cancelled, UserId(), now, "cancelled_by_user");
        foreach (var proposal in trip.Proposals.Where(x => x.Status is ProposalStatus.AwaitingDriverResponse or ProposalStatus.AwaitingRiderApproval or ProposalStatus.Searching)) { proposal.Status = ProposalStatus.Cancelled; proposal.ResolvedAt = now; }
        foreach (var proposal in release) await matching.ReleaseAsync(proposal, "trip_cancelled", ct);
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
        else TripStateMachine.Move(trip, TripStatus.Cancelled, UserId(), now, "rider_rejected_quote");
        if (!approve) await matching.ReleaseAsync(proposal, "rider_rejected_quote", ct);
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
        if (accept) { trip.DriverId = UserId(); trip.QuoteId = proposal.QuoteId; trip.AgreedAmount = proposal.Amount; trip.Currency = proposal.Currency; trip.DriverNameSnapshot = proposal.DriverNameSnapshot; trip.PlateNumberSnapshot = proposal.PlateNumberSnapshot; trip.VehicleNameSnapshot = proposal.VehicleNameSnapshot; trip.VehicleImageKeySnapshot = proposal.VehicleImageKeySnapshot; trip.DriverAcceptedAt = now; TripStateMachine.Move(trip, TripStatus.DriverAccepted, UserId(), now); }
        else if (trip.Proposals.Count(x => x.DriverId is not null) >= workflow.Value.MaxDriverAttempts) TripStateMachine.Move(trip, TripStatus.Failed, UserId(), now, "driver_attempt_limit_reached");
        else { TripStateMachine.Move(trip, TripStatus.Matching, UserId(), now, "driver_rejected"); trip.Proposals.Add(new TripProposal { TripId = trip.Id, CreatedAt = now, Status = ProposalStatus.Searching }); }
        if (!accept) { await matching.ReleaseAsync(proposal, "driver_rejected", ct); await matching.RequestOpenProposalsAsync(trip, ct); }
        await db.SaveChangesAsync(ct);
        await realtime.PublishTripChangedAsync(trip, ct); return Ok(Snapshot(trip));
    }

    private async Task<IActionResult> DriverTransition(Guid tripId, VersionRequest request, TripStatus target, CancellationToken ct)
    {
        var trip = await DriverTrip(tripId, request.StateVersion, ct); var now = clock.GetUtcNow(); TripStateMachine.Move(trip, target, UserId(), now);
        if (target == TripStatus.PickedUp) trip.PickedUpAt = now;
        await db.SaveChangesAsync(ct); await realtime.PublishTripChangedAsync(trip, ct); return Ok(Snapshot(trip));
    }

    private async Task<TripEntity> RiderTrip(Guid id, int version, CancellationToken ct) { var trip = await LoadForUser(id, ct); if (trip.RiderId != UserId()) throw Failure(403, "trip_forbidden", "Only the rider can perform this action."); EnsureVersion(trip, version); return trip; }
    private async Task<TripEntity> DriverTrip(Guid id, int version, CancellationToken ct) { RequireRole("Driver"); var trip = await LoadForUser(id, ct); if (trip.DriverId != UserId()) throw Failure(403, "trip_forbidden", "Only the assigned driver can perform this action."); EnsureVersion(trip, version); return trip; }
    private async Task<TripEntity> LoadForUser(Guid id, CancellationToken ct) { var user = UserId(); return await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id && (x.RiderId == user || x.DriverId == user), ct) ?? throw Failure(404, "trip_not_found", "Trip was not found."); }
    private async Task<TripEntity> LoadForUserOrDriverInvitation(Guid id, CancellationToken ct) { var user = UserId(); return await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id && (x.RiderId == user || x.DriverId == user || x.Proposals.Any(p => p.DriverId == user && p.Status == ProposalStatus.AwaitingDriverResponse)), ct) ?? throw Failure(404, "trip_not_found", "Trip was not found."); }
    private Guid UserId() => Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : throw Failure(401, "invalid_subject", "Token subject is invalid.");
    private void RequireRole(string role) { if (!User.IsInRole(role)) throw Failure(403, "role_forbidden", $"The {role} role is required."); }
    private static void EnsureVersion(TripEntity trip, int version) { if (trip.StateVersion != version) throw Failure(409, "stale_trip_version", "Reload the trip before retrying."); }
    private static Exception Failure(int status, string code, string detail) => new SwiftRide.ServiceDefaults.ApiFailure(status, code, detail);
    private static object Snapshot(TripEntity x) => new { x.Id, x.RiderId, x.DriverId, status = x.Status.ToString(), x.StateVersion, x.AgreedAmount, x.Currency, x.PaymentId, x.PaymentStatus, x.FailureReason, x.CreatedAt, x.UpdatedAt, x.DriverAcceptedAt, x.PickedUpAt, x.DroppedOffAt, x.CompletedAt, proposals = x.Proposals.Select(p => new { p.Id, p.DriverId, p.QuoteId, p.Amount, p.Currency, status = p.Status.ToString(), p.RiderApprovalDeadline, p.RiderRespondedAt, p.DriverResponseDeadline, p.DriverNameSnapshot, p.PlateNumberSnapshot, p.VehicleNameSnapshot, p.VehicleImageKeySnapshot }) };
}

public sealed record CreateTripRequest(string PickupAddress, double PickupLatitude, double PickupLongitude, string DestinationAddress, double DestinationLatitude, double DestinationLongitude);
public sealed record VersionRequest(int StateVersion);
public sealed record DriverProposalRequest(Guid ProposalId, int StateVersion);
