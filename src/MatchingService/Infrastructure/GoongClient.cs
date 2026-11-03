using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwiftRide.Matching.Application;

namespace SwiftRide.Matching.Infrastructure;

public sealed class GoongClient(HttpClient client, IConfiguration configuration, ILogger<GoongClient> logger) : IGoongClient
{
    public async Task<RouteMetrics> GetRouteAsync(
        double originLatitude, double originLongitude,
        double destinationLatitude, double destinationLongitude,
        string vehicleType, CancellationToken ct)
    {
        var vehicle = vehicleType == "Motorbike" ? "bike" : "car";
        var uri = BuildUri("v2/direction", new Dictionary<string, string?>
        {
            ["origin"] = Coordinate(originLatitude, originLongitude),
            ["destination"] = Coordinate(destinationLatitude, destinationLongitude),
            ["vehicle"] = vehicle
        });
        using var document = await GetDocumentAsync(uri, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new MatchingFailureException(502, "goong_response_invalid", "The route provider returned an invalid response.");
        // Directions V2 can return routes without a top-level status. Validate
        // an explicit status if present, then require valid route/leg metrics.
        if (root.TryGetProperty("status", out _)) EnsureSuccessfulStatus(root);
        if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array ||
            routes.GetArrayLength() == 0 || routes[0].ValueKind != JsonValueKind.Object ||
            !routes[0].TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array ||
            legs.GetArrayLength() == 0 || legs[0].ValueKind != JsonValueKind.Object)
            throw new MatchingFailureException(502, "goong_route_unavailable", "The route provider returned no route.");
        var leg = legs[0];
        if (!leg.TryGetProperty("distance", out var distance) ||
            distance.ValueKind != JsonValueKind.Object ||
            !distance.TryGetProperty("value", out var distanceValue) || !distanceValue.TryGetDouble(out var meters) ||
            !leg.TryGetProperty("duration", out var duration) ||
            duration.ValueKind != JsonValueKind.Object ||
            !duration.TryGetProperty("value", out var durationValue) || !durationValue.TryGetInt32(out var seconds))
            throw new MatchingFailureException(502, "goong_route_invalid", "The route provider returned invalid route metrics.");
        return new RouteMetrics(meters, seconds);
    }

