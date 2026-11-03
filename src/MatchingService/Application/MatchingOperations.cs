using SwiftRide.Matching.Domain;

namespace SwiftRide.Matching.Application;

public sealed class MatchingFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record DriverLocationUpdate(bool Online, double? Latitude, double? Longitude);
public sealed record DriverGpsRequest(double Latitude, double Longitude);

public sealed record DriverMatchRequest(
    Guid TripId,
    Guid ProposalId,
    double PickupLatitude,
    double PickupLongitude,
    string VehicleType = "Motorbike",
    IReadOnlyList<Guid>? ExcludedDriverIds = null);

public sealed record FareQuoteRequest(
    Guid TripId,
    Guid ProposalId,
    double PickupLatitude,
    double PickupLongitude,
    double DestinationLatitude,
    double DestinationLongitude,
    string VehicleType = "Motorbike");

public sealed record RouteMetrics(double DistanceMeters, int DurationSeconds);

public sealed record GeocodedAddress(string FormattedAddress, double Latitude, double Longitude, string? PlaceId);

public sealed record PlaceSuggestion(string Description, string PlaceId);

public sealed record MatchedDriver(
    string DriverId,
    string LegalName,
    string PlateNumber,
    string VehicleName,
    string VehicleType,
    string? VehicleImageKey,
    double DistanceMeters,
    string Status,
    DateTime? ReservedUntil,
    string? AvatarImageKey = null);

public sealed class MatchingOptions
{
    public double MaxSearchDistanceMeters { get; set; } = 10_000;
    public int MaxCandidates { get; set; } = 50;
    public int LocationMaxAgeSeconds { get; set; } = 120;
    public int ReservationSeconds { get; set; } = 120;
    public int QuoteValiditySeconds { get; set; } = 300;
}

public sealed class PricingOptions
{
    public decimal PerKilometer { get; set; } = 5000;
    public decimal PerMinute { get; set; } = 500;
    public decimal Surge { get; set; } = 1;
    public decimal Toll { get; set; }
    public decimal PromotionFactor { get; set; } = 1;
    public decimal TaxRate { get; set; }
}

public interface IMatchingStore
{
    Task<DriverAvailability?> UpdateGpsAsync(string driverId, DriverGpsRequest gps, DateTime now, CancellationToken ct);
    Task<bool> UpdateAvailabilityAsync(
        string driverId, string vehicleType, DriverLocationUpdate update, DateTime now, CancellationToken ct);
    Task<IReadOnlyList<DriverAvailability>> FindNearbyAvailableDriversAsync(
        GeoPoint pickup, string vehicleType, DateTime locationUpdatedAfter, double maxDistanceMeters,
        int maxCandidates, DateTime now, IReadOnlyCollection<string> excludedDriverIds, CancellationToken ct);
    Task<DriverAvailability?> FindActiveReservationAsync(string proposalId, DateTime now, CancellationToken ct);
    Task<DriverAvailability?> FindActiveAssignmentForTripAsync(string tripId, DateTime now, CancellationToken ct);
    Task<bool> TryReserveDriverAsync(
        string driverId, string vehicleType, GeoPoint expectedLocation, string tripId, string proposalId,
        DateTime locationUpdatedAfter,
        DateTime now, DateTime reservedUntil, CancellationToken ct);
    Task<bool> ConfirmReservationAsync(
        string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct);
    Task<bool> ReleaseReservationAsync(
        string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct);
    Task<FareQuote> SaveQuoteAsync(FareQuote quote, CancellationToken ct);
}

public interface IGoongClient
{
    Task<RouteMetrics> GetRouteAsync(
        double originLatitude, double originLongitude,
        double destinationLatitude, double destinationLongitude,
        string vehicleType, CancellationToken ct);
    Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct);
    Task<GeocodedAddress?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct);
    Task<IReadOnlyList<PlaceSuggestion>> SearchPlacesAsync(string input, CancellationToken ct);
}

