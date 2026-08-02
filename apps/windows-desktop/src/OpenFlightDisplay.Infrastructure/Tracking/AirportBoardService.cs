namespace OpenFlightDisplay.Infrastructure.Tracking;

using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;

/// <summary>Everything the movements board draws.</summary>
public sealed record AirportBoardState
{
    public required Airport Airport { get; init; }

    public IReadOnlyList<AirportMovement> Movements { get; init; } = [];

    /// <summary>Why the board is empty or stale, in words, or <c>null</c>.</summary>
    public string? Issue { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Polls the traffic around one airport and classifies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An observed-movements board, not a flight-information display.</b> ADS-B
/// carries no schedule, so this shows what is actually flying, not what is meant
/// to be. There are no scheduled times, no gates and no cancellations, and the
/// UI says so rather than leaving the user to infer it from an oddly incomplete
/// airport board.
/// </para>
/// <para>
/// Runs its own poll rather than reusing the main feed, because the airport is
/// usually not the observer's home and the feed is centred on home. The cadence
/// is fixed and unhurried: an airport's traffic picture does not change usefully
/// faster than this, and it is a free service.
/// </para>
/// </remarks>
public sealed partial class AirportBoardService : IAsyncDisposable
{
    /// <summary>
    /// Gap between polls.
    /// </summary>
    /// <remarks>
    /// Deliberately slower than the radar's. A board of movements is read, not
    /// watched frame by frame, and this is a second query against a
    /// community-funded service on top of the one the radar already makes.
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly AdsbLolProvider _provider;
    private readonly AirportLookup _airports;
    private readonly ILogger<AirportBoardService> _logger;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Airport? _airport;

    public AirportBoardService(
        AdsbLolProvider provider,
        AirportLookup airports,
        ILogger<AirportBoardService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(airports);
        ArgumentNullException.ThrowIfNull(logger);

        _provider = provider;
        _airports = airports;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on every published update, on the polling thread.</summary>
    public event EventHandler<AirportBoardState>? StateChanged;

    /// <summary>Latest published state, or <c>null</c> when no airport is selected.</summary>
    public AirportBoardState? CurrentState { get; private set; }

    /// <summary>The airport being watched, or <c>null</c>.</summary>
    public Airport? Airport => _airport;

    /// <summary>
    /// Resolves an airport and starts watching it.
    /// </summary>
    /// <returns>
    /// A reason the airport could not be used, or <c>null</c> on success. The
    /// caller shows it; a board that silently stayed empty would be
    /// indistinguishable from an airport with no traffic.
    /// </returns>
    public async Task<string?> StartAsync(string? icao, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);

        AirportLookupResult result =
            await _airports.ResolveAsync(icao, cancellationToken).ConfigureAwait(false);

        switch (result)
        {
            case AirportLookupResult.Resolved resolved:
                _airport = resolved.Airport;
                break;

            case AirportLookupResult.NotFound notFound:
                return $"{notFound.Icao} was not recognised. Airport codes are the four-letter "
                    + "ICAO form, like KSLC rather than SLC.";

            case AirportLookupResult.Failure failure:
                return failure.Detail;

            default:
                return "The airport could not be resolved.";
        }

        LogWatching(_logger, _airport.Value.Icao, _airport.Value.Name ?? "unnamed");

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        return null;
    }

    /// <summary>Stops watching. Safe to call when nothing is selected.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // How the loop ends.
                }
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        _airport = null;
        CurrentState = null;
    }

    /// <summary>
    /// Runs one poll and publishes the result.
    /// </summary>
    /// <remarks>
    /// Separated from the loop so the classification can be driven a step at a
    /// time in tests without waiting on a clock.
    /// </remarks>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_airport is not { } airport)
        {
            return;
        }

        var area = new CircleArea(airport.Latitude, airport.Longitude, AirportMovements.RadiusKm);

        ProviderResult result =
            await _provider.FetchAircraftAsync(area, cancellationToken).ConfigureAwait(false);

        switch (result)
        {
            case ProviderResult.Success success:
                Publish(airport, AirportMovements.Build(success.Aircraft, airport), null);
                break;

            case ProviderResult.Failure failure:
                // The previous board is kept: stale movements with an explanation
                // beat a blank screen that looks like an empty airport.
                Publish(airport, CurrentState?.Movements ?? [], failure.Detail);
                break;

            default:
                break;
        }
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
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
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
            LogLoopFaulted(_logger, ex);

            if (_airport is { } airport)
            {
                Publish(airport, CurrentState?.Movements ?? [], $"The board stopped: {ex.Message}");
            }
        }
    }

    private void Publish(Airport airport, IReadOnlyList<AirportMovement> movements, string? issue)
    {
        var state = new AirportBoardState
        {
            Airport = airport,
            Movements = movements,
            Issue = issue,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        CurrentState = state;
        StateChanged?.Invoke(this, state);
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Watching movements at {Icao} ({Name})")]
    private static partial void LogWatching(ILogger logger, string icao, string name);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Error,
        Message = "The airport movements loop faulted and has stopped")]
    private static partial void LogLoopFaulted(ILogger logger, Exception exception);
}
