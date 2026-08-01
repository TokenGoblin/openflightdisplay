namespace OpenFlightDisplay.Providers.AdsbLol;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Tracking;

/// <summary>Outcome of resolving a destination airport.</summary>
/// <remarks>
/// Three outcomes, not two. "No such airport" and "the lookup failed" demand
/// different words on screen — the first is a typo the user can fix, the second
/// is a network problem they cannot — and collapsing them would tell somebody
/// their correct ICAO code was wrong.
/// </remarks>
public abstract record AirportLookupResult
{
    private AirportLookupResult()
    {
    }

    /// <summary>The airport was found.</summary>
    public sealed record Resolved(Airport Airport) : AirportLookupResult;

    /// <summary>
    /// The service answered, and does not know this code.
    /// </summary>
    /// <remarks>
    /// Usually a typo or an IATA code. Not a failure — the lookup worked.
    /// </remarks>
    public sealed record NotFound(string Icao) : AirportLookupResult;

    /// <summary>The lookup could not be completed.</summary>
    public sealed record Failure(
        FeedFailure Kind,
        string Detail,
        Exception? Cause = null) : AirportLookupResult;
}

/// <summary>
/// Resolves an ICAO airport code to coordinates and field elevation.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>resolveDestination</c> in
/// <c>firmware/display/src/app/adsb_provider.cpp</c>. Runs once per tracked
/// flight rather than per poll: an airport does not move, and this is a free,
/// community-funded service.
/// </para>
/// <para>
/// Field elevation is the reason this request exists at all. Landing is judged
/// against the destination's own elevation, so without it Denver's ramp — at
/// 5,400 ft — reads as an aircraft still well in the air.
/// </para>
/// </remarks>
public sealed partial class AirportLookup
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirportLookup> _logger;

    public AirportLookup(HttpClient httpClient, ILogger<AirportLookup> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Looks up an airport by ICAO code.
    /// </summary>
    /// <param name="icao">
    /// Validated with <see cref="FlightTracking.NormalizeAirportIcao"/> before
    /// any request is made, so a malformed code costs nothing.
    /// </param>
    public async Task<AirportLookupResult> ResolveAsync(
        string? icao,
        CancellationToken cancellationToken)
    {
        if (FlightTracking.NormalizeAirportIcao(icao) is not { } code)
        {
            // Rejected without a request: the endpoint would answer null for
            // this anyway, and saying so immediately is both faster and more
            // specific than reporting a not-found.
            return new AirportLookupResult.Failure(
                FeedFailure.InvalidResponse,
                "An airport code must be exactly four letters, like KSEA or EGLL. "
                    + "Three-letter IATA codes are not accepted.");
        }

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync($"/api/0/airport/{code}", cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AirportLookupResult.NotFound(code);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AirportLookupResult.Failure(
                    ClassifyStatus(response.StatusCode),
                    $"adsb.lol returned HTTP {(int)response.StatusCode} for {code}");
            }

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            // A 200 carrying `null` is the documented answer for a code the
            // service does not know, so this is the ordinary not-found path
            // rather than an edge case.
            if (AdsbLolAirportReader.Parse(body, code) is not { } airport)
            {
                LogNotFound(_logger, code);
                return new AirportLookupResult.NotFound(code);
            }

            LogResolved(_logger, code, airport.Name ?? "unnamed", airport.ElevationFt);
            return new AirportLookupResult.Resolved(airport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return new AirportLookupResult.Failure(
                FeedFailure.Timeout, $"adsb.lol did not respond in time for {code}", ex);
        }
        catch (HttpRequestException ex)
        {
            FeedFailure kind = ex.InnerException is System.Net.Sockets.SocketException
                ? FeedFailure.NetworkUnavailable
                : FeedFailure.ProviderUnavailable;

            return new AirportLookupResult.Failure(
                kind, $"airport lookup for {code} failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            return new AirportLookupResult.Failure(
                FeedFailure.InvalidResponse, $"adsb.lol returned invalid JSON for {code}", ex);
        }
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Destination {Icao} resolved: {Name}, field elevation {ElevationFt:F0} ft")]
    private static partial void LogResolved(
        ILogger logger, string icao, string name, double elevationFt);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Destination {Icao} did not resolve; adsb.lol does not know this code")]
    private static partial void LogNotFound(ILogger logger, string icao);

    private static FeedFailure ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => FeedFailure.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FeedFailure.AuthenticationFailed,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => FeedFailure.Timeout,
        _ => FeedFailure.ProviderUnavailable,
    };
}
