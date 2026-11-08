using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using SwiftRide.Matching.Domain;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Infrastructure;

public sealed class MatchingDatabase : IDriverProfileReader, IMatchingStore
{
    static MatchingDatabase()
    {
        ConventionRegistry.Register("matching-camel-case", new ConventionPack { new CamelCaseElementNameConvention() },
            t => t.Namespace == "SwiftRide.Matching.Domain");
    }
    public IMongoDatabase Database { get; }
    public IMongoCollection<DriverAvailability> Drivers => Database.GetCollection<DriverAvailability>("driver_availability");
    public IMongoCollection<DriverProfile> Profiles => Database.GetCollection<DriverProfile>("driver_profiles");
    public async Task<DriverProfile?> FindAsync(string userId, CancellationToken ct) =>
        await Profiles.Find(x => x.Id == userId).SingleOrDefaultAsync(ct);
    public MatchingDatabase(IMongoClient client, string name) => Database = client.GetDatabase(name);

    public async Task<DriverAvailability?> GetDriverAsync(string driverId, CancellationToken ct) =>
        await Drivers.Find(x => x.Id == driverId).FirstOrDefaultAsync(ct);

    public Task<DriverAvailability?> UpdateGpsAsync(string driverId, DriverGpsRequest gps, DateTime now, CancellationToken ct) =>
        Drivers.FindOneAndUpdateAsync<DriverAvailability>(x => x.Id == driverId,
            Builders<DriverAvailability>.Update
                .Set(x => x.Location, new GeoPoint { Coordinates = [gps.Longitude, gps.Latitude] })
                .Set(x => x.LocationUpdatedAt, now).Set(x => x.UpdatedAt, now),
            new FindOneAndUpdateOptions<DriverAvailability> { ReturnDocument = ReturnDocument.After }, ct)!;

