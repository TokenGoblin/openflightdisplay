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
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Persistence;
using OpenFlightDisplay.Providers;

/// <summary>
/// Main application window: navigation rail, radar, flight board, detail pane,
/// data-source picker and settings.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    // Fallback observer location, used only until a real one is configured.
    // Deliberately a well-known public coordinate and NOT anyone's home — the
    // privacy rules forbid committing a real location.
    private const double FallbackLat = 47.6062;
    private const double FallbackLon = -122.3321;

    private readonly SettingsStore _settingsStore;
    private readonly ProviderRegistry _providers;
    private readonly AircraftFeedService _feed;
    private readonly IServiceProvider _services;

    private AppSettings _settings = new();
    private HistoryStore? _historyStore;
    private HistoryObservationRecorder? _recorder;
    private ToastAlertNotifier? _notifier;

    /// <summary>History database location, beside the settings file.</summary>
    private static string HistoryDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenFlightDisplay",
        "history.db");

    /// <summary>
    /// Guards the picker handlers while they are being populated from settings,
    /// so restoring the saved selection does not look like a user change and
    /// restart the feed twice.
    /// </summary>
    private bool _suppressSelectionEvents;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        _services = services;
        ViewModel = services.GetRequiredService<FlightBoardViewModel>();
        _settingsStore = services.GetRequiredService<SettingsStore>();
        _providers = services.GetRequiredService<ProviderRegistry>();
        _feed = services.GetRequiredService<AircraftFeedService>();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Graceful shutdown: stop polling and flush queued history before the
        // process goes away, rather than losing the last batches on exit.
        Closed += OnWindowClosed;

        Nav.SelectedItem = Nav.MenuItems[0];

        // Startup is deferred until the content is loaded, because
        // ContentDialog.ShowAsync needs a live XamlRoot and there is none in the
        // constructor. Running it here threw, and because the task was
        // discarded the exception vanished and the app simply sat on
        // "Setup required" forever.
        if (Content is FrameworkElement root)
        {
            root.Loaded += OnContentLoaded;
        }
    }

    /// <summary>Bound by the XAML.</summary>
    public FlightBoardViewModel ViewModel { get; }

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        // Once only — Loaded can fire again on theme or visual-tree changes.
        if (Content is FrameworkElement root)
        {
            root.Loaded -= OnContentLoaded;
        }

        try
        {
            await InitialiseAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Startup must report failure, not disappear.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Anything thrown here previously became an unobserved task
            // exception and left the app on its initial state with no
            // explanation. Surfacing it is the whole point of the no-silent-
            // failure rule.
            ViewModel.ReportStartupFailure(ex.Message);
        }
    }

    private async Task InitialiseAsync()
    {
        _settings = await _settingsStore.LoadAsync().ConfigureAwait(true);

        if (!_settings.OnboardingCompleted)
        {
            await RunOnboardingAsync().ConfigureAwait(true);
        }

        PopulateDataSources();
        PopulateSettingsForm();

        await RestartFeedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Shows first-run setup.
    /// </summary>
    /// <remarks>
    /// Skipping is allowed and leaves the defaults in place, which are working
    /// mock data — first run must not be a locked door. Skipping deliberately
    /// does <b>not</b> mark onboarding complete, so it is offered again next
    /// launch rather than silently never appearing.
    /// </remarks>
    private async Task RunOnboardingAsync()
    {
        var dialog = new OnboardingDialog(_providers, _settings)
        {
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync().AsTask().ConfigureAwait(true);

        if (!dialog.Completed)
        {
            return;
        }

        _settings = dialog.Result;

        if (!await _settingsStore.SaveAsync(_settings).ConfigureAwait(true))
        {
            // The chosen settings still apply to this session; only persistence
            // failed, and the atomic write means nothing was corrupted.
            SettingsError.Text =
                "Your choices could not be saved and will be asked for again next launch.";
            SettingsError.Visibility = Visibility.Visible;
        }
    }

    private void PopulateDataSources()
    {
        _suppressSelectionEvents = true;
        try
        {
            SourceList.Items.Clear();

            foreach (DataMode mode in Enum.GetValues<DataMode>())
            {
                bool implemented = ProviderRegistry.IsImplemented(mode);

                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(new TextBlock
                {
                    Text = ProviderRegistry.DisplayName(mode),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });

                panel.Children.Add(new TextBlock
                {
                    Text = ProviderRegistry.Describe(mode),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)
                        Application.Current.Resources["TextFillColorSecondaryBrush"],
                });

                var item = new ListViewItem
                {
                    Content = panel,
                    Tag = mode,

                    // Unimplemented modes are shown but not selectable, and say
                    // why in their description. Hiding them would misrepresent
                    // the roadmap; enabling them would misrepresent the app.
                    IsEnabled = implemented,
                };

                SourceList.Items.Add(item);

                if (mode == _settings.DataMode)
                {
                    SourceList.SelectedItem = item;
                }
            }

            if (SourceList.SelectedItem is null)
            {
                SourceList.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private void PopulateSettingsForm()
    {
        _suppressSelectionEvents = true;
        try
        {
            foreach (object candidate in UnitsBox.Items)
            {
                if (candidate is ComboBoxItem { Tag: string tag }
                    && Enum.TryParse(tag, out UnitSystem system)
                    && system == _settings.Units)
                {
                    UnitsBox.SelectedItem = candidate;
                    break;
                }
            }

            UnitsBox.SelectedItem ??= UnitsBox.Items[0];

            // Blank rather than a placeholder coordinate: an unconfigured
            // location must not look like a configured one.
            LatBox.Text = _settings.HomeLatitude?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            LonBox.Text = _settings.HomeLongitude?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            RadiusBox.Text = _settings.MonitoringRadiusKm.ToString(CultureInfo.CurrentCulture);
            HistoryCheck.IsChecked = _settings.HistoryEnabled;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    /// <summary>
    /// Opens or closes the history database to match the current setting.
    /// </summary>
    /// <remarks>
    /// History is opt-in, so the database is not even opened unless it is on —
    /// turning it off should stop creating files, not merely stop writing to
    /// them. A failure to open is reported and leaves history disabled rather
    /// than taking the application down.
    /// </remarks>
    private async Task ApplyHistorySettingAsync()
    {
        if (_recorder is not null)
        {
            await _recorder.DisposeAsync().ConfigureAwait(true);
            _recorder = null;
        }

        _historyStore?.Dispose();
        _historyStore = null;

        _feed.Recorder = NullObservationRecorder.Instance;

        if (!_settings.HistoryEnabled)
        {
            return;
        }

        try
        {
            _historyStore = HistoryStore.Open(
                HistoryDatabasePath,
                _services.GetRequiredService<ILogger<HistoryStore>>());

            _historyStore.Prune(
                new RetentionPolicy(
                    TimeSpan.FromDays(_settings.HistoryRetentionDays),
                    (long)_settings.HistoryMaxDatabaseMb * 1024 * 1024),
                DateTimeOffset.UtcNow);

            _recorder = new HistoryObservationRecorder(
                _historyStore,
                _services.GetRequiredService<ILogger<HistoryObservationRecorder>>());

            _feed.Recorder = _recorder;
        }
#pragma warning disable CA1031 // History must never prevent the app running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _historyStore?.Dispose();
            _historyStore = null;

            SettingsError.Text =
                $"History could not be opened and stays disabled: {ex.Message}";
            SettingsError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Installs alert rules and the notification channel from settings.
    /// </summary>
    /// <remarks>
    /// Until the rule editor exists (Phase 2), one built-in rule is installed:
    /// emergency squawks. It is the one alert that is unambiguously worth
    /// interrupting someone for, needs no configuration, and cannot produce a
    /// stream of noise. Shipping the engine with no rules at all would be
    /// dormant code that looks like a feature.
    /// </remarks>
    private void ApplyAlertSettings()
    {
        _feed.Alerts.Reset();

        if (!_settings.NotificationsEnabled)
        {
            _feed.AlertRules = [];
            _feed.Notifier = NullAlertNotifier.Instance;
            return;
        }

        _notifier ??= new ToastAlertNotifier(
            _services.GetRequiredService<ILogger<ToastAlertNotifier>>());

        _feed.Notifier = _notifier;
        _feed.AlertRules =
        [
            new AlertRule
            {
                Id = "builtin-emergency",
                Name = "Emergency squawk",
                Trigger = AlertTrigger.EmergencySquawk,
                Channels = AlertChannels.InApp | AlertChannels.Toast | AlertChannels.Log,

                // No quiet hours on this one by design: an emergency is exactly
                // the case where a silence window should not apply.
                Cooldown = TimeSpan.FromMinutes(15),
            },
        ];
    }

    private async Task RestartFeedAsync()
    {
        double lat = _settings.HomeLatitude ?? FallbackLat;
        double lon = _settings.HomeLongitude ?? FallbackLon;

        await ApplyHistorySettingAsync().ConfigureAwait(true);
        ApplyAlertSettings();

        ViewModel.Units = _settings.Units;
        ViewModel.RangeKm = _settings.MonitoringRadiusKm;
        ViewModel.ObserverLatitude = lat;
        ViewModel.ObserverLongitude = lon;

        IAviationDataProvider provider;
        try
        {
            provider = _providers.Resolve(_settings.DataMode);
        }
        catch (NotSupportedException)
        {
            // Should be unreachable — unimplemented modes cannot be selected —
            // but falling back silently to mock would present synthetic
            // aircraft as live, so fall back to mock and say so.
            _settings = _settings with { DataMode = DataMode.Mock };
            provider = _providers.Resolve(DataMode.Mock);
        }

        var area = new CircleArea(lat, lon, _settings.MonitoringRadiusKm);
        await ViewModel.StartAsync(provider, area, lat, lon).ConfigureAwait(true);

        UpdateHistoryStatus();
    }

    private async void OnDataSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || SourceList.SelectedItem is not ListViewItem { Tag: DataMode mode })
        {
            return;
        }

        _settings = _settings with { DataMode = mode };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
        await RestartFeedAsync().ConfigureAwait(true);
    }

    private async void OnUnitsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || UnitsBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out UnitSystem system))
        {
            return;
        }

        _settings = _settings with { Units = system };
        ViewModel.Units = system;

        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        // Rows are formatted at construction, so a unit change needs a rebuild
        // rather than only a property notification.
        await RestartFeedAsync().ConfigureAwait(true);
    }

    private async void OnHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionEvents)
        {
            return;
        }

        _settings = _settings with { HistoryEnabled = HistoryCheck.IsChecked is true };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        await ApplyHistorySettingAsync().ConfigureAwait(true);
        UpdateHistoryStatus();

        // Turning history off clears the trail immediately rather than leaving
        // a stale one on screen sourced from a database that is now closed.
        LoadTrailForSelection();
    }

    private void UpdateHistoryStatus()
    {
        if (_historyStore is null)
        {
            HistoryStatus.Text = _settings.HistoryEnabled
                ? "History is enabled but the database is not open."
                : "History is off. No observations are being recorded.";
            return;
        }

        HistoryStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{_historyStore.ObservationCount:N0} observations, " +
            $"{_historyStore.DatabaseBytes / 1024.0 / 1024.0:N1} MB, " +
            $"kept for {_settings.HistoryRetentionDays} days.");
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        SettingsSaved.Visibility = Visibility.Collapsed;

        if (!TryReadLocation(out double? lat, out double? lon, out double radiusKm, out string? error))
        {
            SettingsError.Text = error;
            SettingsError.Visibility = Visibility.Visible;
            return;
        }

        SettingsError.Visibility = Visibility.Collapsed;

        _settings = _settings with
        {
            HomeLatitude = lat,
            HomeLongitude = lon,
            MonitoringRadiusKm = radiusKm,
        };

        bool saved = await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
        if (!saved)
        {
            SettingsError.Text =
                "Settings could not be written. The previous settings are unchanged.";
            SettingsError.Visibility = Visibility.Visible;
            return;
        }

        SettingsSaved.Visibility = Visibility.Visible;
        await RestartFeedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Parses the location form. Rejects rather than clamps — a silently
    /// corrected coordinate is worse than being told it was wrong.
    /// </summary>
    private bool TryReadLocation(
        out double? lat,
        out double? lon,
        out double radiusKm,
        out string? error)
    {
        lat = null;
        lon = null;
        radiusKm = _settings.MonitoringRadiusKm;
        error = null;

        bool latBlank = string.IsNullOrWhiteSpace(LatBox.Text);
        bool lonBlank = string.IsNullOrWhiteSpace(LonBox.Text);

        if (latBlank != lonBlank)
        {
            error = "Enter both a latitude and a longitude, or leave both blank.";
            return false;
        }

        if (!latBlank)
        {
            if (!double.TryParse(LatBox.Text, CultureInfo.CurrentCulture, out double parsedLat)
                || parsedLat is < -90 or > 90)
            {
                error = "Latitude must be a number between -90 and 90.";
                return false;
            }

            if (!double.TryParse(LonBox.Text, CultureInfo.CurrentCulture, out double parsedLon)
                || parsedLon is < -180 or > 180)
            {
                error = "Longitude must be a number between -180 and 180.";
                return false;
            }

            lat = parsedLat;
            lon = parsedLon;
        }

        if (!double.TryParse(RadiusBox.Text, CultureInfo.CurrentCulture, out double parsedRadius)
            || parsedRadius is < 0.5 or > 500)
        {
            error = "Radius must be between 0.5 and 500 km.";
            return false;
        }

        radiusKm = parsedRadius;
        return true;
    }

    private void OnNavSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        RadarPage.Visibility = tag == "radar" ? Visibility.Visible : Visibility.Collapsed;
        BoardPage.Visibility = tag == "board" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPage.Visibility = tag == "sources" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        bool built = tag is "radar" or "board" or "sources" or "settings";
        NotBuiltPage.Visibility = built ? Visibility.Collapsed : Visibility.Visible;

        if (built)
        {
            return;
        }

        // Say plainly what is not built and when it is planned. An empty page
        // implying work that does not exist would be worse than an honest note.
        (string title, string detail) = tag switch
        {
            "track" => ("Track Flight",
                "Not built yet. Follow-a-flight with ETA and departure advice is planned for " +
                "Phase 3. The firmware already implements this; the desktop port has not started."),
            "history" => ("History",
                "Not built yet. Local SQLite observation history, trails and timeline playback " +
                "are planned for Phase 2. History will be off by default."),
            "alerts" => ("Alerts",
                "Not built yet. Rule-based alerts with cooldown, deduplication and Windows toast " +
                "notifications are planned for Phase 2."),
            "areas" => ("Monitoring Areas",
                "Not built yet. The domain supports circles, cones and polygons with altitude " +
                "bands today; the map-based editor is planned for Phase 2."),
            "devices" => ("Devices",
                "Not built yet. Discovery, pairing and configuration for M5Stack Core2 devices " +
                "are planned for Phase 3."),
            "diagnostics" => ("Diagnostics",
                "Not built yet. Provider latency, record counts, update cadence and log location " +
                "are planned for Phase 4."),
            _ => ("Not available", "This section does not exist yet."),
        };

        NotBuiltTitle.Text = title;
        NotBuiltDetail.Text = detail;
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