public sealed class MatchingCoordinator(
    IMatchingStore store,
    IDriverProfileReader profiles,
    IGoongClient goong,
    FareCalculator fareCalculator,
    MatchingOptions matchingOptions,
    PricingOptions pricingOptions)
{
    private readonly MatchingOptions matching = matchingOptions;
    private readonly PricingOptions pricing = pricingOptions;

    public async Task<bool> UpdateAvailabilityAsync(
        string driverId, DriverLocationUpdate update, CancellationToken ct)
    {
        var profile = await profiles.FindAsync(driverId, ct);
        if (profile is null) return false;
        var vehicleType = NormalizeVehicleType(profile.VehicleType);
        return await store.UpdateAvailabilityAsync(driverId, vehicleType, update, DateTime.UtcNow, ct);
    }

    public async Task<MatchedDriver?> FindAndReserveNearestAsync(DriverMatchRequest request, CancellationToken ct)
    {
        if (request.TripId == Guid.Empty || request.ProposalId == Guid.Empty)
            throw new MatchingFailureException(400, "invalid_proposal", "TripId and ProposalId must be non-empty UUIDs.");
        ValidateCoordinate(request.PickupLatitude, request.PickupLongitude);
        var excludedDriverIds = NormalizeExcludedDriverIds(request.ExcludedDriverIds);
        var vehicleType = NormalizeVehicleType(request.VehicleType);
        var now = DateTime.UtcNow;
        var existing = await store.FindActiveReservationAsync(request.ProposalId.ToString(), now, ct);
        if (existing is not null)
            return await ReturnExistingAssignmentAsync(existing, request, vehicleType, excludedDriverIds, ct);

        var tripAssignment = await store.FindActiveAssignmentForTripAsync(request.TripId.ToString(), now, ct);
        if (tripAssignment is not null)
            throw new MatchingFailureException(409, "trip_already_assigned",
                "This trip already has an active driver assignment.");

        var pickup = ToGeoPoint(request.PickupLatitude, request.PickupLongitude);
        var candidates = await store.FindNearbyAvailableDriversAsync(
            pickup, vehicleType, now.AddSeconds(-matching.LocationMaxAgeSeconds),
            matching.MaxSearchDistanceMeters, matching.MaxCandidates, now, excludedDriverIds, ct);
        foreach (var candidate in candidates)
        {
            var candidateLocation = candidate.Location;
            if (candidateLocation is null || candidateLocation.Coordinates.Length != 2)
                throw new MatchingFailureException(409, "driver_location_invalid",
                    "A nearby driver's location is invalid.");
            var reservedUntil = now.AddSeconds(matching.ReservationSeconds);
            if (await store.TryReserveDriverAsync(candidate.Id, vehicleType, candidateLocation,
                request.TripId.ToString(),
                request.ProposalId.ToString(), now.AddSeconds(-matching.LocationMaxAgeSeconds),
                now, reservedUntil, ct))
            {
                candidate.Status = "Reserved";
                candidate.CurrentTripId = request.TripId.ToString();
                candidate.CurrentProposalId = request.ProposalId.ToString();
                candidate.ReservedUntil = reservedUntil;
                return await ToMatchedDriverAsync(candidate, request.PickupLatitude, request.PickupLongitude, ct);
            }

            existing = await store.FindActiveReservationAsync(request.ProposalId.ToString(), now, ct);
            if (existing is not null)
                return await ReturnExistingAssignmentAsync(existing, request, vehicleType, excludedDriverIds, ct);
            tripAssignment = await store.FindActiveAssignmentForTripAsync(request.TripId.ToString(), now, ct);
            if (tripAssignment is not null)
                throw new MatchingFailureException(409, "trip_already_assigned",
                    "This trip already has an active driver assignment.");
        }
        return null;
    }

    public Task<bool> ConfirmReservationAsync(
        string driverId, Guid tripId, Guid proposalId, CancellationToken ct) =>
        store.ConfirmReservationAsync(driverId, tripId.ToString(), proposalId.ToString(), DateTime.UtcNow, ct);

    public Task<bool> ReleaseReservationAsync(
        string driverId, Guid tripId, Guid proposalId, CancellationToken ct) =>
        store.ReleaseReservationAsync(driverId, tripId.ToString(), proposalId.ToString(), DateTime.UtcNow, ct);

    public async Task<FareQuote> CreateFareQuoteAsync(FareQuoteRequest request, CancellationToken ct)
    {
        if (request.TripId == Guid.Empty || request.ProposalId == Guid.Empty)
            throw new MatchingFailureException(400, "invalid_proposal", "TripId and ProposalId must be non-empty UUIDs.");
        ValidateCoordinate(request.PickupLatitude, request.PickupLongitude);
        ValidateCoordinate(request.DestinationLatitude, request.DestinationLongitude);
        var vehicleType = NormalizeVehicleType(request.VehicleType);
        var route = await goong.GetRouteAsync(request.PickupLatitude, request.PickupLongitude,
            request.DestinationLatitude, request.DestinationLongitude, vehicleType, ct);
        var total = fareCalculator.Calculate(route, pricing);
        var now = DateTime.UtcNow;
        var quote = new FareQuote
        {
            TripId = request.TripId.ToString(),
            ProposalId = request.ProposalId.ToString(),
            VehicleType = vehicleType,
            DistanceMeters = route.DistanceMeters,
            EstimatedDurationSeconds = route.DurationSeconds,
            PricingSnapshot = new PricingSnapshot
            {
                PerKilometer = pricing.PerKilometer,
                PerMinute = pricing.PerMinute,
                Surge = pricing.Surge,
                Toll = pricing.Toll,
                PromotionFactor = pricing.PromotionFactor,
                TaxRate = pricing.TaxRate
            },
            TotalAmount = total,
            ExpiresAt = now.AddSeconds(matching.QuoteValiditySeconds),
            CreatedAt = now
        };
        var savedQuote = await store.SaveQuoteAsync(quote, ct);
        if (savedQuote.TripId != quote.TripId || savedQuote.VehicleType != quote.VehicleType)
            throw new MatchingFailureException(409, "proposal_quote_conflict",
                "This proposal already has a quote for a different trip or vehicle type.");
        return savedQuote;
    }

    public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct) =>
        goong.GeocodeAsync(address.Trim(), ct);

    public Task<GeocodedAddress?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct)
    {
        ValidateCoordinate(latitude, longitude);
        return goong.ReverseGeocodeAsync(latitude, longitude, ct);
    }

    public Task<IReadOnlyList<PlaceSuggestion>> SearchPlacesAsync(string input, CancellationToken ct) =>
        goong.SearchPlacesAsync(input.Trim(), ct);

    private async Task<MatchedDriver> ReturnExistingAssignmentAsync(
        DriverAvailability existing, DriverMatchRequest request, string vehicleType,
        IReadOnlyCollection<string> excludedDriverIds, CancellationToken ct)
    {
        if (existing.CurrentTripId != request.TripId.ToString() || existing.VehicleType != vehicleType ||
            excludedDriverIds.Contains(existing.Id, StringComparer.Ordinal))
            throw new MatchingFailureException(409, "proposal_reservation_conflict",
                "This proposal's active reservation does not match the requested trip, vehicle type, or exclusion list.");
        var match = await ToMatchedDriverAsync(existing, request.PickupLatitude, request.PickupLongitude, ct);
        if (match.DistanceMeters > matching.MaxSearchDistanceMeters)
            throw new MatchingFailureException(409, "proposal_reservation_conflict",
                "This proposal already has an active reservation outside the requested search area.");
        return match;
    }

    private static IReadOnlyCollection<string> NormalizeExcludedDriverIds(IReadOnlyList<Guid>? driverIds)
    {
        if (driverIds is null) return [];
        if (driverIds.Count > 100 || driverIds.Contains(Guid.Empty))
            throw new MatchingFailureException(400, "invalid_excluded_drivers",
                "At most 100 non-empty driver IDs may be excluded.");
        return driverIds.Select(id => id.ToString()).Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<MatchedDriver> ToMatchedDriverAsync(
        DriverAvailability availability, double pickupLatitude, double pickupLongitude, CancellationToken ct)
    {
        var profile = await profiles.FindAsync(availability.Id, ct);
        if (profile is null)
            throw new MatchingFailureException(409, "driver_profile_missing",
                "The selected driver profile is unavailable.");
        var location = availability.Location;
        if (location is null || location.Coordinates.Length != 2)
            throw new MatchingFailureException(409, "driver_location_missing",
                "The selected driver's location is unavailable.");
        return new MatchedDriver(profile.Id, profile.LegalName, profile.PlateNumber, profile.VehicleName,
            profile.VehicleType, profile.VehicleImageKey,
            DistanceMeters(pickupLatitude, pickupLongitude, location.Coordinates[1], location.Coordinates[0]),
            availability.Status, availability.ReservedUntil, profile.AvatarImageKey);
    }

    public static string NormalizeVehicleType(string? vehicleType)
    {
        var normalized = vehicleType?.Trim();
        if (normalized is "Motorbike" or "Car4Seats" or "Car7Seats") return normalized;
        throw new MatchingFailureException(400, "invalid_vehicle_type",
            "Vehicle type must be Motorbike, Car4Seats, or Car7Seats.");
    }

    public static void ValidateCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new MatchingFailureException(400, "invalid_coordinates",
                "Latitude or longitude is outside the valid range.");
    }

    private static GeoPoint ToGeoPoint(double latitude, double longitude) => new()
    {
        Coordinates = [longitude, latitude]
    };

    private static double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusMeters = 6_371_000;
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(DegreesToRadians(latitude1)) * Math.Cos(DegreesToRadians(latitude2)) *
            Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}

