using System.Text.Json;
using MongoDB.Driver;
using SwiftRide.Contracts.Trips;
using SwiftRide.Matching.Domain;

namespace SwiftRide.Matching.Infrastructure;

public sealed class MatchingJobStore(MatchingDatabase db)
{
    public IMongoCollection<MatchingJob> Jobs => db.Database.GetCollection<MatchingJob>("matching_jobs");
    private static readonly UpdateDefinitionBuilder<MatchingJob> U = Builders<MatchingJob>.Update;

    public async Task EnqueueAsync(TripMatchingRequested request, CancellationToken ct)
    {
        await EnsureAsync(request.TripId, request.ProposalId, ct);
        await Jobs.UpdateOneAsync(x => x.Id == request.ProposalId.ToString() && x.TripId == request.TripId.ToString() &&
            x.RequestJson == null && !x.Released,
            U.Set(x => x.RequestJson, JsonSerializer.Serialize(request)).Set(x => x.WorkPending, true)
                .Set(x => x.NextAttemptAt, DateTime.UtcNow).Inc(x => x.Revision, 1), cancellationToken: ct);
    }

    public async Task ControlAsync(Guid tripId, Guid proposalId, Guid driverId, bool release, CancellationToken ct)
    {
        await EnsureAsync(tripId, proposalId, ct);
        // Release is a terminal tombstone. A delayed confirm/find cannot reopen it.
        var filter = Builders<MatchingJob>.Filter.Where(x => x.Id == proposalId.ToString() &&
            x.TripId == tripId.ToString() && !x.Released);
        await Jobs.UpdateOneAsync(filter,
            U.Set(x => x.DriverId, driverId.ToString()).Set(x => x.Released, release)
                .Set(x => x.Confirmed, !release).Set(x => x.WorkPending, true)
                .Set(x => x.NextAttemptAt, DateTime.UtcNow).Inc(x => x.Revision, 1), cancellationToken: ct);
    }

    private async Task EnsureAsync(Guid tripId, Guid proposalId, CancellationToken ct)
    {
        try
        {
            await Jobs.UpdateOneAsync(x => x.Id == proposalId.ToString(),
                U.SetOnInsert(x => x.TripId, tripId.ToString())
                    .SetOnInsert(x => x.LeaseUntil, DateTime.UnixEpoch)
                    .SetOnInsert(x => x.Released, false).SetOnInsert(x => x.Confirmed, false)
                    .SetOnInsert(x => x.ResultSent, false).SetOnInsert(x => x.WorkPending, false),
                new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException e) when (e.WriteError?.Code == 11000) { /* Concurrent delivery inserted it. */ }
    }

    public Task<MatchingJob> ClaimAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return Jobs.FindOneAndUpdateAsync(x => x.WorkPending && x.NextAttemptAt <= now && x.LeaseUntil <= now,
            U.Set(x => x.LeaseId, Guid.NewGuid().ToString()).Set(x => x.LeaseUntil, now.AddMinutes(1)),
            new FindOneAndUpdateOptions<MatchingJob> { ReturnDocument = ReturnDocument.After, Sort = Builders<MatchingJob>.Sort.Ascending(x => x.NextAttemptAt) }, ct);
    }

    public Task SaveResultAsync(MatchingJob job, TripMatchingResult result, CancellationToken ct) =>
        Jobs.UpdateOneAsync(x => x.Id == job.Id && x.LeaseId == job.LeaseId && !x.Released && x.ResultJson == null,
            U.Set(x => x.ResultJson, JsonSerializer.Serialize(result)), cancellationToken: ct);

    public Task<MatchingJob> ReloadAsync(string id, CancellationToken ct) => Jobs.Find(x => x.Id == id).SingleAsync(ct);

    public Task CompleteAsync(MatchingJob job, bool sent, CancellationToken ct) =>
        Jobs.UpdateOneAsync(x => x.Id == job.Id && x.LeaseId == job.LeaseId && x.Revision == job.Revision,
            U.Set(x => x.WorkPending, false).Set(x => x.ResultSent, sent).Set(x => x.LastError, null), cancellationToken: ct);

    public Task RetryAsync(MatchingJob job, Exception error, CancellationToken ct) =>
        Jobs.UpdateOneAsync(x => x.Id == job.Id && x.LeaseId == job.LeaseId,
            U.Inc(x => x.Attempts, 1).Set(x => x.LastError, error.GetType().Name)
                .Set(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(5)), cancellationToken: ct);

    public Task UnlockAsync(MatchingJob job, CancellationToken ct) =>
        Jobs.UpdateOneAsync(x => x.Id == job.Id && x.LeaseId == job.LeaseId,
            U.Set(x => x.LeaseUntil, DateTime.UnixEpoch).Set(x => x.LeaseId, null), cancellationToken: ct);
}
