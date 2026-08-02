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

    /// <summary>
    /// Aircraft shown in the detail pane, or <c>null</c> when none is selected.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="HasSelection"/> rather than a null-to-visibility
    /// converter: a Window is not a FrameworkElement in WinUI 3, so a
    /// StaticResource converter referenced from a Window's binding fails to
    /// generate. x:Bind converts a bool to Visibility natively.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial AircraftRowViewModel? SelectedAircraft { get; set; }

    /// <summary>True when the detail pane should be shown.</summary>
    public bool HasSelection => SelectedAircraft is not null;

    /// <summary>Radius of the monitoring area, for the radar's outer ring.</summary>
    [ObservableProperty]
    public partial double RangeKm { get; set; }

    /// <summary>Observer latitude, the origin the radar is drawn around.</summary>
    [ObservableProperty]
    public partial double ObserverLatitude { get; set; }

    /// <summary>Observer longitude.</summary>
    [ObservableProperty]
    public partial double ObserverLongitude { get; set; }

    /// <summary>
    /// Recorded track of the selected aircraft, or empty when history is off.
    /// </summary>
    /// <remarks>
    /// Empty rather than null when history is disabled: there is no trail to
    /// show, and that is a normal state rather than a missing value.
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<Persistence.TrailPoint> SelectedTrail { get; set; }

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
        RangeKm = 80.0;
        SelectedTrail = [];

        _feed.StateChanged += OnFeedStateChanged;
        Apply(_feed.CurrentState);
    }

    /// <summary>Rows currently on the board.</summary>
    public ObservableCollection<AircraftRowViewModel> Aircraft { get; } = [];

    /// <summary>
    /// Reports a failure that happened during startup, before the feed began.
    /// </summary>
    /// <remarks>
    /// Startup runs outside the feed's own state machine, so a failure there
    /// has no <see cref="FeedState"/> to travel in. Without this the app would
    /// sit on its initial state with no explanation — exactly the silent
    /// failure the project forbids.
    /// </remarks>
    public void ReportStartupFailure(string detail)
    {
        StatusHeadline = "Could not start";
        StatusDetail = $"{detail} Try restarting; settings can be corrected from the Settings page.";
        StatusSeverity = StatusSeverity.Error;
        IsBusy = false;
    }

    /// <summary>
    /// The filter currently in force, used to explain an empty board.
    /// </summary>
    /// <remarks>
    /// Held here as well as on the feed so the status text can distinguish an
    /// empty sky from a board the user filtered empty themselves.
    /// </remarks>
    public Core.Ranking.AircraftFilter Filter { get; set; } = Core.Ranking.AircraftFilter.None;

    /// <summary>Starts the feed for the given provider and area.</summary>
    public Task StartAsync(
        Providers.IAviationDataProvider provider,
        MonitoringArea area,
        double observerLat,
        double observerLon,
        Core.Ranking.RankingMode rankingMode = Core.Ranking.RankingMode.NearestHorizontal)
        => _feed.StartAsync(provider, area, observerLat, observerLon, rankingMode);

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
                //
                // But an empty board caused by the user's own filter is NOT an
                // empty sky, and reporting them identically is how somebody
                // concludes the feed is broken. The active filter is named so
                // the cause is obvious and the fix is one page away.
                StatusHeadline = Filter.IsEmpty
                    ? "No aircraft in range"
                    : "Nothing matches your filter";

                StatusDetail = Filter.IsEmpty
                    ? $"{none.ProviderId} responded normally, updated {Ago(none.ObservedAt, now)}. " +
                        "Try widening the monitoring radius."
                    : $"{none.ProviderId} responded normally, updated {Ago(none.ObservedAt, now)}. " +
                        $"{Filter.Summarise()}. Change or clear the filter in Settings.";

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
        // Reconciled in place, keyed on IcaoHex.
        //
        // The earlier version cleared the collection and rebuilt every row each
        // poll. That recreates every list container, discards scroll position
        // and selection, and does not scale to the 1,000-aircraft target. Rows
        // that are still present are now updated rather than replaced, so the
        // list reuses its containers and only genuine arrivals and departures
        // move anything.
        var existing = new Dictionary<string, AircraftRowViewModel>(
            Aircraft.Count, StringComparer.Ordinal);

        foreach (AircraftRowViewModel row in Aircraft)
        {
            // A provider returning the same hex twice in one poll would
            // otherwise throw; the first wins and the duplicate is treated as
            // a new row below.
            existing.TryAdd(row.Aircraft.IcaoHex, row);
        }

        // The desired order is built first, reusing and updating surviving rows.
        //
        // Doing this in one pass over the ObservableCollection with Move() was
        // the first attempt and was O(n^2): IndexOf inside the loop is a linear
        // scan, so 1,000 aircraft cost a million operations per poll and pinned
        // a core. Measured, not assumed.
        var desired = new List<AircraftRowViewModel>(aircraft.Count);

        foreach (Core.Aircraft.AircraftState state in aircraft)
        {
            if (existing.Remove(state.IcaoHex, out AircraftRowViewModel? row))
            {
                row.Update(state, Units, now);
                desired.Add(row);
            }
            else
            {
                desired.Add(new AircraftRowViewModel(state, Units, now));
            }
        }

        // Then reconciled positionally. Each index is an O(1) comparison and, at
        // worst, an O(1) indexer assignment raising a single Replace. A row
        // whose rank did not change costs no collection event at all - only the
        // property notification its Update already raised.
        int shared = Math.Min(Aircraft.Count, desired.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!ReferenceEquals(Aircraft[i], desired[i]))
            {
                Aircraft[i] = desired[i];
            }
        }

        // Trim from the end so no index shifts more than once.
        for (int i = Aircraft.Count - 1; i >= desired.Count; i--)
        {
            Aircraft.RemoveAt(i);
        }

        for (int i = Aircraft.Count; i < desired.Count; i++)
        {
            Aircraft.Add(desired[i]);
        }

        // The selected row survives as an object when the aircraft is still
        // present, so the detail pane does not close itself every poll.
        if (SelectedAircraft is { } selected
            && !Aircraft.Contains(selected))
        {
            SelectedAircraft = null;
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
