using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftRide.Matching.Infrastructure;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class GoongClientTests
{
    [Fact]
    public async Task Reverse_geocoding_sends_coordinates_and_parses_the_address()
    {
        HttpRequestMessage? capturedRequest = null;
        var (client, httpClient) = CreateClient(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {"status":"OK","results":[{"formatted_address":"District 1, Ho Chi Minh City","place_id":"place-1",
                "geometry":{"location":{"lat":10.5,"lng":106.7}}}]}
                """);
        });
        using (httpClient)
        {
            var result = await client.ReverseGeocodeAsync(10.5, 106.7, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("District 1, Ho Chi Minh City", result.FormattedAddress);
            Assert.Equal(10.5, result.Latitude);
            Assert.Equal(106.7, result.Longitude);
            Assert.Equal("place-1", result.PlaceId);
            Assert.NotNull(capturedRequest);
            var query = QueryHelpers.ParseQuery(capturedRequest.RequestUri!.Query);
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
                {"status":"OK","predictions":[{"description":"District 1, Ho Chi Minh City","place_id":"place-1"}]}
                """);
        });
        using (httpClient)
        {
            var results = await client.SearchPlacesAsync("Quận 1", CancellationToken.None);

            var suggestion = Assert.Single(results);
            Assert.Equal("District 1, Ho Chi Minh City", suggestion.Description);
            Assert.Equal("place-1", suggestion.PlaceId);
            Assert.NotNull(capturedRequest);
            Assert.Equal("/Place/AutoComplete", capturedRequest.RequestUri!.AbsolutePath);
            Assert.Equal("Quận 1", QueryHelpers.ParseQuery(capturedRequest.RequestUri.Query)["input"]);
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
