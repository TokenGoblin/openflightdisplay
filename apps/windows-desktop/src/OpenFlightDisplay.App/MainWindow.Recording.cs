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
/// Session recording and replay playback.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- session recording and replay ----

    /// <summary>Where recordings are written, beside the settings file.</summary>
    private static string RecordingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenFlightDisplay",
        "recordings");

    private async void OnToggleRecording(object sender, RoutedEventArgs e)
    {
        if (_sessionRecorder is not null)
        {
            string path = _sessionRecorder.Path;
            int frames = _sessionRecorder.FrameCount;
            long dropped = _sessionRecorder.DroppedBatches;

            await _sessionRecorder.DisposeAsync().ConfigureAwait(true);
            _sessionRecorder = null;
            ApplyRecorders();

            RecordButton.Content = "Start recording";
            RecordingStatus.Text = dropped == 0
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"Saved {frames:N0} frames to {path}.")
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Saved {frames:N0} frames to {path}. {dropped:N0} batches were dropped "
                    + $"because the disk could not keep up, so the recording has gaps.");
            return;
        }

        if (_feed.ActiveProvider is not { } provider)
        {
            RecordingStatus.Text = "Start a data source before recording.";
            return;
        }

        // Recording a replay is pointless and confusing, so it is refused
        // rather than producing a copy of a file the user already has.
        if (provider.Id == "replay")
        {
            RecordingStatus.Text = "A replay cannot be recorded. Switch to a live source first.";
            return;
        }

        try
        {
            string path = Path.Combine(
                RecordingsDirectory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{provider.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}{ReplayFile.Extension}"));

            ReplayRecorder writer = await ReplayRecorder
                .StartAsync(path, provider.Id, DateTimeOffset.UtcNow)
                .ConfigureAwait(true);

            _sessionRecorder = new SessionReplayRecorder(
                writer,
                _services.GetRequiredService<ILogger<SessionReplayRecorder>>());

            ApplyRecorders();

            RecordButton.Content = "Stop recording";
            RecordingStatus.Text = $"Recording to {path}.";
        }
#pragma warning disable CA1031 // A failed recording must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RecordingStatus.Text = $"Recording could not be started: {ex.Message}";
            _sessionRecorder = null;
        }
    }

    /// <summary>
    /// Points the feed at whichever recorders are currently active.
    /// </summary>
    /// <remarks>
    /// History and session recording are independent — capturing a session to
    /// reproduce a bug should not mean giving up the history database — so both
    /// can be attached at once.
    /// </remarks>
    private void ApplyRecorders()
    {
        IObservationRecorder history =
            (IObservationRecorder?)_recorder ?? NullObservationRecorder.Instance;

        _feed.Recorder = _sessionRecorder is null
            ? history
            : new CompositeObservationRecorder(history, _sessionRecorder);
    }

    /// <summary>Opens a recording and switches the feed to replaying it.</summary>
    private void OnLoadRecording(object sender, RoutedEventArgs e) => Safe(LoadRecordingAsync);

    private async Task LoadRecordingAsync()
    {
        string? path = await PickRecordingAsync().ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        ReplayLoadResult result = await ReplayFile.LoadAsync(path).ConfigureAwait(true);

        if (result is ReplayLoadResult.Failed failure)
        {
            RecordingStatus.Text = failure.Detail;
            return;
        }

        var loaded = (ReplayLoadResult.Loaded)result;
        _providers.LoadedRecording = loaded.Recording;

        RecordingStatus.Text = loaded.SkippedLines == 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Loaded {loaded.Recording.Name}: {loaded.Recording.Frames.Count:N0} frames "
                + $"recorded from {loaded.Recording.ProviderId} on "
                + $"{loaded.Recording.RecordedAt.ToLocalTime():dd MMM yyyy HH:mm}.")

            // Said plainly rather than quietly handing over a short recording.
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Loaded {loaded.Recording.Name}: {loaded.Recording.Frames.Count:N0} frames. "
                + $"{loaded.SkippedLines:N0} damaged lines were skipped, which usually means "
                + $"the recording session ended abruptly.");

        // Switch to replay so loading a file does what the user plainly meant.
        _settings = _settings with { DataMode = DataMode.Replay };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        PopulateDataSources();
        await RestartFeedAsync().ConfigureAwait(true);
    }

    private async Task<string?> PickRecordingAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            picker.FileTypeFilter.Add(ReplayFile.Extension);

            // Same unpackaged-window requirement as the save picker.
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
#pragma warning disable CA1031 // A failed pick must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RecordingStatus.Text = $"That file could not be opened: {ex.Message}";
            return null;
        }
    }

}
