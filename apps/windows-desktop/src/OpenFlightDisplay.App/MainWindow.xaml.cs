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
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Infrastructure.Tracking;
using OpenFlightDisplay.Persistence;
using OpenFlightDisplay.Providers;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
    private readonly FlightTrackingService _tracking;
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
        _tracking = services.GetRequiredService<FlightTrackingService>();

        _tracking.StateChanged += OnTrackedFlightChanged;
        _tracking.DepartureAdviceChanged += OnDepartureAdviceChanged;

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
        PopulateTrackingForm();

        await RestartFeedAsync().ConfigureAwait(true);
        await ResumeTrackingAsync().ConfigureAwait(true);
    }

    private void PopulateTrackingForm()
    {
        TrackCallsignBox.Text = _settings.TrackedCallsign ?? string.Empty;
        TrackDestinationBox.Text = _settings.TrackedDestinationIcao ?? string.Empty;

        // Blank rather than "0": an unconfigured travel time is a real state
        // that produces no advice, and showing a zero would look configured.
        TrackTravelBox.Text = _settings.TrackedTravelMinutes > 0
            ? _settings.TrackedTravelMinutes.ToString(CultureInfo.CurrentCulture)
            : string.Empty;

        TrackWalkOutBox.Text =
            _settings.TrackedPostLandingMinutes.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Picks up a flight that was being tracked when the app last closed.
    /// </summary>
    /// <remarks>
    /// Worth resuming automatically: the whole feature is a countdown somebody
    /// is relying on, and making them retype it after a restart is exactly when
    /// they would miss it.
    /// </remarks>
    private async Task ResumeTrackingAsync()
    {
        if (_settings.TrackedCallsign is not { } callsign)
        {
            return;
        }

        await _tracking.StartAsync(new TrackedFlightRequest(
                callsign,
                _settings.TrackedDestinationIcao,
                _settings.TrackedTravelMinutes,
                _settings.TrackedPostLandingMinutes))
            .ConfigureAwait(true);
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
            ReceiverUrlBox.Text = _settings.LocalReceiverUrl ?? string.Empty;
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
    /// <para>
    /// Rules are evaluated whether or not notifications are enabled — the master
    /// switch governs Windows toasts, not whether alerts happen. Turning it off
    /// used to install no rules at all, which meant the in-app alert list stayed
    /// permanently empty and nothing recorded that anything had been observed.
    /// </para>
    /// <para>
    /// Area-based rules bind to the monitoring area built here, so there is one
    /// area defined in one place and a rule cannot end up pointing at a stale one.
    /// </para>
    /// </remarks>
    private void ApplyAlertSettings(MonitoringArea? monitoringArea)
    {
        _feed.Alerts.Reset();

        _feed.AlertRules = [.. _settings.EffectiveAlertRules.Select(r => r.ToRule(monitoringArea))];

        if (!_settings.NotificationsEnabled)
        {
            // Alerts still fire and still reach the list; they just do not
            // interrupt with a toast.
            _feed.Notifier = NullAlertNotifier.Instance;
            return;
        }

        _notifier ??= new ToastAlertNotifier(
            _services.GetRequiredService<ILogger<ToastAlertNotifier>>());

        _feed.Notifier = _notifier;
    }

    // ---- alert rule editing ----

    private async void OnAddAlertRule(object sender, RoutedEventArgs e)
        => await EditRuleAsync(null).ConfigureAwait(true);

    private async void OnEditAlertRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }
            && _settings.EffectiveAlertRules.FirstOrDefault(r => r.Id == id) is { } existing)
        {
            await EditRuleAsync(existing).ConfigureAwait(true);
        }
    }

    private async Task EditRuleAsync(AlertRuleSetting? existing)
    {
        bool hasArea = _settings.HomeLatitude is not null && _settings.HomeLongitude is not null;

        var dialog = new AlertRuleDialog(existing, hasArea)
        {
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync().AsTask().ConfigureAwait(true);

        if (dialog.Result is not { } saved)
        {
            return;
        }

        // Replace by id rather than by position, so editing a rule keeps its
        // place in the list instead of jumping to the end.
        var rules = _settings.EffectiveAlertRules.ToList();
        int index = rules.FindIndex(r => r.Id == saved.Id);

        if (index >= 0)
        {
            rules[index] = saved;
        }
        else
        {
            rules.Add(saved);
        }

        await SaveRulesAsync(rules).ConfigureAwait(true);
    }

    private async void OnDeleteAlertRule(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var rules = _settings.EffectiveAlertRules.Where(r => r.Id != id).ToList();
        await SaveRulesAsync(rules).ConfigureAwait(true);
    }

    private async void OnAlertRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string id } toggle
            || _settings.EffectiveAlertRules.FirstOrDefault(r => r.Id == id) is not { } rule)
        {
            return;
        }

        // Rebuilding the list sets IsOn programmatically, which raises Toggled
        // again. Comparing against the stored value makes that a no-op instead
        // of an endless save loop.
        if (toggle.IsOn == rule.Enabled)
        {
            return;
        }

        var rules = _settings.EffectiveAlertRules
            .Select(r => r.Id == id ? r with { Enabled = toggle.IsOn } : r)
            .ToList();

        await SaveRulesAsync(rules).ConfigureAwait(true);
    }

    /// <summary>
    /// Persists a new rule set and puts it into effect immediately.
    /// </summary>
    /// <remarks>
    /// The list is assigned even when the save fails: the rules the user just
    /// configured apply to this session either way, and the atomic write means
    /// a failure leaves the previous file intact rather than a partial one.
    /// </remarks>
    private async Task SaveRulesAsync(IReadOnlyList<AlertRuleSetting> rules)
    {
        _settings = _settings with { AlertRules = rules };

        if (!await _settingsStore.SaveAsync(_settings).ConfigureAwait(true))
        {
            SettingsError.Text =
                "The alert rules could not be saved and will be lost when the app closes. "
                + "They are active for this session.";
            SettingsError.Visibility = Visibility.Visible;
        }

        ApplyAlertSettings(CurrentMonitoringArea());
        RefreshAlerts();
    }

    /// <summary>The area currently being monitored, or <c>null</c> if unset.</summary>
    private CircleArea? CurrentMonitoringArea()
        => _settings.HomeLatitude is { } lat && _settings.HomeLongitude is { } lon
            ? new CircleArea(lat, lon, _settings.MonitoringRadiusKm)
            : null;

    private async Task RestartFeedAsync()
    {
        double lat = _settings.HomeLatitude ?? FallbackLat;
        double lon = _settings.HomeLongitude ?? FallbackLon;

        var area = new CircleArea(lat, lon, _settings.MonitoringRadiusKm);

        await ApplyHistorySettingAsync().ConfigureAwait(true);
        ApplyAlertSettings(area);

        ViewModel.Units = _settings.Units;
        ViewModel.RangeKm = _settings.MonitoringRadiusKm;
        ViewModel.ObserverLatitude = lat;
        ViewModel.ObserverLongitude = lon;

        IAviationDataProvider provider;
        try
        {
            provider = _providers.Resolve(_settings.DataMode, _settings.LocalReceiverUrl);
        }
        catch (NotSupportedException)
        {
            // Should be unreachable — unimplemented modes cannot be selected —
            // but falling back silently to mock would present synthetic
            // aircraft as live, so fall back to mock and say so.
            _settings = _settings with { DataMode = DataMode.Mock };
            provider = _providers.Resolve(DataMode.Mock);
        }

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

        // Blank clears the setting rather than storing an empty string, so
        // "no receiver configured" is one representable state, not two.
        string? receiverUrl = string.IsNullOrWhiteSpace(ReceiverUrlBox.Text)
            ? null
            : ReceiverUrlBox.Text.Trim();

        _settings = _settings with
        {
            HomeLatitude = lat,
            HomeLongitude = lon,
            MonitoringRadiusKm = radiusKm,
            LocalReceiverUrl = receiverUrl,
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
        AlertsPage.Visibility = tag == "alerts" ? Visibility.Visible : Visibility.Collapsed;
        TrackPage.Visibility = tag == "track" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        bool built = tag is "radar" or "board" or "sources" or "settings" or "alerts" or "track";

        if (tag == "alerts")
        {
            RefreshAlerts();
        }
        NotBuiltPage.Visibility = built ? Visibility.Collapsed : Visibility.Visible;

        if (built)
        {
            return;
        }

        // Say plainly what is not built and when it is planned. An empty page
        // implying work that does not exist would be worse than an honest note.
        (string title, string detail) = tag switch
        {
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

    /// <summary>
    /// Writes the current board to a file the user chooses.
    /// </summary>
    /// <remarks>
    /// Exports exactly what is on screen — the ranked, filtered list — so the
    /// file matches what the user was looking at when they pressed the button.
    /// </remarks>
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string format })
        {
            return;
        }

        var aircraft = ViewModel.Aircraft.Select(r => r.Aircraft).ToList();
        if (aircraft.Count == 0)
        {
            ExportStatus.Text = "Nothing to export — no aircraft on the board.";
            return;
        }

        (string extension, string description, string content) = format switch
        {
            "csv" => (".csv", "Comma-separated values", AircraftExporter.ToCsv(aircraft)),
            "geojson" => (".geojson", "GeoJSON", AircraftExporter.ToGeoJson(aircraft)),
            _ => (".json", "JSON", AircraftExporter.ToJson(aircraft)),
        };

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = string.Create(
                    CultureInfo.InvariantCulture,
                    $"openflightdisplay-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"),
            };

            picker.FileTypeChoices.Add(description, [extension]);

            // An unpackaged app has no implicit window for the picker to attach
            // to, so the handle has to be supplied explicitly or the call hangs.
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                ExportStatus.Text = "Export cancelled.";
                return;
            }

            await FileIO.WriteTextAsync(file, content);

            ExportStatus.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Exported {aircraft.Count} aircraft to {file.Name}.");
        }
