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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ExpireAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Unable to expire trip proposals"); }
        }
    }

    public async Task ExpireAsync(CancellationToken ct)
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
                TripProposalRetry.Advance(trip, workflow.Value.MaxDriverAttempts, null, now, "driver_response_timeout");
                continue;
            }
            foreach (var proposal in trip.Proposals.Where(p => p.Status == ProposalStatus.AwaitingRiderApproval && p.RiderApprovalDeadline <= now)) { proposal.Status = ProposalStatus.RiderTimedOut; proposal.ResolvedAt = now; releases.Add((proposal, "rider_approval_timeout")); }
            TripStateMachine.Move(trip, TripStatus.Cancelled, null, now, "rider_approval_timeout");
        }
        if (trips.Count == 0) return;
        foreach (var release in releases) await matching.ReleaseAsync(release.Proposal, release.Reason, ct);
        foreach (var trip in trips)
        {
            await matching.RequestOpenProposalsAsync(trip, ct);
            if (trip.Status is TripStatus.Failed or TripStatus.Cancelled) TripRouting.EnqueueCleanup(db, trip, now);
        }
        await db.SaveChangesAsync(ct);
        foreach (var trip in trips) await realtime.PublishTripChangedAsync(trip, ct);
    }
}
