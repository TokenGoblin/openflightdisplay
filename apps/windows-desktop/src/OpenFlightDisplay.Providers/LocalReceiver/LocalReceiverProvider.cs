namespace OpenFlightDisplay.Providers.LocalReceiver;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;

/// <summary>
/// Reads aircraft from a dump1090, readsb or tar1090 receiver on the local
/// network.
/// </summary>
/// <remarks>
/// <para>
/// The best long-term data source for this application: no rate limits, no
/// terms of service, no internet dependency, and the lowest latency available
/// because there is no upstream aggregator between the antenna and the display.
/// </para>
/// <para>
/// <b>Only HTTP JSON feeds are supported.</b> Raw Beast binary and serial
/// decoding are deliberately out of scope — they need a full Mode S decoder,
/// which is a different project. The provider contract is shaped so those could
/// be added later without changing it.
/// </para>
/// </remarks>
public sealed partial class LocalReceiverProvider : IAviationDataProvider
{
    /// <summary>
    /// How far behind our clock the receiver may be before its data is refused.
    /// </summary>
    /// <remarks>
    /// The characteristic failure of a local receiver is not the web server
    /// going away — it is the decoder dying while the web server keeps serving
    /// the last file it wrote. That response parses perfectly and looks live.
    /// Comparing the receiver's own timestamp against ours is the only way to
    /// notice, and reporting it beats showing hours-old aircraft as current.
    /// </remarks>
    public static readonly TimeSpan MaxReceiverClockLag = TimeSpan.FromMinutes(2);

    /// <summary>Common paths, tried in order, when a bare host is configured.</summary>
    /// <remarks>
    /// tar1090 and readsb serve under <c>/data/</c>; a plain dump1090 install
    /// usually serves under <c>/dump1090/data/</c> or at the root. Trying the
    /// well-known ones means a user can paste <c>http://192.168.1.10</c> and have
    /// it work rather than needing to know their install's layout.
    /// </remarks>
    public static IReadOnlyList<string> WellKnownPaths { get; } =
    [
        "/data/aircraft.json",
        "/dump1090/data/aircraft.json",
        "/dump1090-fa/data/aircraft.json",
        "/aircraft.json",
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalReceiverProvider> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Path that last worked, so a working install is not re-probed.</summary>
    private string? _resolvedPath;

    public LocalReceiverProvider(
        HttpClient httpClient,
        ILogger<LocalReceiverProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Id => "local-receiver";

    /// <inheritdoc/>
    public string DisplayName => "Local ADS-B receiver";

    /// <inheritdoc/>
    public bool RequiresApiKey => false;

    /// <inheritdoc/>
    /// <remarks>
    /// One second. A local receiver has no rate limit and no upstream to be
    /// polite to, so the cadence is bounded only by how often the file changes —
    /// dump1090 rewrites it roughly every second.
    /// </remarks>
    public TimeSpan RecommendedPollInterval => TimeSpan.FromSeconds(1);

    /// <summary>Messages the receiver reported decoding, if it said.</summary>
    public long? TotalMessages { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// A receiver reports everything it hears, with no server-side area filter.
    /// The whole set is returned and the ranker applies the monitoring area,
    /// which is also what makes the area editable without re-fetching.
    /// </remarks>
    public async Task<ProviderResult> FetchAircraftAsync(
        MonitoringArea area,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);

        IEnumerable<string> paths = _resolvedPath is { } known ? [known] : WellKnownPaths;
        ProviderResult.Failure? lastFailure = null;

        foreach (string path in paths)
        {
            ProviderResult result = await TryPathAsync(path, cancellationToken)
                .ConfigureAwait(false);

            if (result is ProviderResult.Success)
            {
                _resolvedPath = path;
                return result;
            }

            lastFailure = (ProviderResult.Failure)result;

            // Only a missing path is worth trying the next candidate for. A
            // refused connection or a stale receiver means the host is wrong or
            // broken, and probing further paths just delays the real answer.
            if (lastFailure.Kind != FeedFailure.LocalReceiverUnavailable)
            {
                break;
            }
        }

        // A previously working path that has started failing is forgotten, so
        // the next poll probes again rather than being stuck on a path the
        // receiver no longer serves.
        _resolvedPath = null;

        return lastFailure ?? new ProviderResult.Failure(
            FeedFailure.LocalReceiverUnavailable, "No receiver path could be reached");
    }

    private async Task<ProviderResult> TryPathAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderResult.Failure(
                    response.StatusCode == HttpStatusCode.NotFound
                        ? FeedFailure.LocalReceiverUnavailable
                        : FeedFailure.LocalReceiverUnavailable,
                    $"receiver returned HTTP {(int)response.StatusCode} for {path}");
            }

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();
            LocalReceiverSnapshot snapshot =
                LocalReceiverNormalizer.Parse(body, Id, fetchedAt);

            TotalMessages = snapshot.TotalMessages;

            // The decoder-died-but-web-server-lives case. Refused rather than
            // displayed, because the data looks completely healthy otherwise.
            if (snapshot.ReceiverTime is { } receiverTime)
            {
                TimeSpan lag = fetchedAt - receiverTime;
                if (lag > MaxReceiverClockLag)
                {
                    LogReceiverStale(_logger, lag.TotalSeconds, path);

                    return new ProviderResult.Failure(
                        FeedFailure.LocalReceiverUnavailable,
                        $"receiver data is {lag.TotalMinutes:N0} minutes old - " +
                        "the decoder may have stopped while its web server kept running");
                }
            }

            LogFetched(_logger, snapshot.Aircraft.Count, path);
            return new ProviderResult.Success(snapshot.Aircraft, fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return new ProviderResult.Failure(
                FeedFailure.Timeout, "the receiver did not respond in time", ex);
        }
        catch (HttpRequestException ex)
        {
            return new ProviderResult.Failure(
                FeedFailure.LocalReceiverUnavailable,
                $"could not reach the receiver: {ex.Message}",
                ex);
        }
        catch (JsonException ex)
        {
            return new ProviderResult.Failure(
                FeedFailure.InvalidResponse,
                "the receiver returned something that is not valid JSON - " +
                "check the URL points at aircraft.json and not a web page",
                ex);
        }
    }

    [LoggerMessage(
        EventId = 7000,
        Level = LogLevel.Debug,
        Message = "Local receiver returned {Count} aircraft from {Path}")]
    private static partial void LogFetched(ILogger logger, int count, string path);

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Warning,
        Message = "Local receiver data at {Path} is {LagSeconds:N0}s old; the decoder may have stopped")]
    private static partial void LogReceiverStale(ILogger logger, double lagSeconds, string path);
}