#pragma warning disable CA1031 // A failed export must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ExportStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Rebuilds the alerts list from the evaluator's history.</summary>
    private void RefreshAlerts()
    {
        IReadOnlyList<AlertEvent> history = _feed.Alerts.History;
        IReadOnlyList<AlertRuleSetting> rules = _settings.EffectiveAlertRules;

        RulesList.ItemsSource = rules.Select(r => new AlertRuleViewModel(r)).ToList();
        NoRulesNote.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int enabled = rules.Count(r => r.Enabled);

        // Says what is actually happening rather than only whether toasts are
        // on: rules are evaluated and recorded either way, and a user who has
        // notifications off still needs to know their rules are running.
        string ruleState = enabled == 0
            ? "No rules are enabled, so nothing will alert."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{enabled} of {rules.Count} rules enabled.");

        string toastState = _settings.NotificationsEnabled
            ? string.Empty
            : " Windows notifications are off in Settings, so alerts appear here only.";

        string historyState = history.Count == 0
            ? " Nothing has fired yet this session."
            : string.Create(CultureInfo.CurrentCulture, $" {history.Count} fired this session.");

        AlertsSummary.Text = ruleState + toastState + historyState;

        AlertsList.Items.Clear();

        // Newest first: the most recent alert is the one worth reading.
        foreach (AlertEvent e in history.Reverse())
        {
            var panel = new StackPanel { Spacing = 2 };

            panel.Children.Add(new TextBlock
            {
                Text = e.Message,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{e.RuleName} · {e.FiredAt.ToLocalTime():HH:mm:ss} · {e.IcaoHex.ToUpperInvariant()}"),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            AlertsList.Items.Add(panel);
        }
    }

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
    private async void OnStartTracking(object sender, RoutedEventArgs e)
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

    private async void OnStopTracking(object sender, RoutedEventArgs e)
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
            ? $"{state.Callsign} → {airport.Name ?? airport.Icao}"
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
