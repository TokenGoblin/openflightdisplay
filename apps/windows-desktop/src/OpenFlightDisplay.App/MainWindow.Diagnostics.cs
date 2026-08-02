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
/// The diagnostics readout.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- diagnostics ----

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    /// <summary>
    /// Rebuilds the diagnostics readout.
    /// </summary>
    /// <remarks>
    /// Counters that are supposed to be zero — dropped history batches, dropped
    /// replay frames — are surfaced in a warning bar rather than left as one
    /// number among many. A silent drop is exactly the kind of failure this
    /// project's no-silent-failure rule exists to catch, and it is no use
    /// recording one if nothing ever says so.
    /// </remarks>
    private void RefreshDiagnostics()
    {
        var c = CultureInfo.CurrentCulture;

        DiagFeed.Text = string.Create(c,
            $"Provider      : {_feed.ActiveProvider?.Id ?? "none"}\n" +
            $"State         : {_feed.CurrentState.GetType().Name}\n" +
            $"On the board  : {ViewModel.Aircraft.Count:N0} aircraft\n" +
            $"Ranking       : {_settings.RankingMode}\n" +
            $"Filter        : {_settings.Filter.Summarise()}\n" +
            $"Radius        : {_settings.MonitoringRadiusKm:N1} km");

        DiagHistory.Text = _historyStore is null
            ? _settings.HistoryEnabled
                ? "Enabled, but the database is not open."
                : "Off. Nothing is being recorded."
            : string.Create(c,
                $"Observations  : {_historyStore.ObservationCount:N0}\n" +
                $"Database      : {_historyStore.DatabaseBytes / 1024.0 / 1024.0:N1} MB " +
                    $"(limit {_settings.HistoryMaxDatabaseMb:N0} MB)\n" +
                $"Schema        : v{_historyStore.SchemaVersion}\n" +
                $"Retention     : {_settings.HistoryRetentionDays} days\n" +
                $"Written       : {_recorder?.WrittenObservations ?? 0:N0}\n" +
                $"Queue depth   : {_recorder?.QueueDepth ?? 0:N0}\n" +
                $"Dropped       : {_recorder?.DroppedBatches ?? 0:N0} batches");

        IReadOnlyList<AlertRuleSetting> rules = _settings.EffectiveAlertRules;
        DiagAlerts.Text = string.Create(c,
            $"Rules         : {rules.Count(r => r.Enabled):N0} enabled of {rules.Count:N0}\n" +
            $"Fired         : {_feed.Alerts.History.Count:N0} this session\n" +
            $"Per-poll cap  : {AlertEvaluator.MaxEventsPerPoll}\n" +
            $"Toasts        : {(_settings.NotificationsEnabled ? "on" : "off")}");

        DiagTracking.Text = _tracking.Tracked is { } tracked
            ? string.Create(c,
                $"Following     : {tracked.Callsign}\n" +
                $"Destination   : {tracked.DestinationIcao ?? "none"}\n" +
                $"Phase         : {FlightTracking.PhaseWord(_tracking.CurrentState?.Progress.Phase ?? FlightPhase.AwaitingContact)}\n" +
                $"Last contact  : {_tracking.CurrentState?.Progress.SecondsSinceContact ?? 0}s ago")
            : "Not tracking a flight.";

        DiagRecording.Text = _sessionRecorder is null
            ? "Not recording."
            : string.Create(c,
                $"Writing to    : {_sessionRecorder.Path}\n" +
                $"Frames        : {_sessionRecorder.FrameCount:N0}\n" +
                $"Dropped       : {_sessionRecorder.DroppedBatches:N0} batches");

        DiagEnvironment.Text = string.Create(c,
            $"App version   : {typeof(MainWindow).Assembly.GetName().Version}\n" +
            $".NET          : {Environment.Version}\n" +
            $"OS            : {Environment.OSVersion.VersionString}\n" +
            $"Architecture  : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
            $"Working set   : {Environment.WorkingSet / 1024.0 / 1024.0:N0} MB\n" +
            $"Settings      : {SettingsStore.DefaultFilePath}\n" +
            $"History DB    : {HistoryDatabasePath}\n" +
            $"Recordings    : {RecordingsDirectory}");

        long droppedHistory = _recorder?.DroppedBatches ?? 0;
        long droppedFrames = _sessionRecorder?.DroppedBatches ?? 0;

        if (droppedHistory > 0 || droppedFrames > 0)
        {
            DiagnosticsWarning.Title = "Data has been dropped";
            DiagnosticsWarning.Message = string.Create(c,
                $"{droppedHistory:N0} history batches and {droppedFrames:N0} replay frames were " +
                $"dropped because writing could not keep up. History has gaps and any recording " +
                $"is incomplete. This usually means a slow or full disk.");
            DiagnosticsWarning.IsOpen = true;
        }
        else
        {
            DiagnosticsWarning.IsOpen = false;
        }
    }

    /// <summary>
    /// Copies the readout for pasting into a bug report.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes the home coordinates and every callsign. A
    /// diagnostics dump is the thing people paste into public issue trackers,
    /// and this application's privacy rules do not stop applying because the
    /// user pressed a button.
    /// </remarks>
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();

        string dump = string.Join(
            "\n\n",
            "== Feed ==\n" + DiagFeed.Text,
            "== History ==\n" + DiagHistory.Text,
            "== Alerts ==\n" + DiagAlerts.Text,
            "== Flight tracking ==\n" + DiagTracking.Text,
            "== Recording ==\n" + DiagRecording.Text,
            "== Environment ==\n" + DiagEnvironment.Text);

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(dump);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            DiagCopyStatus.Text = "Copied. It contains no coordinates and no callsigns.";
        }
#pragma warning disable CA1031 // A clipboard failure must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DiagCopyStatus.Text = $"Could not copy: {ex.Message}";
        }
    }

}
