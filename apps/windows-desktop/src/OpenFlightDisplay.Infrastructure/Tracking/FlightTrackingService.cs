namespace OpenFlightDisplay.Infrastructure.Tracking;

using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;

/// <summary>What the user asked to follow.</summary>
/// <param name="Callsign">
/// Already normalized to the form ADS-B broadcasts, via
/// <see cref="FlightTracking.NormalizeFlightIdentifier"/>.
/// </param>
/// <param name="DestinationIcao">
/// Optional. Without it there is a position but no ETA and no departure advice,
/// which is a reduced but honest display rather than an error.
/// </param>
/// <param name="TravelMinutes">
/// Door-to-arrivals-hall time. Zero means the user never configured one, and the
/// advice stays <see cref="DepartureAdvice.Unknown"/> rather than being guessed.
/// </param>
/// <param name="PostLandingMinutes">Touchdown to walking out: taxi, bags, immigration.</param>
public sealed record TrackedFlightRequest(
    string Callsign,
    string? DestinationIcao,
    int TravelMinutes,
    int PostLandingMinutes);

/// <summary>Everything the Track Flight page draws.</summary>
public sealed record TrackedFlightState
{
    public required string Callsign { get; init; }

    public FlightProgress Progress { get; init; }

    public DeparturePlan Departure { get; init; }

    /// <summary>Latest position report, or <c>null</c> if never seen.</summary>
    public AircraftState? Aircraft { get; init; }

    /// <summary>Resolved destination, or <c>null</c> if none was set or it failed.</summary>
    public Airport? Destination { get; init; }

    /// <summary>
    /// Why the destination is missing, in words for the user.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="FeedIssue"/>: a code the service does not know is
    /// the user's to fix, a lookup that failed is not.
    /// </remarks>
    public string? DestinationIssue { get; init; }

    /// <summary>Why the last poll did not produce an update, if it did not.</summary>
    public string? FeedIssue { get; init; }

    /// <summary>When the flight was last actually seen.</summary>
    public DateTimeOffset? LastContact { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Follows one flight to its destination on an adaptive cadence.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the tracked-flight half of
/// <c>firmware/display/src/app/adsb_provider.cpp</c>. All the judgement lives in
/// <see cref="FlightTracking"/>, which the firmware's own native tests and this
/// project's tests both cover; this class is the loop around it.
/// </para>
/// <para>
/// The cadence is the point. A flight three hours out is polled every five
/// minutes and one on final every ten seconds, which is both gentler on a free
/// service and more responsive at the only moment anybody is watching.
/// </para>
/// </remarks>
public sealed partial class FlightTrackingService : IAsyncDisposable
{
    private readonly ITrackedFlightGateway _gateway;
    private readonly ILogger<FlightTrackingService> _logger;
    private readonly TimeProvider _timeProvider;

    private TrackedFlightRequest? _request;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    private Airport? _destination;
    private string? _destinationIssue;

    /// <summary>
    /// True once the destination lookup has settled and should not be retried.
    /// </summary>
    /// <remarks>
    /// A code the service does not know will not start being known, so that case
    /// stops retrying. A network failure will, so that one keeps trying on each
    /// poll — the difference is why the lookup returns three outcomes.
    /// </remarks>
    private bool _destinationSettled;

    private AircraftState? _lastAircraft;
    private DateTimeOffset? _lastContact;
    private bool _everSeen;

    /// <summary>
    /// Departure advice as of the previous publish.
    /// </summary>
    /// <remarks>
    /// Kept so a change can be reported once rather than on every poll — the
    /// difference between one "leave now" and one every ten seconds.
    /// </remarks>
    private DepartureAdvice _lastAdvice = DepartureAdvice.Unknown;

    public FlightTrackingService(
        ITrackedFlightGateway gateway,
        ILogger<FlightTrackingService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on every published update, on the polling thread.</summary>
    public event EventHandler<TrackedFlightState>? StateChanged;

    /// <summary>
    /// Raised when the departure advice changes, not on every poll.
    /// </summary>
    /// <remarks>
    /// The toast hangs off this. Firing per poll would produce a notification
    /// every ten seconds through the whole approach, which would train the user
    /// to dismiss the one that matters.
    /// </remarks>
    public event EventHandler<TrackedFlightState>? DepartureAdviceChanged;

    /// <summary>Latest published state, or <c>null</c> when nothing is tracked.</summary>
    public TrackedFlightState? CurrentState { get; private set; }

    /// <summary>What is being followed, or <c>null</c>.</summary>
    public TrackedFlightRequest? Tracked => _request;

    /// <summary>
    /// Sets what to follow and publishes an initial state, without polling.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="StartAsync"/> so the tracking behaviour can be
    /// driven one <see cref="PollOnceAsync"/> at a time. Callers that want a
    /// running tracker use <see cref="StartAsync"/>, which is this plus the loop.
    /// </remarks>
    public void Configure(TrackedFlightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Callsign);

        // Every piece of per-flight state belongs to the old flight. Carrying
        // any of it over would show one flight's last known position under
        // another's callsign.
        _request = request;
        _destination = null;
        _destinationIssue = null;
        _destinationSettled = request.DestinationIcao is null;
        _lastAircraft = null;
        _lastContact = null;
        _everSeen = false;
        _lastAdvice = DepartureAdvice.Unknown;

        LogStarted(_logger, request.Callsign, request.DestinationIcao ?? "none");

        // Publish immediately so the page shows "waiting for contact" rather
        // than an empty panel until the first poll returns.
        Publish();
    }

    /// <summary>
    /// Starts following a flight, replacing anything already tracked.
    /// </summary>
    public async Task StartAsync(TrackedFlightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        await StopAsync().ConfigureAwait(false);

        Configure(request);

        _pollingCts = new CancellationTokenSource();
        _pollingTask = RunAsync(_pollingCts.Token);
    }

    /// <summary>Stops following. Safe to call when nothing is tracked.</summary>
    public async Task StopAsync()
    {
        if (_pollingCts is not null)
        {
            await _pollingCts.CancelAsync().ConfigureAwait(false);

            if (_pollingTask is not null)
            {
                try
                {
                    await _pollingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: this is how the loop ends.
                }
            }

            _pollingCts.Dispose();
            _pollingCts = null;
            _pollingTask = null;
        }

        // Cleared whether or not a loop was running, so a service that was only
        // configured still stops cleanly.
        _request = null;
        CurrentState = null;
    }

    /// <summary>
    /// Runs one poll and returns how long to wait before the next.
    /// </summary>
    /// <remarks>
    /// Separated from the loop so the transitions can be tested a step at a time
    /// without waiting on a real clock.
    /// </remarks>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_request is not { } request)
        {
            return FlightTracking.MaxPollInterval;
        }

        // A destination that failed for a reason that might clear is retried
        // here rather than only at start, so a flight configured while the
        // network was down still gets its ETA once it comes back.
        if (!_destinationSettled)
        {
            await ResolveDestinationAsync(request.DestinationIcao, cancellationToken)
                .ConfigureAwait(false);
        }

        string? feedIssue = null;

        ProviderResult result = await _gateway
            .FetchByCallsignAsync(request.Callsign, cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case ProviderResult.Success success:
                if (success.Aircraft.Count > 0)
                {
                    _lastAircraft = success.Aircraft[0];
                    _lastContact = success.ObservedAt;
                    _everSeen = true;
                }

                // An empty answer is not an error. Before pushback the
                // transponder is simply off, and in a coverage gap the aircraft
                // is still flying. Either way the previous position is kept and
                // the staleness clock does the talking.
                break;

            case ProviderResult.Failure failure:
                feedIssue = failure.Detail;
                LogPollFailed(_logger, request.Callsign, failure.Kind.ToString(), failure.Detail);
                break;

            default:
                break;
        }

        return Publish(feedIssue);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait = await PollOnceAsync(cancellationToken).ConfigureAwait(false);

                await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
#pragma warning disable CA1031 // The loop must report a bug, not vanish with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // An unexpected throw here would otherwise leave the page frozen on
            // its last state with no explanation, which is exactly the silent
            // failure this project forbids.
            LogLoopFaulted(_logger, ex);
            Publish($"Tracking stopped unexpectedly: {ex.Message}");
        }
    }

