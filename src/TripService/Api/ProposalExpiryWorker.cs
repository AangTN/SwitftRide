using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

/// <summary>Deadlines live in the database; this worker is deliberately stateless and recovers after a restart.</summary>
public sealed class ProposalExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ProposalExpiryWorker> logger, IOptions<TripWorkflowOptions> workflow) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ExpireAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Unable to expire trip proposals"); }
        }
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>(); var realtime = scope.ServiceProvider.GetRequiredService<ITripRealtimePublisher>(); var matching = scope.ServiceProvider.GetRequiredService<ITripMatchingNotifier>(); var now = clock.GetUtcNow();
        var trips = await db.Trips.Include(x => x.Proposals).Include(x => x.History)
            .Where(x => (x.Status == TripStatus.AwaitingDriverResponse && x.Proposals.Any(p => p.Status == ProposalStatus.AwaitingDriverResponse && p.DriverResponseDeadline <= now)) ||
                (x.Status == TripStatus.AwaitingRiderApproval && x.Proposals.Any(p => p.Status == ProposalStatus.AwaitingRiderApproval && p.RiderApprovalDeadline <= now))).ToListAsync(ct);
        var releases = new List<(TripProposal Proposal, string Reason)>();
        foreach (var trip in trips)
        {
            var driverTimedOut = trip.Proposals.Where(p => p.Status == ProposalStatus.AwaitingDriverResponse && p.DriverResponseDeadline <= now).ToList();
            if (driverTimedOut.Count != 0)
            {
                foreach (var proposal in driverTimedOut) { proposal.Status = ProposalStatus.DriverTimedOut; proposal.ResolvedAt = now; releases.Add((proposal, "driver_response_timeout")); }
                if (trip.Proposals.Count(p => p.DriverId is not null) >= workflow.Value.MaxDriverAttempts)
                    TripStateMachine.Move(trip, TripStatus.Failed, null, now, "driver_attempt_limit_reached");
                else
                {
                    TripStateMachine.Move(trip, TripStatus.Matching, null, now, "driver_response_timeout");
                    trip.Proposals.Add(new TripProposal { TripId = trip.Id, CreatedAt = now, Status = ProposalStatus.Searching });
                }
                continue;
            }
            foreach (var proposal in trip.Proposals.Where(p => p.Status == ProposalStatus.AwaitingRiderApproval && p.RiderApprovalDeadline <= now)) { proposal.Status = ProposalStatus.RiderTimedOut; proposal.ResolvedAt = now; releases.Add((proposal, "rider_approval_timeout")); }
            TripStateMachine.Move(trip, TripStatus.Cancelled, null, now, "rider_approval_timeout");
        }
        if (trips.Count == 0) return;
        await db.SaveChangesAsync(ct);
        foreach (var release in releases) await matching.ReleaseAsync(release.Proposal, release.Reason, ct);
        foreach (var trip in trips) { await matching.RequestOpenProposalsAsync(trip, ct); await realtime.PublishTripChangedAsync(trip, ct); }
    }
}
