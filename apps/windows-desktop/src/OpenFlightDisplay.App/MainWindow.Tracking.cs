namespace OpenFlightDisplay.App;

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.App.Dialogs;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Export;
using OpenFlightDisplay.Core.Ranking;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Infrastructure.Maps;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Infrastructure.Tracking;
using OpenFlightDisplay.Persistence;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.Replay;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

/// <summary>
/// Following one flight to its destination.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- Track Flight ----

    /// <summary>
    /// Reads the form, starts tracking, and persists the choice.
    /// </summary>
    /// <remarks>
    /// Both identifiers are validated before anything is started. A wrong flight
    /// number is indistinguishable from one that has not pushed back — both
    /// report "waiting for contact" forever — so catching a malformed one here
    /// is the only chance to tell the user it was their input that was wrong.
    /// </remarks>
    private void OnStartTracking(object sender, RoutedEventArgs e) => Safe(StartTrackingAsync);

    private async Task StartTrackingAsync()
    {
        if (FlightTracking.NormalizeFlightIdentifier(TrackCallsignBox.Text) is not { } callsign)
        {
            ShowTrackError(
                "Enter a flight number like UA1234 or a callsign like UAL1234. "
                + "It needs letters followed by digits.");
            return;
        }

        // Blank is allowed and means "follow it, but I am not collecting
        // anyone" — position without an ETA. A non-blank code must be valid.
        string? destination = null;
        if (!string.IsNullOrWhiteSpace(TrackDestinationBox.Text))
        {
            destination = FlightTracking.NormalizeAirportIcao(TrackDestinationBox.Text);
            if (destination is null)
            {
                ShowTrackError(
                    "A destination must be the four-letter ICAO code, like KSEA or EGLL. "
                    + "Three-letter IATA codes such as SEA are not accepted, because there "
                    + "is no reliable way to expand them.");
                return;
            }
        }

        if (!TryReadMinutes(TrackTravelBox.Text, out int travelMinutes, out string? travelError))
        {
            ShowTrackError($"Travel time: {travelError}");
            return;
        }

        if (!TryReadMinutes(TrackWalkOutBox.Text, out int walkOutMinutes, out string? walkOutError))
        {
            ShowTrackError($"Landing to walk-out: {walkOutError}");
            return;
        }

        TrackError.IsOpen = false;

        _settings = _settings with
        {
            TrackedCallsign = callsign,
            TrackedDestinationIcao = destination,
            TrackedTravelMinutes = travelMinutes,
            TrackedPostLandingMinutes = walkOutMinutes,
        };

        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        await _tracking
            .StartAsync(new TrackedFlightRequest(callsign, destination, travelMinutes, walkOutMinutes))
            .ConfigureAwait(true);
    }

    private void OnStopTracking(object sender, RoutedEventArgs e) => Safe(StopTrackingAsync);

    private async Task StopTrackingAsync()
    {
        await _tracking.StopAsync().ConfigureAwait(true);

        _settings = _settings with { TrackedCallsign = null };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        TrackStatusCard.Visibility = Visibility.Collapsed;
        TrackDestinationIssue.IsOpen = false;
        TrackFeedIssue.IsOpen = false;
    }

    private void ShowTrackError(string message)
    {
        TrackError.Message = message;
        TrackError.IsOpen = true;
    }

    /// <summary>
    /// Parses a minutes field. Blank is zero, which the domain reads as "not
    /// configured" and answers with no advice rather than a guess.
    /// </summary>
    private static bool TryReadMinutes(string? text, out int minutes, out string? error)
    {
        minutes = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text, CultureInfo.CurrentCulture, out int parsed) || parsed < 0)
        {
            error = "enter a whole number of minutes, or leave it blank.";
            return false;
        }

        // A day is already absurd for either field, and a larger number is
        // almost certainly a typo that would silently distort the advice.
        if (parsed > 1440)
        {
            error = "that is more than a day. Check the number.";
            return false;
        }

        minutes = parsed;
        return true;
    }

    /// <summary>
    /// Renders a tracking update. Marshalled to the UI thread because the
    /// polling loop raises this from a background task.
    /// </summary>
    private void OnTrackedFlightChanged(object? sender, TrackedFlightState state)
    {
        if (!DispatcherQueue.TryEnqueue(() => RenderTrackedFlight(state)))
        {
            // Shutting down; there is no UI left to update.
        }
    }

    private void RenderTrackedFlight(TrackedFlightState state)
    {
        TrackStatusCard.Visibility = Visibility.Visible;
        TrackFlightLabel.Text = state.Destination is { } airport
            ? $"{state.Callsign} â†’ {airport.Name ?? airport.Icao}"
            : state.Callsign;

        TrackPhase.Text = FlightTracking.PhaseWord(state.Progress.Phase);
        TrackEta.Text = FlightTracking.FormatMinutesRemaining(state.Progress.MinutesRemaining);

        TrackDistance.Text = state.Progress.DistanceToDestinationKm is { } km
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{UnitConverter.DistanceFromKm(km, _settings.Units):N0} " +
                $"{UnitConverter.DistanceUnitLabel(_settings.Units)}")
            : "—";

        RenderDepartureAdvice(state);

        TrackDestinationLabel.Text = state.Destination is { } field
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Destination {field.Icao}, field elevation {field.ElevationFt:N0} ft. " +
                $"Landing is judged against the field, not sea level.")
            : string.Empty;

        // The staleness clock, in words. "Last seen 4 minutes ago" is what
        // stops a frozen position being read as a live one.
        TrackContactLabel.Text = state.LastContact is { } seen
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Last position report {state.Progress.SecondsSinceContact}s ago, at {seen.ToLocalTime():HH:mm:ss}.")
            : "No position report yet. Before pushback the transponder is off, so this is "
                + "normal — and it also looks exactly like a flight number that does not exist.";

        if (state.DestinationIssue is { } destinationIssue)
        {
            TrackDestinationIssue.Message = destinationIssue;
            TrackDestinationIssue.IsOpen = true;
        }
        else
        {
            TrackDestinationIssue.IsOpen = false;
        }

        if (state.FeedIssue is { } feedIssue)
        {
            TrackFeedIssue.Title = "Last lookup failed";
            TrackFeedIssue.Message =
                $"{feedIssue} The position shown is the last one received.";
            TrackFeedIssue.IsOpen = true;
        }
        else
        {
            TrackFeedIssue.IsOpen = false;
        }
    }

    private void RenderDepartureAdvice(TrackedFlightState state)
    {
        if (state.Departure.Advice == DepartureAdvice.Unknown)
        {
            // Nothing honest to say: either no ETA yet, or no travel time
            // configured. An empty card beats an invented countdown.
            TrackAdviceCard.Visibility = Visibility.Collapsed;
            return;
        }

        TrackAdviceCard.Visibility = Visibility.Visible;
        TrackAdvice.Text = FlightTracking.AdviceWord(state.Departure.Advice);

        int minutes = state.Departure.MinutesUntilDeparture ?? 0;

        // The value is signed on purpose, so "leave now" and "you are twenty
        // minutes late" do not render identically.
        TrackAdviceDetail.Text = minutes switch
        {
            > 0 => string.Create(
                CultureInfo.CurrentCulture,
                $"Leave in about {minutes} minutes."),
            0 => "Leave now to arrive as they walk out.",
            _ => string.Create(
                CultureInfo.CurrentCulture,
                $"You are about {-minutes} minutes past the ideal departure time."),
        };
    }

    /// <summary>
    /// Raises a toast when it is time to set off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two advice levels a user can act on. The service raises this on
    /// change rather than per poll, so an approach produces one "leave soon" and
    /// one "leave now" instead of one every ten seconds — a countdown that
    /// notified continuously would be muted long before it mattered.
    /// </para>
    /// <para>
    /// <see cref="DepartureAdvice.Late"/> is deliberately silent. By then the
    /// user either already left or already knows, and the domain stops
    /// escalating for the same reason.
    /// </para>
    /// </remarks>
    private void OnDepartureAdviceChanged(object? sender, TrackedFlightState state)
    {
        if (!_settings.NotificationsEnabled || _notifier is null)
        {
            return;
        }

        if (state.Departure.Advice is not (DepartureAdvice.LeaveSoon or DepartureAdvice.LeaveNow))
        {
            return;
        }

        int minutes = state.Departure.MinutesUntilDeparture ?? 0;

        string message = state.Departure.Advice == DepartureAdvice.LeaveNow
            ? $"{state.Callsign} arrives in about " +
                $"{FlightTracking.FormatMinutesRemaining(state.Progress.MinutesRemaining)} min. " +
                "Leave now."
            : $"{state.Callsign} — leave in about {minutes} minutes.";

        if (!DispatcherQueue.TryEnqueue(() =>
            _notifier?.Show(FlightTracking.AdviceWord(state.Departure.Advice), message)))
        {
            // Shutting down; a missed toast at that point is not worth reporting.
        }
    }

    /// <summary>
    /// Applies a zoom change from the radar and remembers it.
    /// </summary>
    /// <remarks>
    /// <b>No feed restart.</b> Zoom only changes what is drawn from data already
    /// in hand, so it takes effect on the next frame rather than after a poll,
    /// and costs the provider nothing. The save is fire-and-forget for the same
    /// reason: the user should not wait on a disk to see the plot redraw.
    /// </remarks>
    private async void OnRangeChangeRequested(object? sender, double rangeKm)
    {
        ViewModel.RangeKm = rangeKm;

        _settings = _settings with { DisplayRangeKm = rangeKm };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
    }

    private void OnAircraftSelected(object? sender, AircraftRowViewModel row)
        => ViewModel.SelectedAircraft = row;

    private void OnCloseDetail(object sender, RoutedEventArgs e)
        => ViewModel.SelectedAircraft = null;

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;

        try
        {
            // Stop producing before draining, so the queue cannot be refilled
            // while it is being flushed.
            await _feed.StopAsync().ConfigureAwait(true);
            await _tracking.StopAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Shutdown must not throw on the way out.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing useful left to report at this point.
        }

        Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // DisposeAsync on the recorder waits up to 5 seconds for its drain, so
        // this is bounded rather than able to hang shutdown on a slow disk.
        _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _recorder = null;

        // Closes the recording file cleanly, so a session that was being
        // recorded when the window closed still ends with a complete last frame.
        _sessionRecorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _sessionRecorder = null;

        _historyStore?.Dispose();
        _historyStore = null;

        _notifier?.Dispose();
        _notifier = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightBoardViewModel.SelectedAircraft))
        {
            LoadTrailForSelection();
        }

        // The compact panel is plain TextBlocks rather than bindings, so it is
        // refreshed whenever the status the user is watching changes.
        if (e.PropertyName is nameof(FlightBoardViewModel.StatusHeadline)
            or nameof(FlightBoardViewModel.StatusDetail))
        {
            RefreshCompact();
        }
    }

    /// <summary>
    /// Loads the selected aircraft's recorded track.
    /// </summary>
    /// <remarks>
    /// Read synchronously and deliberately: it is one indexed query bounded to
    /// 500 points against a local file, and hopping threads for it would let the
    /// selection change underneath the result and draw a trail belonging to a
    /// different aircraft.
    /// </remarks>
    private void LoadTrailForSelection()
    {
        if (_historyStore is null || ViewModel.SelectedAircraft is not { } selected)
        {
            ViewModel.SelectedTrail = [];
            return;
        }

        try
        {
            ViewModel.SelectedTrail = _historyStore.ReadTrail(
                selected.Aircraft.IcaoHex,
                DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        }
#pragma warning disable CA1031 // A missing trail must not break selection.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The detail pane and the live symbol remain correct without it.
            ViewModel.SelectedTrail = [];
        }
    }
}
