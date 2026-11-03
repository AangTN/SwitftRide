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
    {
        var trip = await db.Trips.Include(x => x.Proposals).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == tripId, ct)
            ?? throw new SwiftRide.ServiceDefaults.ApiFailure(404, "trip_not_found", "Trip was not found.");
        var proposal = trip.Proposals.SingleOrDefault(x => x.Id == candidate.ProposalId)
            ?? throw new SwiftRide.ServiceDefaults.ApiFailure(409, "proposal_not_found", "Matching must supply a proposal id created by Trip.");
        if (trip.Status != TripStatus.Matching || proposal.Status != ProposalStatus.Searching)
        {
            if (proposal.DriverId == candidate.DriverId && proposal.Status is ProposalStatus.AwaitingRiderApproval or ProposalStatus.AwaitingDriverResponse or ProposalStatus.DriverAccepted)
                return Accepted(); // Duplicate callback for a still-active reservation.
            await matching.ReleaseAsync(new TripProposal { Id = proposal.Id, TripId = trip.Id, DriverId = candidate.DriverId }, "late_matching_result", ct);
            await db.SaveChangesAsync(ct);
            return Conflict(new { code = "proposal_closed", detail = "Proposal is closed; release of the late reservation has been queued." });
        }
        if (trip.Proposals.Any(x => x.DriverId == candidate.DriverId)) throw new SwiftRide.ServiceDefaults.ApiFailure(409, "driver_already_considered", "This driver was already considered for this trip.");
        if (trip.Proposals.Count(x => x.DriverId is not null) >= workflow.Value.MaxDriverAttempts)
            throw new SwiftRide.ServiceDefaults.ApiFailure(409, "driver_attempt_limit_reached", "The trip has exhausted its configured driver attempts.");
        var now = clock.GetUtcNow();
        proposal.DriverId = candidate.DriverId; proposal.QuoteId = candidate.QuoteId; proposal.Amount = candidate.Amount; proposal.Currency = candidate.Currency;
        proposal.DriverNameSnapshot = candidate.DriverName; proposal.PlateNumberSnapshot = candidate.PlateNumber; proposal.VehicleNameSnapshot = candidate.VehicleName; proposal.VehicleImageKeySnapshot = candidate.VehicleImageKey;
        proposal.Status = ProposalStatus.AwaitingRiderApproval; proposal.RiderApprovalDeadline = now.AddSeconds(workflow.Value.RiderApprovalSeconds);
        TripStateMachine.Move(trip, TripStatus.AwaitingRiderApproval, null, now, "matching_candidate_offered");
        await db.SaveChangesAsync(ct); await realtime.PublishTripChangedAsync(trip, ct);
        return Accepted();
    }
}
