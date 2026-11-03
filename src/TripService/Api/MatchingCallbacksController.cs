using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftRide.Contracts.Trips;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

/// <summary>Private integration endpoint. It is deliberately not reachable with Rider or Driver roles.</summary>
[ApiController, Route("internal/trips"), Authorize(Roles = "Service"), TypeFilter(typeof(TripExceptionFilter))]
public sealed class MatchingCallbacksController(TripDbContext db, ITripRealtimePublisher realtime, ITripMatchingNotifier matching, TimeProvider clock, IOptions<TripWorkflowOptions> workflow) : ControllerBase
{
    [HttpPost("{tripId:guid}/proposals")]
    public async Task<IActionResult> Offer(Guid tripId, TripProposalCandidate candidate, CancellationToken ct)
        => await new TripMatchingResultHandler(db, realtime, matching, clock, workflow)
            .ApplyAsync(new TripMatchingResult(tripId, candidate.ProposalId, candidate), ct)
            ? Accepted() : Conflict(new { code = "proposal_closed" });
}

public sealed class TripMatchingResultHandler(TripDbContext db, ITripRealtimePublisher realtime, ITripMatchingNotifier matching,
    TimeProvider clock, IOptions<TripWorkflowOptions> workflow)
{
    public async Task<bool> ApplyAsync(TripMatchingResult result, CancellationToken ct)
    {
        try { return await ApplyCoreAsync(result, ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // A concurrent cancel/expiry won; retry against its committed state.
            throw;
        }
    }

    private async Task<bool> ApplyCoreAsync(TripMatchingResult result, CancellationToken ct)
    {
        var trip = await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == result.TripId, ct)
            ?? throw new SwiftRide.ServiceDefaults.ApiFailure(404, "trip_not_found", "Trip was not found.");
        var candidate = result.Candidate;
        var proposal = trip.Proposals.SingleOrDefault(x => x.Id == result.ProposalId)
            ?? throw new SwiftRide.ServiceDefaults.ApiFailure(409, "proposal_not_found", "Matching must supply a proposal id created by Trip.");
        if (trip.Status != TripStatus.Matching || proposal.Status != ProposalStatus.Searching)
        {
            if (candidate is null) return true;
            if (proposal.DriverId == candidate.DriverId &&
                proposal.Status is (ProposalStatus.AwaitingRiderApproval or ProposalStatus.AwaitingDriverResponse or ProposalStatus.DriverAccepted) &&
                trip.Status is not (TripStatus.Cancelled or TripStatus.Failed or TripStatus.PaymentPending or TripStatus.Paid))
                return true; // Duplicate delivery must not restart the rider's countdown.
            await matching.ReleaseAsync(new TripProposal { Id = proposal.Id, TripId = trip.Id, DriverId = candidate.DriverId }, "late_matching_result", ct);
            await db.SaveChangesAsync(ct);
            return false;
        }
        if (candidate is null)
        {
            proposal.Status = ProposalStatus.NoDriverFound;
            proposal.ResolvedAt = clock.GetUtcNow();
            TripStateMachine.Move(trip, TripStatus.Failed, null, clock.GetUtcNow(), result.FailureCode ?? "no_driver_available");
            await db.SaveChangesAsync(ct);
            await realtime.PublishTripChangedAsync(trip, ct);
            return true;
        }
        if (candidate.ProposalId != result.ProposalId || candidate.DriverId == Guid.Empty || candidate.Amount < 0 || candidate.Currency != "VND")
            throw new SwiftRide.ServiceDefaults.ApiFailure(400, "invalid_matching_result", "Invalid candidate result.");
        if (trip.Proposals.Any(x => x.DriverId == candidate.DriverId)) throw new SwiftRide.ServiceDefaults.ApiFailure(409, "driver_already_considered", "This driver was already considered for this trip.");
        if (trip.Proposals.Count(x => x.DriverId is not null) >= workflow.Value.MaxDriverAttempts)
            throw new SwiftRide.ServiceDefaults.ApiFailure(409, "driver_attempt_limit_reached", "The trip has exhausted its configured driver attempts.");
        var now = clock.GetUtcNow();
        proposal.DriverId = candidate.DriverId; proposal.QuoteId = candidate.QuoteId; proposal.Amount = candidate.Amount; proposal.Currency = candidate.Currency;
        proposal.DriverNameSnapshot = candidate.DriverName; proposal.PlateNumberSnapshot = candidate.PlateNumber; proposal.VehicleNameSnapshot = candidate.VehicleName; proposal.VehicleImageKeySnapshot = candidate.VehicleImageKey;
        proposal.DriverAvatarImageKeySnapshot = candidate.DriverAvatarImageKey;
        proposal.Status = ProposalStatus.AwaitingRiderApproval; proposal.RiderApprovalDeadline = now.AddSeconds(workflow.Value.RiderApprovalSeconds);
        TripStateMachine.Move(trip, TripStatus.AwaitingRiderApproval, null, now, "matching_candidate_offered");
        await db.SaveChangesAsync(ct); await realtime.PublishTripChangedAsync(trip, ct);
        return true;
    }
}
