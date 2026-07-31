namespace OpenFlightDisplay.App.Services;

using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Quality;
using OpenFlightDisplay.Core.Ranking;
using OpenFlightDisplay.Providers;

/// <summary>
/// The in-process data pipeline: poll, normalize, filter, rank, publish.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the desktop standalone — the gateway is a selectable data
/// source, not a dependency (see the ADR). The pipeline is
/// <c>Provider -> Normalization -> Filtering -> Ranking -> Application state</c>;
/// history persistence slots in before the last step in Phase 2.
/// </para>
/// <para>
/// Belongs in OpenFlightDisplay.Infrastructure once that project exists. It
/// lives here for the Phase 1 vertical slice and moves without API change.
/// </para>
/// </remarks>
public sealed partial class AircraftFeedService : IAsyncDisposable
{
    private readonly ILogger<AircraftFeedService> _logger;
    private readonly TimeProvider _timeProvider;

    private IAviationDataProvider? _provider;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    public AircraftFeedService(
        ILogger<AircraftFeedService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The provider currently being polled, or <c>null</c> if stopped.</summary>
    public IAviationDataProvider? ActiveProvider => _provider;

    /// <summary>
    /// Id of the active provider, for status messages.
    /// </summary>
    /// <remarks>
    /// Only read from the publish path, which cannot run before
    /// <see cref="StartAsync"/> has set the provider — but a placeholder is
    /// returned rather than dereferencing null, because a status string is never
    /// worth crashing the feed over.
    /// </remarks>
    private string ProviderId => _provider?.Id ?? "unknown";

    /// <summary>Raised on every state transition, including failures.</summary>
    public event EventHandler<FeedState>? StateChanged;

    /// <summary>The most recent state. Never null.</summary>
    public FeedState CurrentState { get; private set; } = new FeedState.NeedsConfiguration();

    /// <summary>
    /// Starts polling. Safe to call repeatedly — an existing loop is stopped
    /// first, so changing the area or provider cannot leave two loops running.
    /// </summary>
    /// <param name="provider">
    /// The data source to poll. Passed per-start rather than injected once, so
    /// switching data sources is the same code path as starting up.
    /// </param>
    public async Task StartAsync(
        IAviationDataProvider provider,
        MonitoringArea area,
        double observerLat,
        double observerLon,
        RankingMode rankingMode = RankingMode.NearestHorizontal)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(area);

        await StopAsync().ConfigureAwait(false);

        _provider = provider;
        _pollingCts = new CancellationTokenSource();
        CancellationToken token = _pollingCts.Token;

        Publish(new FeedState.Connecting(provider.Id));

        _pollingTask = Task.Run(
            () => PollLoopAsync(provider, area, observerLat, observerLon, rankingMode, token),
            token);
    }

    /// <summary>Stops polling and waits for the loop to unwind.</summary>
    public async Task StopAsync()
    {
        if (_pollingCts is null)
        {
            return;
        }

        await _pollingCts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_pollingTask is not null)
            {
                await _pollingTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how the loop ends.
        }
        finally
        {
            _pollingCts.Dispose();
            _pollingCts = null;
            _pollingTask = null;
        }
    }

    private async Task PollLoopAsync(
        IAviationDataProvider provider,
        MonitoringArea area,
        double observerLat,
        double observerLon,
        RankingMode rankingMode,
        CancellationToken token)
    {
        // Backoff state for consecutive failures. Reset on every success so a
        // brief outage doesn't leave the app polling slowly for a long time
        // after the provider has recovered.
        int consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            TimeSpan delay = provider.RecommendedPollInterval;

            try
            {
                ProviderResult result =
                    await provider.FetchAircraftAsync(area, token).ConfigureAwait(false);

                switch (result)
                {
                    case ProviderResult.Success success:
                        consecutiveFailures = 0;
                        PublishSuccess(success, area, observerLat, observerLon, rankingMode);
                        break;

                    case ProviderResult.Exhausted exhausted:
                        Publish(new FeedState.ReplayComplete(exhausted.RecordingName));
                        return;

                    case ProviderResult.Failure failure:
                        consecutiveFailures++;
                        delay = BackoffDelay(provider.RecommendedPollInterval, consecutiveFailures);
                        LogPollFailed(_logger, provider.Id, failure.Kind.ToString(), failure.Detail);
                        PublishFailure(failure);
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The poll loop must survive any provider bug.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A provider throwing is a bug, not an expected condition, but
                // it must not take the whole feed down silently.
                consecutiveFailures++;
                delay = BackoffDelay(provider.RecommendedPollInterval, consecutiveFailures);
                LogPollThrew(_logger, ex, provider.Id);

                PublishFailure(new ProviderResult.Failure(
                    FeedFailure.ProviderUnavailable, $"unexpected provider error: {ex.Message}", ex));
            }

            try
            {
                await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void PublishSuccess(
        ProviderResult.Success success,
        MonitoringArea area,
        double observerLat,
        double observerLon,
        RankingMode rankingMode)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        IReadOnlyList<AircraftState> ranked = AircraftRanker.Rank(
            success.Aircraft.Select(a => Staleness.WithStalenessApplied(a, now)),
            area,
            observerLat,
            observerLon,
            rankingMode);

        if (ranked.Count == 0)
        {
            // An empty sky is a correct answer, not a failure.
            Publish(new FeedState.NoMatchingAircraft(ProviderId, success.ObservedAt));
            return;
        }

        // Stale if every aircraft is stale. One lagging record among fresh ones
        // is a per-aircraft concern the board shows individually; it does not
        // make the whole feed stale.
        bool allStale = ranked.All(a => Staleness.IsStale(a, now));

        Publish(allStale
            ? new FeedState.Stale(ranked, ProviderId, success.ObservedAt)
            : new FeedState.Live(ranked, ProviderId, success.ObservedAt));
    }

    private void PublishFailure(ProviderResult.Failure failure)
    {
        // Per docs/PROTOCOL.md a source problem must not clear the last-known
        // list — it is shown alongside a data-age indicator instead of blanking.
        DateTimeOffset? lastSuccess = CurrentState switch
        {
            FeedState.Live l => l.ObservedAt,
            FeedState.Stale s => s.ObservedAt,
            FeedState.NoMatchingAircraft n => n.ObservedAt,
            FeedState.SourceUnavailable u => u.LastSuccessAt,
            _ => null,
        };

        Publish(new FeedState.SourceUnavailable(
            ProviderId,
            failure.Kind,
            failure.Detail,
            CurrentState.KnownAircraft,
            lastSuccess));
    }

    private void Publish(FeedState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Exponential backoff with jitter, capped at 30 seconds.
    /// </summary>
    /// <remarks>
    /// Base and cap match the reconnect policy already binding on every
    /// implementation in <c>docs/PROTOCOL.md</c>. The jitter matters when
    /// several clients lose a provider at once — without it they all retry in
    /// lockstep and arrive as a thundering herd.
    /// </remarks>
    internal static TimeSpan BackoffDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval;
        }

        double exponent = Math.Min(consecutiveFailures - 1, 10);
        double seconds = Math.Min(baseInterval.TotalSeconds * Math.Pow(2, exponent), 30.0);

        double jitter = Random.Shared.NextDouble() * 0.3 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Poll of {ProviderId} failed ({Kind}): {Detail}")]
    private static partial void LogPollFailed(
        ILogger logger, string providerId, string kind, string detail);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Provider {ProviderId} threw an unexpected exception")]
    private static partial void LogPollThrew(ILogger logger, Exception ex, string providerId);
}