    private async Task ResolveDestinationAsync(string? icao, CancellationToken cancellationToken)
    {
        AirportLookupResult result = await _gateway
            .ResolveAirportAsync(icao, cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case AirportLookupResult.Resolved resolved:
                _destination = resolved.Airport;
                _destinationIssue = null;
                _destinationSettled = true;
                break;

            case AirportLookupResult.NotFound notFound:
                // Will not start being known, so this stops retrying.
                _destination = null;
                _destinationIssue =
                    $"{notFound.Icao} was not recognised. Check the code — it must be the "
                    + "four-letter ICAO identifier, like KSEA rather than SEA.";
                _destinationSettled = true;
                break;

            case AirportLookupResult.Failure failure:
                _destination = null;
                _destinationIssue =
                    $"The destination could not be looked up: {failure.Detail}. "
                    + "Position is still being tracked; there is no ETA without it.";

                // Deliberately not settled: a network problem may clear, and the
                // next poll retries. An invalid code arrives here too, but it
                // fails locally without a request, so retrying costs nothing.
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Recomputes progress and advice, raises the events, and returns the next
    /// poll interval.
    /// </summary>
    private TimeSpan Publish(string? feedIssue = null)
    {
        if (_request is not { } request)
        {
            return FlightTracking.MaxPollInterval;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        int secondsSinceContact = _lastContact is { } seen
            ? (int)Math.Max(0, (now - seen).TotalSeconds)
            : 0;

        FlightProgress progress = FlightTracking.ComputeProgress(
            _lastAircraft, _destination, _everSeen, secondsSinceContact);

        DeparturePlan departure = FlightTracking.ComputeDeparturePlan(
            progress, request.TravelMinutes, request.PostLandingMinutes);

        var state = new TrackedFlightState
        {
            Callsign = request.Callsign,
            Progress = progress,
            Departure = departure,
            Aircraft = _lastAircraft,
            Destination = _destination,
            DestinationIssue = _destinationIssue,
            FeedIssue = feedIssue,
            LastContact = _lastContact,
            UpdatedAt = now,
        };

        CurrentState = state;
        StateChanged?.Invoke(this, state);

        if (departure.Advice != _lastAdvice)
        {
            _lastAdvice = departure.Advice;
            DepartureAdviceChanged?.Invoke(this, state);
        }

        return FlightTracking.PollIntervalFor(progress);
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Tracking {Callsign} to {Destination}")]
    private static partial void LogStarted(ILogger logger, string callsign, string destination);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Poll for {Callsign} failed ({Kind}): {Detail}. Keeping the last known position")]
    private static partial void LogPollFailed(
        ILogger logger, string callsign, string kind, string detail);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Error,
        Message = "The flight tracking loop faulted and has stopped")]
    private static partial void LogLoopFaulted(ILogger logger, Exception exception);
}