    public async Task<bool> UpdateAvailabilityAsync(
        string driverId, string vehicleType, DriverLocationUpdate update, DateTime now, CancellationToken ct)
    {
        var filter = Builders<DriverAvailability>.Filter.And(
            Builders<DriverAvailability>.Filter.Eq(x => x.Id, driverId),
            Builders<DriverAvailability>.Filter.In(x => x.Status, ["Offline", "Available"]));
        var change = Builders<DriverAvailability>.Update
            .Set(x => x.Status, update.Online ? "Available" : "Offline")
            .Set(x => x.VehicleType, vehicleType)
            .Set(x => x.UpdatedAt, now);
        if (update.Latitude is not null && update.Longitude is not null)
            change = change
                .Set(x => x.Location, new GeoPoint { Coordinates = [update.Longitude.Value, update.Latitude.Value] })
                .Set(x => x.LocationUpdatedAt, now);
        if (!update.Online)
            change = change
                .Set(x => x.CurrentTripId, null)
                .Set(x => x.CurrentProposalId, null)
            .Unset(x => x.ActiveRouteJson).Unset(x => x.RouteStageVersion)
                .Set(x => x.ReservedUntil, null);
        try
        {
            var result = await Drivers.UpdateOneAsync(filter, change, new UpdateOptions { IsUpsert = true }, ct);
            return result.MatchedCount == 1 || result.UpsertedId is not null;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<DriverAvailability>> FindNearbyAvailableDriversAsync(
        GeoPoint pickup, string vehicleType, DateTime locationUpdatedAfter, double maxDistanceMeters,
        int maxCandidates, DateTime now, IReadOnlyCollection<string> excludedDriverIds, CancellationToken ct)
    {
        // Trip owns expiry. Do not unlock a reservation based on a local clock:
        // a confirmation or result can be delayed in RabbitMQ.
        var filter = new BsonDocument
        {
            { "status", "Available" },
            { "vehicleType", vehicleType },
            { "locationUpdatedAt", new BsonDocument("$gte", locationUpdatedAfter) },
            {
                "location", new BsonDocument("$near", new BsonDocument
                {
                    {
                        "$geometry", new BsonDocument
                        {
                            { "type", "Point" },
                            { "coordinates", new BsonArray { pickup.Coordinates[0], pickup.Coordinates[1] } }
                        }
                    },
                    { "$maxDistance", maxDistanceMeters }
                })
            }
        };
        if (excludedDriverIds.Count > 0)
            filter.Add("_id", new BsonDocument("$nin", new BsonArray(excludedDriverIds)));
        return await Drivers.Find(new BsonDocumentFilterDefinition<DriverAvailability>(filter))
            .Limit(maxCandidates).ToListAsync(ct);
    }

    public async Task<DriverAvailability?> FindActiveReservationAsync(string proposalId, DateTime now, CancellationToken ct) =>
        await Drivers.Find(x => x.CurrentProposalId == proposalId &&
            (x.Status == "Busy" || x.Status == "Reserved"))
            .SingleOrDefaultAsync(ct);

    public async Task<DriverAvailability?> FindActiveAssignmentForTripAsync(
        string tripId, DateTime now, CancellationToken ct) =>
        await Drivers.Find(x => x.CurrentTripId == tripId &&
            (x.Status == "Busy" || x.Status == "Reserved"))
            .SingleOrDefaultAsync(ct);

    public async Task<bool> TryReserveDriverAsync(
        string driverId, string vehicleType, GeoPoint expectedLocation, string tripId, string proposalId,
        DateTime locationUpdatedAfter,
        DateTime now, DateTime reservedUntil, CancellationToken ct)
    {
        var filter = Builders<DriverAvailability>.Filter.And(
            Builders<DriverAvailability>.Filter.Eq(x => x.Id, driverId),
            Builders<DriverAvailability>.Filter.Eq(x => x.Status, "Available"),
            Builders<DriverAvailability>.Filter.Eq(x => x.VehicleType, vehicleType),
            Builders<DriverAvailability>.Filter.Eq(x => x.Location, expectedLocation),
            Builders<DriverAvailability>.Filter.Gte(x => x.LocationUpdatedAt, locationUpdatedAfter));
        var update = Builders<DriverAvailability>.Update
            .Set(x => x.Status, "Reserved")
            .Set(x => x.CurrentTripId, tripId)
            .Set(x => x.CurrentProposalId, proposalId)
            .Set(x => x.ReservedUntil, reservedUntil)
            .Set(x => x.UpdatedAt, now);
        try
        {
            var result = await Drivers.UpdateOneAsync(filter, update, cancellationToken: ct);
            return result.ModifiedCount == 1;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            return false;
        }
    }

    public async Task<bool> ConfirmReservationAsync(
        string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct)
    {
        var filter = Builders<DriverAvailability>.Filter.And(
            Builders<DriverAvailability>.Filter.Eq(x => x.Id, driverId),
            Builders<DriverAvailability>.Filter.Eq(x => x.Status, "Reserved"),
            Builders<DriverAvailability>.Filter.Eq(x => x.CurrentTripId, tripId),
            Builders<DriverAvailability>.Filter.Eq(x => x.CurrentProposalId, proposalId));
        var update = Builders<DriverAvailability>.Update
            .Set(x => x.Status, "Busy")
            .Set(x => x.ReservedUntil, null)
            .Set(x => x.UpdatedAt, now);
        var result = await Drivers.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.ModifiedCount == 1) return true;
        return await Drivers.Find(x => x.Id == driverId && x.Status == "Busy" &&
            x.CurrentTripId == tripId && x.CurrentProposalId == proposalId).AnyAsync(ct);
    }

    public async Task<bool> ReleaseReservationAsync(
        string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct)
    {
        var filter = Builders<DriverAvailability>.Filter.And(
            Builders<DriverAvailability>.Filter.Eq(x => x.Id, driverId),
            Builders<DriverAvailability>.Filter.In(x => x.Status, ["Reserved", "Busy"]),
            Builders<DriverAvailability>.Filter.Eq(x => x.CurrentTripId, tripId),
            Builders<DriverAvailability>.Filter.Eq(x => x.CurrentProposalId, proposalId));
        var update = Builders<DriverAvailability>.Update
            .Set(x => x.Status, "Available")
            .Set(x => x.CurrentTripId, null)
            .Set(x => x.CurrentProposalId, null)
                .Unset(x => x.ActiveRouteJson).Unset(x => x.RouteStageVersion)
            .Set(x => x.ReservedUntil, null)
            .Set(x => x.UpdatedAt, now);
        var result = await Drivers.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task InitializeAsync(bool seed, CancellationToken ct = default)
    {
        // Idempotent Mongo schema upgrade: old profiles have no avatar. Never overwrite a saved image key.
        await Profiles.UpdateManyAsync(Builders<DriverProfile>.Filter.Exists(x => x.AvatarImageKey, false),
            Builders<DriverProfile>.Update.Set(x => x.AvatarImageKey, (string?)null), cancellationToken: ct);
        await Database.GetCollection<MatchingJob>("matching_jobs").Indexes.CreateOneAsync(new CreateIndexModel<MatchingJob>(
            Builders<MatchingJob>.IndexKeys.Ascending(x => x.WorkPending).Ascending(x => x.NextAttemptAt).Ascending(x => x.LeaseUntil)), cancellationToken: ct);
        await Profiles.Indexes.CreateOneAsync(new CreateIndexModel<DriverProfile>(
            Builders<DriverProfile>.IndexKeys.Ascending(x => x.PlateNumber), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Geo2DSphere(x => x.Location)), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Ascending(x => x.VehicleType).Ascending(x => x.Status)
                .Ascending(x => x.LocationUpdatedAt)), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Ascending(x => x.CurrentProposalId),
            new CreateIndexOptions<DriverAvailability>
            {
                Unique = true,
                PartialFilterExpression = new BsonDocument("currentProposalId",
                    new BsonDocument("$type", "string"))
            }), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Ascending(x => x.CurrentTripId),
            new CreateIndexOptions<DriverAvailability>
            {
                Unique = true,
                PartialFilterExpression = new BsonDocument("currentTripId", new BsonDocument("$type", "string"))
            }), cancellationToken: ct);
        if (seed)
        {
            // Never overwrite migrated/verified identity or vehicle information on restart.
            const string driverId = "22222222-2222-2222-2222-222222222222";
            await Profiles.UpdateOneAsync(x => x.Id == driverId,
                Builders<DriverProfile>.Update.SetOnInsert(x => x.LegalName, "Driver Demo")
                    .SetOnInsert(x => x.PlateNumber, "59A1-12345").SetOnInsert(x => x.VehicleName, "Honda Wave Alpha")
                    .SetOnInsert(x => x.VehicleType, "Motorbike").SetOnInsert(x => x.VehicleImageKey, "seed/vehicle.svg")
                    .SetOnInsert(x => x.CreatedAt, DateTime.UtcNow).SetOnInsert(x => x.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
            await Drivers.UpdateOneAsync(x => x.Id == "22222222-2222-2222-2222-222222222222",
                Builders<DriverAvailability>.Update.SetOnInsert(x => x.Status, "Offline")
                    .SetOnInsert(x => x.VehicleType, "Motorbike").SetOnInsert(x => x.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
        }
    }

}
