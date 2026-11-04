using System.Net;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftRide.Matching.Infrastructure;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class GoongClientTests
{
    [Fact]
    public async Task Forward_geocoding_uses_current_units_even_for_an_old_address_query()
    {
        var (client, http) = CreateClient(request =>
        {
            Assert.Equal("/v2/geocode", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("273 An Dương Vương, Quận 5", query["address"]);
            Assert.Equal("false", query["has_deprecated_administrative_unit"]);
            return JsonResponse("""
                {"status":"OK","results":[{"formatted_address":"273 An Dương Vương, Chợ Quán, Hồ Chí Minh",
                "deprecated_description":"273 An Dương Vương, Quận 5, Hồ Chí Minh",
                "place_id":"v2-campus","geometry":{"location":{"lat":10.759,"lng":106.682}}}]}
                """);
        });
        using (http)
        {
            var result = await client.GeocodeAsync("273 An Dương Vương, Quận 5", default);
            Assert.NotNull(result);
            Assert.Equal("273 An Dương Vương, Chợ Quán, Hồ Chí Minh", result.FormattedAddress);
            Assert.Equal("v2-campus", result.PlaceId);
        }
    }

    [Theory]
    [InlineData("Motorbike", "bike")]
    [InlineData("Car4Seats", "car")]
    public async Task Pricing_routes_use_V2_and_keep_vehicle_and_metrics(string vehicle, string expectedProviderVehicle)
    {
        var (client, http) = CreateClient(request =>
        {
            Assert.Equal("/v2/direction", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal(expectedProviderVehicle, query["vehicle"]);
            Assert.Equal("10.759,106.682", query["origin"]);
            return JsonResponse("""
                {"routes":[{"overview_polyline":{"points":"encoded-route"},"legs":[{"distance":{"value":4800},"duration":{"value":900},
                "start_address":"Chợ Quán, Hồ Chí Minh","end_address":"Bến Thành, Hồ Chí Minh"}]}]}
                """);
        });
        using (http)
        {
            var route = await client.GetRouteAsync(10.759, 106.682, 10.7725, 106.698, vehicle, default);
            Assert.Equal(4800, route.DistanceMeters);
            Assert.Equal(900, route.DurationSeconds);
            Assert.Equal("encoded-route", route.Polyline);
        }
    }

    [Theory]
    [InlineData("""{"error":"No route","code":400}""")]
    [InlineData("""{"routes":[]}""")]
    [InlineData("""{"status":"ZERO_RESULTS","routes":[]}""")]
    public async Task Directions_without_valid_routes_never_produce_a_quote(string body)
    {
        var (client, http) = CreateClient(_ => JsonResponse(body));
        using (http)
            await Assert.ThrowsAsync<SwiftRide.Matching.Application.MatchingFailureException>(() =>
                client.GetRouteAsync(10.759, 106.682, 10.7725, 106.698, "Motorbike", default));
    }

    [Fact]
    public async Task Details_preserve_the_selected_place_id_address_and_coordinates()
    {
        const string id = "campus/+==";
        var (client, http) = CreateClient(request =>
        {
            Assert.Equal("/v2/place/detail", request.RequestUri!.AbsolutePath);
            Assert.Equal(id, QueryHelpers.ParseQuery(request.RequestUri.Query)["place_id"]);
            Assert.Equal("false", QueryHelpers.ParseQuery(request.RequestUri.Query)["has_deprecated_administrative_unit"]);
            return JsonResponse("""
                {"status":"OK","result":{"place_id":"campus/+==","name":"Đại học Sài Gòn",
                "deprecated_description":"273 An Dương Vương, Quận 5","formatted_address":"273 An Dương Vương, Chợ Quán, Hồ Chí Minh","geometry":{"location":{"lat":10.759,"lng":106.682}}}}
                """);
        });
        using (http)
        {
            var place = await client.GetPlaceDetailsAsync(id, default);
            Assert.NotNull(place);
            Assert.Equal(id, place.PlaceId);
            Assert.Equal("273 An Dương Vương, Chợ Quán, Hồ Chí Minh", place.FormattedAddress);
            Assert.Equal(10.759, place.Latitude);
            Assert.Equal(106.682, place.Longitude);
        }
    }

    [Theory]
    [InlineData("wrong-id", "10.759", "106.682")]
    [InlineData("chosen-id", "100", "106.682")]
    [InlineData("chosen-id", "10.759", "null")]
    public async Task Details_reject_a_different_place_or_invalid_coordinates(string id, string lat, string lng)
    {
        var (client, http) = CreateClient(_ => JsonResponse(JsonSerializer.Serialize(new
        {
            status = "OK", result = new { place_id = id, name = "Campus", formatted_address = "Street",
                geometry = new { location = new { lat = double.Parse(lat, CultureInfo.InvariantCulture),
                    lng = lng == "null" ? (double?)null : double.Parse(lng, CultureInfo.InvariantCulture) } } }
        })));
        using (http)
            await Assert.ThrowsAsync<SwiftRide.Matching.Application.MatchingFailureException>(() => client.GetPlaceDetailsAsync("chosen-id", default));
    }

    [Fact]
    public async Task Details_return_no_match_instead_of_guessing_another_address()
    {
        var (client, http) = CreateClient(_ => JsonResponse("""{"status":"ZERO_RESULTS"}"""));
        using (http) Assert.Null(await client.GetPlaceDetailsAsync("missing", default));
    }

    [Fact]
    public async Task Reverse_geocoding_sends_coordinates_and_parses_the_address()
    {
        HttpRequestMessage? capturedRequest = null;
        var (client, httpClient) = CreateClient(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {"status":"OK","results":[{"deprecated_description":"Quận 1, Hồ Chí Minh","formatted_address":"Bến Thành, Hồ Chí Minh","place_id":"place-1",
                "geometry":{"location":{"lat":10.5,"lng":106.7}}}]}
                """);
        });
        using (httpClient)
        {
            var result = await client.ReverseGeocodeAsync(10.5, 106.7, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("Bến Thành, Hồ Chí Minh", result.FormattedAddress);
            Assert.Equal(10.5, result.Latitude);
            Assert.Equal(106.7, result.Longitude);
            Assert.Equal("place-1", result.PlaceId);
            Assert.NotNull(capturedRequest);
            var query = QueryHelpers.ParseQuery(capturedRequest.RequestUri!.Query);
            Assert.Equal("/v2/geocode", capturedRequest.RequestUri.AbsolutePath);
            Assert.Equal("false", query["has_deprecated_administrative_unit"]);
            Assert.Equal("10.5,106.7", query["latlng"]);
            Assert.Equal("test-goong-key", query["api_key"]);
        }
    }

    [Fact]
    public async Task Place_search_uses_autocomplete_and_returns_suggestions()
    {
        HttpRequestMessage? capturedRequest = null;
        var (client, httpClient) = CreateClient(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {"status":"OK","predictions":[{"deprecated_description":"Quận 1, Hồ Chí Minh","description":"Bến Thành, Hồ Chí Minh","place_id":"place-1"}]}
                """);
        });
        using (httpClient)
        {
            var results = await client.SearchPlacesAsync("Bến Thành", CancellationToken.None);

            var suggestion = Assert.Single(results);
            Assert.Equal("Bến Thành, Hồ Chí Minh", suggestion.Description);
            Assert.Equal("place-1", suggestion.PlaceId);
            Assert.NotNull(capturedRequest);
            Assert.Equal("/v2/place/autocomplete", capturedRequest.RequestUri!.AbsolutePath);
            Assert.Equal("Bến Thành", QueryHelpers.ParseQuery(capturedRequest.RequestUri.Query)["input"]);
            Assert.Equal("false", QueryHelpers.ParseQuery(capturedRequest.RequestUri.Query)["has_deprecated_administrative_unit"]);
        }
    }

    private static (GoongClient Client, HttpClient HttpClient) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHandler(responseFactory))
        {
            BaseAddress = new Uri("https://rsapi.goong.io/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Goong:ApiKey"] = "test-goong-key" })
            .Build();
        return (new GoongClient(httpClient, configuration, NullLogger<GoongClient>.Instance), httpClient);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