public sealed class FareCalculator(
    IStandardFareStrategy standard,
    ISurgeFareStrategy surge,
    IPromotionFareStrategy promotion)
{
    public decimal Calculate(RouteMetrics route, PricingOptions pricing)
    {
        if (!double.IsFinite(route.DistanceMeters) || route.DistanceMeters < 0 || route.DurationSeconds < 0)
            throw new MatchingFailureException(502, "invalid_route_metrics",
                "The route provider returned invalid distance or duration.");
        if (pricing.PerKilometer < 0 || pricing.PerMinute < 0 || pricing.Toll < 0 ||
            pricing.Surge <= 0 || pricing.PromotionFactor <= 0 || pricing.TaxRate is < 0 or > 1)
            throw new InvalidOperationException("Pricing configuration contains an invalid rate or factor.");

        var standardFare = standard.Calculate(route, pricing);
        var surgedFare = surge.Apply(standardFare, pricing);
        var promotedFare = promotion.Apply(surgedFare, pricing);
        return decimal.Round(promotedFare, 0, MidpointRounding.AwayFromZero);
    }
}

public interface IStandardFareStrategy
{
    decimal Calculate(RouteMetrics route, PricingOptions pricing);
}

public interface ISurgeFareStrategy
{
    decimal Apply(decimal standardFare, PricingOptions pricing);
}

public interface IPromotionFareStrategy
{
    decimal Apply(decimal surgedFare, PricingOptions pricing);
}

public sealed class StandardFareStrategy : IStandardFareStrategy
{
    public decimal Calculate(RouteMetrics route, PricingOptions pricing)
    {
        var distanceKm = (decimal)route.DistanceMeters / 1000;
        var durationMinutes = (decimal)route.DurationSeconds / 60;
        return distanceKm * pricing.PerKilometer + durationMinutes * pricing.PerMinute;
    }
}

public sealed class SurgeFareStrategy : ISurgeFareStrategy
{
    public decimal Apply(decimal standardFare, PricingOptions pricing) =>
        standardFare * pricing.Surge + pricing.Toll;
}

public sealed class PromotionFareStrategy : IPromotionFareStrategy
{
    public decimal Apply(decimal surgedFare, PricingOptions pricing) =>
        surgedFare * pricing.PromotionFactor * (1 + pricing.TaxRate);
}
