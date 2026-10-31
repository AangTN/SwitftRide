using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Npgsql;
using SwiftRide.Auth.Infrastructure;
using SwiftRide.Matching.Domain;
using SwiftRide.Matching.Infrastructure;

// Run with the service environment loaded. Never print connection strings or credentials.
var authConnection = Environment.GetEnvironmentVariable("ConnectionStrings__AuthDb") ?? throw new InvalidOperationException("Auth connection is required.");
var matchingConnection = Environment.GetEnvironmentVariable("ConnectionStrings__MatchingDb") ?? throw new InvalidOperationException("Matching connection is required.");
if (args.Contains("--verify"))
{
    await DriverActivationCheck.RunAsync(authConnection, matchingConnection);
    return;
}
await using var auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(authConnection).Options);
await auth.Database.MigrateAsync(); // Archives legacy profiles and freezes their original legal names before any Rider activation.
var matching = new MatchingDatabase(new MongoClient(matchingConnection), "MatchingDB");
await matching.InitializeAsync(seed: false);
await using var connection = new NpgsqlConnection(authConnection);
await connection.OpenAsync();
await using var command = new NpgsqlCommand("SELECT user_id, legal_name, plate_number, vehicle_name, vehicle_image_key, created_at, updated_at FROM legacy_driver_profiles", connection);
await using var reader = await command.ExecuteReaderAsync();
var count = 0;
while (await reader.ReadAsync())
{
    var profile = new DriverProfile {
        Id = reader.GetGuid(0).ToString(), LegalName = reader.GetString(1), PlateNumber = reader.GetString(2),
        VehicleName = reader.GetString(3), VehicleImageKey = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = reader.GetDateTime(5), UpdatedAt = reader.GetDateTime(6)
    };
    var existing = await matching.FindAsync(profile.Id, default);
    if (existing is null)
    {
        await matching.Profiles.InsertOneAsync(profile);
        existing = await matching.FindAsync(profile.Id, default);
    }
    if (existing is null || existing.LegalName != profile.LegalName || existing.PlateNumber != profile.PlateNumber ||
        existing.VehicleName != profile.VehicleName || existing.VehicleImageKey != profile.VehicleImageKey)
        throw new InvalidOperationException($"Profile {profile.Id} conflicts with the archived source; no data was overwritten. Resolve explicitly before retrying.");
    count++;
}
Console.WriteLine($"Verified {count} driver profiles in Matching. The PostgreSQL archive remains intact. Safe to rerun.");
