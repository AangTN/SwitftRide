using System.Net.Http.Headers;
using SwiftRide.ServiceDefaults;

namespace SwiftRide.Trip.Api;

public sealed record RiderProfile(Guid Id, string FullName, string PhoneNumber);

public sealed class RiderProfileClient(HttpClient http)
{
    public async Task<RiderProfile> GetAsync(Guid riderId, string authorization, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new ApiFailure(401, "invalid_token", "A rider token is required.");
        request.Headers.Authorization = header;
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode &&
                await response.Content.ReadFromJsonAsync<RiderProfile>(cancellationToken: ct) is { } profile &&
                profile.Id == riderId && !string.IsNullOrWhiteSpace(profile.FullName) &&
                !string.IsNullOrWhiteSpace(profile.PhoneNumber))
                return profile;
        }
        catch (Exception e) when (!ct.IsCancellationRequested &&
            e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Avoid exposing upstream response details.
        }

        throw new ApiFailure(503, "rider_profile_unavailable", "Unable to load the rider profile. Try again.");
    }
}
