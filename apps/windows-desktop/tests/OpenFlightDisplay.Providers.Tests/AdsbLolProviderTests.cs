namespace OpenFlightDisplay.Providers.Tests;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Providers.AdsbLol;
using Xunit;

/// <summary>
/// Provider-level behaviour: HTTP failure classification, cancellation, and the
/// bounding-circle logic. Uses a stub handler - no live service.
/// </summary>
public class AdsbLolProviderTests
{
    private static readonly CircleArea Area = new(47.6062, -122.3321, RadiusKm: 50.0);

    [Fact]
    public async Task A_successful_response_yields_aircraft()
    {
        var provider = Provider(HttpStatusCode.OK, """
            { "ac": [ { "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 } ] }
            """);

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var success = Assert.IsType<ProviderResult.Success>(result);
        Assert.Single(success.Aircraft);
    }

    [Fact]
    public async Task An_empty_sky_is_a_success_not_a_failure()
    {
        var provider = Provider(HttpStatusCode.OK, """{ "ac": [], "total": 0 }""");

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var success = Assert.IsType<ProviderResult.Success>(result);
        Assert.Empty(success.Aircraft);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, FeedFailure.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, FeedFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, FeedFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.RequestTimeout, FeedFailure.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, FeedFailure.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, FeedFailure.ProviderUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, FeedFailure.ProviderUnavailable)]
    public async Task Classifies_http_failures_by_what_the_user_can_do_about_them(
        HttpStatusCode status,
        FeedFailure expected)
    {
        // Rate-limited and auth-failed both arrive as 4xx but demand completely
        // different advice: wait, versus go fix your key.
        var provider = Provider(status, "");

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(expected, failure.Kind);
    }

    [Fact]
    public async Task Malformed_json_becomes_an_invalid_response_failure_not_an_exception()
    {
        var provider = Provider(HttpStatusCode.OK, "{ not json at all");

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.InvalidResponse, failure.Kind);
    }

    [Fact]
    public async Task A_socket_failure_is_reported_as_network_unavailable()
    {
        var provider = ThrowingProvider(
            new HttpRequestException("no route", new System.Net.Sockets.SocketException()));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.NetworkUnavailable, failure.Kind);
    }

    [Fact]
    public async Task A_non_socket_request_failure_is_reported_as_provider_unavailable()
    {
        var provider = ThrowingProvider(new HttpRequestException("bad handshake"));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.ProviderUnavailable, failure.Kind);
    }

    [Fact]
    public async Task Caller_requested_cancellation_propagates_rather_than_looking_like_an_outage()
    {
        // An abandoned poll is not a provider failure. Surfacing it as one would
        // flash a spurious "provider unavailable" every time the area changes.
        var provider = ThrowingProvider(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchAircraftAsync(Area, cts.Token));
    }

    [Fact]
    public async Task A_client_side_timeout_is_reported_as_a_timeout_failure()
    {
        // HttpClient signals its own timeout as OperationCanceledException with
        // no cancellation requested by us.
        var provider = ThrowingProvider(new OperationCanceledException());

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.Timeout, failure.Kind);
    }

    [Fact]
    public async Task Clamps_the_requested_radius_to_250_nautical_miles()
    {
        // Politeness to a free, community-funded service.
        var handler = new StubHandler(HttpStatusCode.OK, """{ "ac": [] }""");
        var provider = Provider(handler);
        var huge = new CircleArea(47.6, -122.3, RadiusKm: 5000.0);

        await provider.FetchAircraftAsync(huge, CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.EndsWith("/250.0", handler.LastRequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cone_is_bounded_by_its_own_circle()
    {
        var cone = new ConeArea(47.6, -122.3, RadiusKm: 30.0, HeadingDeg: 90, WidthDeg: 60);

        var (lat, lon, radiusKm) = AdsbLolProvider.BoundingCircle(cone);

        Assert.Equal(47.6, lat);
        Assert.Equal(-122.3, lon);
        Assert.Equal(30.0, radiusKm);
    }

    [Fact]
    public void A_polygon_is_bounded_by_a_circle_containing_every_vertex()
    {
        var polygon = new PolygonArea([
            new GeoPoint(47.5, -122.5),
            new GeoPoint(47.7, -122.5),
            new GeoPoint(47.7, -122.1),
            new GeoPoint(47.5, -122.1),
        ]);

        var (lat, lon, radiusKm) = AdsbLolProvider.BoundingCircle(polygon);

        foreach (GeoPoint v in polygon.Vertices)
        {
            double d = Core.Geo.GeoMath.HaversineDistanceKm(lat, lon, v.Lat, v.Lon);
            Assert.True(d <= radiusKm + 1e-9, $"vertex at {d:F3} km fell outside {radiusKm:F3} km");
        }
    }

    private static AdsbLolProvider Provider(HttpStatusCode status, string body)
        => Provider(new StubHandler(status, body));

    private static AdsbLolProvider ThrowingProvider(Exception toThrow)
        => Provider(new StubHandler(toThrow));

    private static AdsbLolProvider Provider(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.adsb.lol") };
        return new AdsbLolProvider(client, NullLogger<AdsbLolProvider>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception toThrow)
        {
            _throw = toThrow;
            _body = string.Empty;
        }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