    public async Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct)
    {
        var uri = BuildAddressUri("v2/geocode", new Dictionary<string, string?> { ["address"] = address });
        using var document = await GetDocumentAsync(uri, ct);
        return ParseGeocodedAddress(document.RootElement);
    }

    public async Task<GeocodedAddress?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct)
    {
        var uri = BuildAddressUri("v2/geocode", new Dictionary<string, string?>
        {
            ["latlng"] = Coordinate(latitude, longitude)
        });
        using var document = await GetDocumentAsync(uri, ct);
        return ParseGeocodedAddress(document.RootElement);
    }

    public async Task<IReadOnlyList<PlaceSuggestion>> SearchPlacesAsync(string input, CancellationToken ct)
    {
        var uri = BuildAddressUri("v2/place/autocomplete", new Dictionary<string, string?> { ["input"] = input });
        using var document = await GetDocumentAsync(uri, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new MatchingFailureException(502, "goong_response_invalid", "The place provider returned an invalid response.");
        var status = GetStatus(root);
        if (status == "ZERO_RESULTS") return [];
        EnsureSuccessfulStatus(status);
        if (!root.TryGetProperty("predictions", out var predictions) || predictions.ValueKind != JsonValueKind.Array)
            throw new MatchingFailureException(502, "goong_places_invalid", "The place provider returned invalid results.");

        var suggestions = new List<PlaceSuggestion>(predictions.GetArrayLength());
        foreach (var prediction in predictions.EnumerateArray())
        {
            if (prediction.ValueKind != JsonValueKind.Object ||
                !prediction.TryGetProperty("description", out var description) ||
                description.ValueKind != JsonValueKind.String ||
                !prediction.TryGetProperty("place_id", out var placeId) ||
                placeId.ValueKind != JsonValueKind.String)
                throw new MatchingFailureException(502, "goong_places_invalid", "The place provider returned an invalid result.");
            suggestions.Add(new PlaceSuggestion(description.GetString()!, placeId.GetString()!));
        }
        return suggestions;
    }

    public async Task<PlaceDetails?> GetPlaceDetailsAsync(string placeId, CancellationToken ct)
    {
        var uri = BuildAddressUri("v2/place/detail", new Dictionary<string, string?> { ["place_id"] = placeId });
        using var document = await GetDocumentAsync(uri, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new MatchingFailureException(502, "goong_place_invalid", "The place provider returned invalid details.");
        var status = GetStatus(root);
        if (status is "ZERO_RESULTS" or "NOT_FOUND") return null;
        EnsureSuccessfulStatus(status);
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("place_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != placeId ||
            !result.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
            !result.TryGetProperty("formatted_address", out var address) || address.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(address.GetString()) ||
            !result.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object ||
            !location.TryGetProperty("lat", out var latitude) || latitude.ValueKind != JsonValueKind.Number || !latitude.TryGetDouble(out var lat) ||
            !location.TryGetProperty("lng", out var longitude) || longitude.ValueKind != JsonValueKind.Number || !longitude.TryGetDouble(out var lng) ||
            !double.IsFinite(lat) || !double.IsFinite(lng) || lat is < -90 or > 90 || lng is < -180 or > 180)
            throw new MatchingFailureException(502, "goong_place_invalid", "The selected place has invalid details or coordinates.");
        return new PlaceDetails(placeId, name.GetString()!, address.GetString()!, lat, lng);
    }

    private async Task<JsonDocument> GetDocumentAsync(string uri, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Goong returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw new MatchingFailureException(502, "goong_request_failed", "The address provider request failed.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Goong request failed: {ExceptionType}.", exception.GetType().Name);
            throw new MatchingFailureException(502, "goong_unavailable", "The address provider is temporarily unavailable.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MatchingFailureException(502, "goong_timeout", "The address provider request timed out.");
        }
        catch (JsonException)
        {
            throw new MatchingFailureException(502, "goong_response_invalid", "The address provider returned an invalid response.");
        }
    }

    private string BuildAddressUri(string endpoint, Dictionary<string, string?> parameters)
    {
        // Canonical V2 fields use current administrative units. Do not request
        // legacy descriptions or fall back to V1 when the provider is unavailable.
        parameters["has_deprecated_administrative_unit"] = "false";
        return BuildUri(endpoint, parameters);
    }

    private string BuildUri(string endpoint, Dictionary<string, string?> parameters)
    {
        var key = configuration["Goong:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) key = configuration["GOONG_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            throw new MatchingFailureException(503, "goong_not_configured", "Address and routing lookup is not configured.");
        parameters["api_key"] = key;
        return QueryHelpers.AddQueryString(endpoint, parameters);
    }

    private static GeocodedAddress? ParseGeocodedAddress(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MatchingFailureException(502, "goong_response_invalid", "The address provider returned an invalid response.");
        var status = GetStatus(root);
        if (status == "ZERO_RESULTS") return null;
        EnsureSuccessfulStatus(status);
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new MatchingFailureException(502, "goong_geocode_invalid", "The address provider returned invalid results.");
        if (results.GetArrayLength() == 0) return null;
        var result = results[0];
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("formatted_address", out var formattedAddress) ||
            formattedAddress.ValueKind != JsonValueKind.String ||
            !result.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("location", out var location) ||
            location.ValueKind != JsonValueKind.Object ||
            !location.TryGetProperty("lat", out var latitude) || !latitude.TryGetDouble(out var lat) ||
            !location.TryGetProperty("lng", out var longitude) || !longitude.TryGetDouble(out var lng))
            throw new MatchingFailureException(502, "goong_geocode_invalid", "The address provider returned an invalid result.");
        MatchingCoordinator.ValidateCoordinate(lat, lng);
        var placeId = result.TryGetProperty("place_id", out var id) ? id.GetString() : null;
        return new GeocodedAddress(formattedAddress.GetString()!, lat, lng, placeId);
    }

    private static string Coordinate(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude},{longitude}");

    private static string GetStatus(JsonElement root) =>
        root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? ""
            : "";

    private static void EnsureSuccessfulStatus(JsonElement root) => EnsureSuccessfulStatus(GetStatus(root));

    private static void EnsureSuccessfulStatus(string status)
    {
        if (status != "OK")
            throw new MatchingFailureException(502, "goong_request_failed", "The address provider could not complete the request.");
    }
}
