namespace OpenFlightDisplay.Providers.AdsbLol;

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Geo;
using OpenFlightDisplay.Core.Units;

/// <summary>
/// adsb.lol adapter. Free, open, no API key required.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint <c>/v2/point/{lat}/{lon}/{radiusNm}</c> and its response shape
/// are confirmed against the live API — see the comment history in
/// <c>services/gateway/src/providers/adsblol.ts</c>, where the original
/// "re-verify before relying on this" warning was discharged.
/// </para>
/// <para>
/// The 250 NM clamp matches the gateway's, and is intentionally far looser than
/// the firmware's 80 NM. The firmware's limit exists because a larger response
/// overruns a fixed 16 KB parse buffer; a desktop has real memory and only needs
/// to avoid asking a community-funded service for a continent.
/// </para>
/// </remarks>
public sealed partial class AdsbLolProvider : IAviationDataProvider
{
    /// <summary>Upper bound on the radius we will ask adsb.lol for, in NM.</summary>
    public const double MaxRadiusNauticalMiles = 250.0;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AdsbLolProvider> _logger;

    public AdsbLolProvider(HttpClient httpClient, ILogger<AdsbLolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Id => "adsblol";

    /// <inheritdoc/>
    public string DisplayName => "adsb.lol";

    /// <inheritdoc/>
    public bool RequiresApiKey => false;

    /// <inheritdoc/>
    /// <remarks>15 seconds, matching the gateway's cadence for this provider.</remarks>
    public TimeSpan RecommendedPollInterval => TimeSpan.FromSeconds(15);

    /// <inheritdoc/>
    public async Task<ProviderResult> FetchAircraftAsync(
        MonitoringArea area,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);

        // adsb.lol only speaks circles. A non-circular area is queried using its
        // bounding circle and filtered precisely afterwards by the ranker, so
        // polygons and cones cost an over-fetch rather than being unsupported.
        (double centerLat, double centerLon, double radiusKm) = BoundingCircle(area);

        double radiusNm = Math.Min(
            UnitConverter.DistanceFromKm(radiusKm, UnitSystem.Aviation),
            MaxRadiusNauticalMiles);

        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"/v2/point/{centerLat}/{centerLon}/{radiusNm:F1}");

        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderResult.Failure(
                    ClassifyStatus(response.StatusCode),
                    $"adsb.lol returned HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            var aircraft = AdsbLolNormalizer.ParseResponse(body, Id, observedAt);

            LogFetched(_logger, aircraft.Count, radiusNm);

            return new ProviderResult.Success(aircraft, observedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a provider failure. Let it
            // propagate so an abandoned poll doesn't surface as an outage.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Cancelled without the caller asking means the HttpClient timeout fired.
            return new ProviderResult.Failure(
                FeedFailure.Timeout, "adsb.lol did not respond in time", ex);
        }
        catch (HttpRequestException ex)
        {
            FeedFailure kind = ex.InnerException is System.Net.Sockets.SocketException
                ? FeedFailure.NetworkUnavailable
                : FeedFailure.ProviderUnavailable;

            return new ProviderResult.Failure(kind, $"request to adsb.lol failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            return new ProviderResult.Failure(
                FeedFailure.InvalidResponse, "adsb.lol returned invalid JSON", ex);
        }
    }

    /// <summary>
    /// Source-generated log method.
    /// </summary>
    /// <remarks>
    /// This sits in the poll loop, which runs every 15 seconds for the lifetime
    /// of the application. A source-generated delegate avoids boxing the
    /// arguments and formatting the message when debug logging is switched off,
    /// which is the normal case.
    /// </remarks>
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "adsb.lol returned {Count} usable aircraft within {RadiusNm:F1} NM")]
    private static partial void LogFetched(ILogger logger, int count, double radiusNm);

    /// <summary>
    /// Smallest circle enclosing the area, for providers that only accept a
    /// centre and radius.
    /// </summary>
    internal static (double CenterLat, double CenterLon, double RadiusKm) BoundingCircle(
        MonitoringArea area) => area switch
        {
            CircleArea c => (c.CenterLat, c.CenterLon, c.RadiusKm),

            // A cone is a sector of its own circle, so the circle already bounds it.
            ConeArea c => (c.CenterLat, c.CenterLon, c.RadiusKm),

            PolygonArea p => BoundingCircleOfPolygon(p),

            _ => throw new NotSupportedException(
                $"No bounding circle defined for area type {area.GetType().Name}."),
        };

    private static (double CenterLat, double CenterLon, double RadiusKm) BoundingCircleOfPolygon(
        PolygonArea polygon)
    {
        // Centroid of the vertices, then the distance to the farthest one.
        // Not the minimal enclosing circle, but cheap and always valid - the
        // cost of it being slightly large is a marginally bigger fetch.
        double centerLat = polygon.Vertices.Average(v => v.Lat);
        double centerLon = polygon.Vertices.Average(v => v.Lon);

        double radiusKm = polygon.Vertices.Max(
            v => GeoMath.HaversineDistanceKm(centerLat, centerLon, v.Lat, v.Lon));

        return (centerLat, centerLon, radiusKm);
    }

    /// <summary>
    /// Maps an HTTP status to a failure the UI can give distinct advice for.
    /// </summary>
    private static FeedFailure ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => FeedFailure.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FeedFailure.AuthenticationFailed,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => FeedFailure.Timeout,
        _ => FeedFailure.ProviderUnavailable,
    };
}
