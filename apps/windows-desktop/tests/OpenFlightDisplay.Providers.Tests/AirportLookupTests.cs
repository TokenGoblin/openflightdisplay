namespace OpenFlightDisplay.Providers.Tests;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Providers.AdsbLol;
using Xunit;

/// <summary>
/// Destination resolution. The cases that matter are the ones where the service
/// answers successfully but has nothing useful to say — those must not become an
/// airport at 0N 0E with a sea-level field.
/// </summary>
public class AirportLookupTests
{
    [Fact]
    public async Task Resolves_coordinates_and_field_elevation()
    {
        var lookup = Lookup(HttpStatusCode.OK, """
            {
              "icao": "KDEN", "iata": "DEN", "name": "Denver International",
              "lat": 39.8617, "lon": -104.6731, "alt_feet": 5431
            }
            """);

        var result = await lookup.ResolveAsync("KDEN", CancellationToken.None);

        var resolved = Assert.IsType<AirportLookupResult.Resolved>(result);
        Assert.Equal("KDEN", resolved.Airport.Icao);
        Assert.Equal("Denver International", resolved.Airport.Name);
        Assert.Equal(39.8617, resolved.Airport.Latitude, 4);
        Assert.Equal(-104.6731, resolved.Airport.Longitude, 4);

        // The whole reason this request exists. Denver's ramp is a mile up, and
        // judging "landed" against sea level there never reports an arrival.
        Assert.Equal(5431, resolved.Airport.ElevationFt, 0);
    }

    [Fact]
    public async Task A_200_carrying_literal_null_is_not_found_not_a_success()
    {
        // This is how the endpoint reports an unknown code. Treating "the JSON
        // parsed" as "the airport exists" would produce a destination at 0N 0E.
        var lookup = Lookup(HttpStatusCode.OK, "null");

        var result = await lookup.ResolveAsync("ZZZZ", CancellationToken.None);

        var notFound = Assert.IsType<AirportLookupResult.NotFound>(result);
        Assert.Equal("ZZZZ", notFound.Icao);
    }

    [Fact]
    public async Task A_record_without_elevation_is_rejected_rather_than_defaulted_to_zero()
    {
        // Silently substituting sea level would make an inland field's arrivals
        // never register. Better to report the code as unusable.
        var lookup = Lookup(HttpStatusCode.OK, """
            { "icao": "KDEN", "lat": 39.8617, "lon": -104.6731 }
            """);

        var result = await lookup.ResolveAsync("KDEN", CancellationToken.None);

        Assert.IsType<AirportLookupResult.NotFound>(result);
    }

    [Fact]
    public async Task A_record_without_a_position_is_not_found()
    {
        var lookup = Lookup(HttpStatusCode.OK, """{ "icao": "KDEN", "alt_feet": 5431 }""");

        var result = await lookup.ResolveAsync("KDEN", CancellationToken.None);

        Assert.IsType<AirportLookupResult.NotFound>(result);
    }

    [Fact]
    public async Task Numbers_arriving_as_strings_are_still_read()
    {
        // This API already returns alt_baro as a string for surface traffic, so
        // a quoted number is a known habit rather than a hypothetical.
        var lookup = Lookup(HttpStatusCode.OK, """
            { "icao": "KSEA", "lat": "47.4502", "lon": "-122.3088", "alt_feet": "433" }
            """);

        var result = await lookup.ResolveAsync("KSEA", CancellationToken.None);

        var resolved = Assert.IsType<AirportLookupResult.Resolved>(result);
        Assert.Equal(433, resolved.Airport.ElevationFt, 0);
    }

    [Theory]
    [InlineData("SEA")]      // IATA — the endpoint answers null for these
    [InlineData("KSEAX")]
    [InlineData("K1SE")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_code_that_is_not_four_letters_is_rejected_without_a_request(string? code)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "null");
        var lookup = new AirportLookup(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.adsb.lol") },
            NullLogger<AirportLookup>.Instance);

        var result = await lookup.ResolveAsync(code, CancellationToken.None);

        var failure = Assert.IsType<AirportLookupResult.Failure>(result);
        Assert.Equal(FeedFailure.InvalidResponse, failure.Kind);

        // Rejected locally: a malformed code must not cost a request to a
        // free, community-funded service.
        Assert.Equal(0, handler.RequestCount);

        // And the message has to say what a valid code looks like, because
        // "SEA" being wrong is genuinely surprising to a user.
        Assert.Contains("four letters", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lowercase_code_is_accepted_and_uppercased()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, """
            { "icao": "EGLL", "lat": 51.4775, "lon": -0.4614, "alt_feet": 83 }
            """);

        var lookup = new AirportLookup(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.adsb.lol") },
            NullLogger<AirportLookup>.Instance);

        var result = await lookup.ResolveAsync(" egll ", CancellationToken.None);

        Assert.IsType<AirportLookupResult.Resolved>(result);
        Assert.EndsWith("/api/0/airport/EGLL", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_is_not_found_rather_than_a_failure()
    {
        var lookup = Lookup(HttpStatusCode.NotFound, "");

        var result = await lookup.ResolveAsync("ZZZZ", CancellationToken.None);

        Assert.IsType<AirportLookupResult.NotFound>(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, FeedFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, FeedFailure.ProviderUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, FeedFailure.Timeout)]
    public async Task A_service_problem_is_a_failure_not_a_missing_airport(
        HttpStatusCode status,
        FeedFailure expected)
    {
        // Telling somebody their correct ICAO code does not exist because the
        // service was down would send them looking for the wrong problem.
        var lookup = Lookup(status, "");

        var result = await lookup.ResolveAsync("KSEA", CancellationToken.None);

        var failure = Assert.IsType<AirportLookupResult.Failure>(result);
        Assert.Equal(expected, failure.Kind);
    }

    [Fact]
    public async Task Invalid_json_is_a_failure_not_a_missing_airport()
    {
        var lookup = Lookup(HttpStatusCode.OK, "{ this is not json");

        var result = await lookup.ResolveAsync("KSEA", CancellationToken.None);

        var failure = Assert.IsType<AirportLookupResult.Failure>(result);
        Assert.Equal(FeedFailure.InvalidResponse, failure.Kind);
    }

    [Fact]
    public async Task A_caller_cancellation_propagates_rather_than_becoming_a_failure()
    {
        var lookup = Lookup(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lookup.ResolveAsync("KSEA", cts.Token));
    }

    private static AirportLookup Lookup(HttpStatusCode status, string body)
        => Lookup(new CountingHandler(status, body));

    private static AirportLookup Lookup(Exception toThrow)
        => Lookup(new CountingHandler(toThrow));

    private static AirportLookup Lookup(CountingHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.adsb.lol") },
            NullLogger<AirportLookup>.Instance);

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public CountingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public CountingHandler(Exception toThrow)
        {
            _throw = toThrow;
            _body = string.Empty;
        }

        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;

            if (_throw is not null)
            {
                throw _throw;
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
