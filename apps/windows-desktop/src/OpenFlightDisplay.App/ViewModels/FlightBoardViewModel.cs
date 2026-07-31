namespace OpenFlightDisplay.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Units;

/// <summary>
/// Drives the flight board and the status banner.
/// </summary>
/// <remarks>
/// Every <see cref="FeedState"/> maps to a concrete, worded status. There is no
/// path through this class that leaves the UI showing an indefinite spinner
/// with no explanation — that is the project's binding reliability rule, and
/// modelling feed state as a closed hierarchy is what lets the compiler help
/// enforce it.
/// </remarks>
public sealed partial class FlightBoardViewModel : ObservableObject
{
    private readonly AircraftFeedService _feed;
    private readonly DispatcherQueue _dispatcher;
    private readonly TimeProvider _timeProvider;

    // Partial properties rather than annotated fields: in a WinUI 3 app the
    // field form generates code that is not AOT-compatible, because the CsWinRT
    // generators need a real property declaration to hang the WinRT marshalling
    // off (MVVMTK0045).
    [ObservableProperty]
    public partial string StatusHeadline { get; set; }

    [ObservableProperty]
    public partial string StatusDetail { get; set; }

    [ObservableProperty]
    public partial StatusSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ProviderAttribution { get; set; }

    [ObservableProperty]
    public partial UnitSystem Units { get; set; }

    public FlightBoardViewModel(
        AircraftFeedService feed,
        DispatcherQueue dispatcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _feed = feed;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Partial properties cannot carry initialisers, so defaults are set
        // here. Apply() immediately overwrites them from the current feed state.
        StatusHeadline = "Not configured";
        StatusDetail = "Choose a data source to begin.";
        StatusSeverity = StatusSeverity.Info;
        ProviderAttribution = string.Empty;
        Units = UnitSystem.Aviation;

        _feed.StateChanged += OnFeedStateChanged;
        Apply(_feed.CurrentState);
    }

    /// <summary>Rows currently on the board.</summary>
    public ObservableCollection<AircraftRowViewModel> Aircraft { get; } = [];

    /// <summary>Starts the feed for the given area.</summary>
    public Task StartAsync(MonitoringArea area, double observerLat, double observerLon)
        => _feed.StartAsync(area, observerLat, observerLon);

