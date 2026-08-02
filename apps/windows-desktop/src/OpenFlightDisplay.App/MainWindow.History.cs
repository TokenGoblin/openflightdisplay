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
/// Browsing recorded observations.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- History ----

    /// <summary>Period covered by the history list, from the picker.</summary>
    private TimeSpan HistoryRange =>
        HistoryRangeBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, CultureInfo.InvariantCulture, out int hours)
            ? TimeSpan.FromHours(hours)
            : TimeSpan.FromHours(24);

    private void OnHistoryRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        // HistoryList is the last of this page's controls to be created, so a
        // null here means the visual tree is still being built and there is
        // nothing to refresh yet.
        if (!_suppressSelectionEvents && HistoryList is not null)
        {
            RefreshHistory();
        }
    }

    private void OnRefreshHistory(object sender, RoutedEventArgs e) => RefreshHistory();

    /// <summary>
    /// Rebuilds the history list from the database.
    /// </summary>
    /// <remarks>
    /// Says which of the three "nothing to show" cases applies. They need
    /// different actions from the user — turn history on, wait for data, or
    /// widen the period — and an identical empty list for all three would leave
    /// them guessing which.
    /// </remarks>
    private void RefreshHistory()
    {
        if (_historyStore is null)
        {
            HistorySummary.Text = _settings.HistoryEnabled
                ? "History is enabled but the database could not be opened."
                : "History is off, so nothing is being recorded. Turn it on in Settings. "
                    + "It is off by default because it keeps a record of everything that "
                    + "flies over you.";

            HistoryList.ItemsSource = null;
            HistoryStatusLine.Text = string.Empty;
            return;
        }

        try
        {
            DateTimeOffset since = DateTimeOffset.UtcNow - HistoryRange;
            IReadOnlyList<AircraftSummary> summaries = _historyStore.ReadMostObserved(since, limit: 200);

            HistoryList.ItemsSource = summaries.Select(s => new HistoryRowViewModel(s)).ToList();

            HistorySummary.Text = summaries.Count == 0
                ? _historyStore.ObservationCount == 0
                    ? "Nothing recorded yet. Observations appear here as aircraft are seen."
                    : "Nothing in this period. Try a longer one."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{summaries.Count:N0} aircraft in this period, most-seen first.");

            HistoryStatusLine.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Database holds {_historyStore.ObservationCount:N0} observations, " +
                $"{_historyStore.DatabaseBytes / 1024.0 / 1024.0:N1} MB, kept for " +
                $"{_settings.HistoryRetentionDays} days. Select a row to draw its trail on the radar.");
        }
#pragma warning disable CA1031 // A failed read must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HistorySummary.Text = $"History could not be read: {ex.Message}";
            HistoryList.ItemsSource = null;
        }
    }

    /// <summary>
    /// Draws the selected aircraft's recorded track on the radar.
    /// </summary>
    /// <remarks>
    /// The trail is bound to the radar's selected-aircraft trail, so choosing a
    /// row here shows where it went even though the aircraft is long gone from
    /// the live feed.
    /// </remarks>
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_historyStore is null
            || HistoryList.SelectedItem is not HistoryRowViewModel row)
        {
            return;
        }

        try
        {
            ViewModel.SelectedTrail = _historyStore.ReadTrail(
                row.IcaoHex.ToLowerInvariant(),
                DateTimeOffset.UtcNow - HistoryRange);

            HistoryStatusLine.Text = ViewModel.SelectedTrail.Count == 0
                ? $"{row.Callsign} has no positions in this period."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{row.Callsign}: {ViewModel.SelectedTrail.Count:N0} positions drawn on the radar.");
        }
#pragma warning disable CA1031 // A missing trail must not break selection.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HistoryStatusLine.Text = $"That trail could not be read: {ex.Message}";
        }
    }

    /// <summary>Writes one aircraft's recorded track as GeoJSON.</summary>
    private async void OnExportTrail(object sender, RoutedEventArgs e)
    {
        if (_historyStore is null || sender is not FrameworkElement { Tag: string hex })
        {
            return;
        }

        IReadOnlyList<TrailPoint> trail;
        try
        {
            trail = _historyStore.ReadTrail(hex.ToLowerInvariant(), DateTimeOffset.UtcNow - HistoryRange);
        }
#pragma warning disable CA1031 // A failed read must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HistoryStatusLine.Text = $"That trail could not be read: {ex.Message}";
            return;
        }

        if (trail.Count == 0)
        {
            HistoryStatusLine.Text = "That aircraft has no recorded positions in this period.";
            return;
        }

        string content = AircraftExporter.TrailToGeoJson(
            hex.ToLowerInvariant(),
            null,
            trail.Select(p => (p.Latitude, p.Longitude, p.AltitudeFt)));

        await SaveTextAsync(content, ".geojson", "GeoJSON", $"trail-{hex.ToLowerInvariant()}")
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Erases the whole history database.
    /// </summary>
    /// <remarks>
    /// Confirmed first and irreversible, so it says so. History is opt-in
    /// because of what it records, which is the same reason deleting it has to
    /// be possible — switching recording off only stops new rows.
    /// </remarks>
    private void OnDeleteHistory(object sender, RoutedEventArgs e)
        => Safe(DeleteHistoryAsync);

    private async Task DeleteHistoryAsync()
    {
        if (_historyStore is null)
        {
            HistoryStatusLine.Text = "There is no open history database to delete.";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete all history?",
            Content = string.Create(
                CultureInfo.CurrentCulture,
                $"This permanently deletes all {_historyStore.ObservationCount:N0} recorded " +
                $"observations and cannot be undone. Recording stays on."),
            PrimaryButtonText = "Delete everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            int deleted = _historyStore.DeleteAll();

            // The on-screen trail came from rows that no longer exist.
            ViewModel.SelectedTrail = [];

            HistoryStatusLine.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Deleted {deleted:N0} observations.");
        }
#pragma warning disable CA1031 // A failed delete must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HistoryStatusLine.Text = $"History could not be deleted: {ex.Message}";
            return;
        }

        RefreshHistory();
        UpdateHistoryStatus();
    }

}
