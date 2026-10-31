using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using SwiftRide.Matching.Domain;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Infrastructure;

public sealed class MatchingDatabase : IDriverProfileReader
{
    static MatchingDatabase()
    {
        ConventionRegistry.Register("matching-camel-case", new ConventionPack { new CamelCaseElementNameConvention() },
            t => t.Namespace == "SwiftRide.Matching.Domain");
        BsonClassMap.RegisterClassMap<FareQuote>(m =>
        {
            m.AutoMap(); m.MapMember(x => x.TotalAmount).SetSerializer(new DecimalSerializer(BsonType.Decimal128));
        });
        BsonClassMap.RegisterClassMap<PricingSnapshot>(m =>
        {
            m.AutoMap();
            foreach (var member in m.DeclaredMemberMaps.Where(x => x.MemberType == typeof(decimal)))
                member.SetSerializer(new DecimalSerializer(BsonType.Decimal128));
        });
    }
    public IMongoDatabase Database { get; }
    public IMongoCollection<DriverAvailability> Drivers => Database.GetCollection<DriverAvailability>("driver_availability");
    public IMongoCollection<DriverProfile> Profiles => Database.GetCollection<DriverProfile>("driver_profiles");
    public async Task<DriverProfile?> FindAsync(string userId, CancellationToken ct) =>
        await Profiles.Find(x => x.Id == userId).SingleOrDefaultAsync(ct);
    public IMongoCollection<FareQuote> Quotes => Database.GetCollection<FareQuote>("fare_quotes");
    public MatchingDatabase(IMongoClient client, string name) => Database = client.GetDatabase(name);

    public async Task InitializeAsync(bool seed, CancellationToken ct = default)
    {
        await Profiles.Indexes.CreateOneAsync(new CreateIndexModel<DriverProfile>(
            Builders<DriverProfile>.IndexKeys.Ascending(x => x.PlateNumber), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Geo2DSphere(x => x.Location)), cancellationToken: ct);
        await Drivers.Indexes.CreateOneAsync(new CreateIndexModel<DriverAvailability>(
            Builders<DriverAvailability>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.LocationUpdatedAt)), cancellationToken: ct);
        await Quotes.Indexes.CreateOneAsync(new CreateIndexModel<FareQuote>(Builders<FareQuote>.IndexKeys.Ascending(x => x.TripId)), cancellationToken: ct);
        // Keep expired quotes for history; expiresAt is checked by future quote acceptance logic, not a TTL index.
        await Quotes.Indexes.CreateOneAsync(new CreateIndexModel<FareQuote>(Builders<FareQuote>.IndexKeys.Ascending(x => x.ExpiresAt)), cancellationToken: ct);
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
                Builders<DriverAvailability>.Update.SetOnInsert(x => x.Status, "Offline").SetOnInsert(x => x.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
        }
    }
}