    private void OnFeedStateChanged(object? sender, FeedState state)
    {
        // The feed publishes from a background poll thread; every UI-bound
        // mutation has to hop to the dispatcher. Touching ObservableCollection
        // off-thread throws at unpredictable moments rather than immediately.
        if (_dispatcher.HasThreadAccess)
        {
            Apply(state);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Apply(state));
        }
    }

    private void Apply(FeedState state)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Connecting is the only state that shows a progress indicator, and it
        // is bounded — the pipeline always transitions out of it.
        IsBusy = state is FeedState.Connecting;

        switch (state)
        {
            case FeedState.NeedsConfiguration:
                StatusHeadline = "Setup required";
                StatusDetail = "Choose a data source and set your location to begin.";
                StatusSeverity = StatusSeverity.Info;
                break;

            case FeedState.Connecting connecting:
                StatusHeadline = "Connecting";
                StatusDetail = $"Contacting {connecting.ProviderId}…";
                StatusSeverity = StatusSeverity.Info;
                break;

            case FeedState.Live live:
                StatusHeadline = $"{live.Aircraft.Count} aircraft";
                StatusDetail = $"Live from {live.ProviderId}, updated {Ago(live.ObservedAt, now)}.";
                StatusSeverity = StatusSeverity.Ok;
                break;

            case FeedState.NoMatchingAircraft none:
                // Explicitly not an error. An empty sky is a correct answer.
                StatusHeadline = "No aircraft in range";
                StatusDetail =
                    $"{none.ProviderId} responded normally, updated {Ago(none.ObservedAt, now)}. " +
                    "Try widening the monitoring radius.";
                StatusSeverity = StatusSeverity.Info;
                break;

            case FeedState.Stale stale:
                StatusHeadline = "Data is stale";
                StatusDetail =
                    $"Showing the last known {stale.Aircraft.Count} aircraft from " +
                    $"{stale.ProviderId}, observed {Ago(stale.ObservedAt, now)}.";
                StatusSeverity = StatusSeverity.Warning;
                break;

            case FeedState.SourceUnavailable unavailable:
                StatusHeadline = HeadlineFor(unavailable.Failure);
                StatusDetail = DetailFor(unavailable, now);
                StatusSeverity = StatusSeverity.Error;
                break;

            case FeedState.ReplayComplete complete:
                StatusHeadline = "Replay complete";
                StatusDetail = $"Reached the end of {complete.RecordingName}.";
                StatusSeverity = StatusSeverity.Info;
                break;
        }

        ProviderAttribution = state switch
        {
            FeedState.Live l => $"Data: {l.ProviderId}",
            FeedState.Stale s => $"Data: {s.ProviderId}",
            FeedState.NoMatchingAircraft n => $"Data: {n.ProviderId}",
            FeedState.SourceUnavailable u => $"Data: {u.ProviderId}",
            _ => string.Empty,
        };

        SyncRows(state.KnownAircraft, now);
    }

    private void SyncRows(IReadOnlyList<Core.Aircraft.AircraftState> aircraft, DateTimeOffset now)
    {
        // Rebuilt wholesale for the Phase 1 slice. This is the known hot spot
        // for the 1,000-aircraft target: it discards and recreates every row on
        // each poll, which is fine at a dozen and will not be at a thousand.
        // Phase 2 replaces it with a keyed diff against IcaoHex. Measured, not
        // guessed, via the diagnostics page.
        Aircraft.Clear();
        foreach (var a in aircraft)
        {
            Aircraft.Add(new AircraftRowViewModel(a, Units, now));
        }
    }

    private static string HeadlineFor(FeedFailure failure) => failure switch
    {
        FeedFailure.NetworkUnavailable => "No internet connection",
        FeedFailure.ProviderUnavailable => "Data source unavailable",
        FeedFailure.GatewayUnavailable => "Gateway unreachable",
        FeedFailure.LocalReceiverUnavailable => "Local receiver unreachable",
        FeedFailure.InvalidResponse => "Invalid response from data source",
        FeedFailure.RateLimited => "Rate limited",
        FeedFailure.AuthenticationFailed => "Authentication failed",
        FeedFailure.Timeout => "Data source timed out",
        FeedFailure.LocationUnavailable => "Location unavailable",
        FeedFailure.InvalidConfiguration => "Configuration problem",
        FeedFailure.DatabaseFailure => "Local database problem",
        _ => "Data source unavailable",
    };

    private static string DetailFor(FeedState.SourceUnavailable state, DateTimeOffset now)
    {
        // Advice differs by what the user can actually do, which is why
        // FeedFailure separates causes rather than just carrying a status code.
        string advice = state.Failure switch
        {
            FeedFailure.NetworkUnavailable => "Check your network connection.",
            FeedFailure.RateLimited => "Retrying automatically with a longer interval.",
            FeedFailure.AuthenticationFailed => "Check the API credentials in Settings.",
            FeedFailure.LocationUnavailable => "Set a home location in Settings.",
            FeedFailure.InvalidConfiguration => "Review your data source settings.",
            _ => "Retrying automatically.",
        };

        // Naming what is still on screen, and how old it is, is required —
        // a source failure must not silently blank the board.
        string retained = state.LastKnownAircraft.Count > 0 && state.LastSuccessAt is { } last
            ? $" Showing {state.LastKnownAircraft.Count} aircraft from {Ago(last, now)}."
            : string.Empty;

        return $"{state.Detail}. {advice}{retained}";
    }

    private static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        int seconds = (int)Math.Max(0, (now - when).TotalSeconds);
        return seconds < 60 ? $"{seconds}s ago" : $"{seconds / 60}m ago";
    }
}

/// <summary>
/// Severity of the status banner.
/// </summary>
/// <remarks>
/// Paired with wording in the view, never rendered as colour alone.
/// </remarks>
public enum StatusSeverity
{
    Info,
    Ok,
    Warning,
    Error,
}
