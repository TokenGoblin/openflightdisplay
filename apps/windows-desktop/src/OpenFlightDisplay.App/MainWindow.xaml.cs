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
    private SessionReplayRecorder? _sessionRecorder;
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

        // Default the history period here rather than in markup, and with events
        // suppressed: a selection made during InitializeComponent would call the
        // handler before the rest of the page exists.
        _suppressSelectionEvents = true;
        HistoryRangeBox.SelectedIndex = 1;
        _suppressSelectionEvents = false;

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
        PopulateAreaForm();
        ApplyMapSetting();
        UpdateMapCacheStatus();

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

            foreach (object candidate in RankingBox.Items)
            {
                if (candidate is ComboBoxItem { Tag: string mode }
                    && Enum.TryParse(mode, out RankingMode ranking)
                    && ranking == _settings.RankingMode)
                {
                    RankingBox.SelectedItem = candidate;
                    break;
                }
            }

            RankingBox.SelectedItem ??= RankingBox.Items[0];

            AircraftFilter filter = _settings.Filter;
            MinAltBox.Text = filter.MinAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            MaxAltBox.Text = filter.MaxAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            AirborneOnlyCheck.IsChecked = filter.ExcludeOnGround;
            RequireCallsignCheck.IsChecked = filter.RequireCallsign;
            EmergencyOnlyCheck.IsChecked = filter.EmergencyOnly;

            // Blank rather than a placeholder coordinate: an unconfigured
            // location must not look like a configured one.
            LatBox.Text = _settings.HomeLatitude?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            LonBox.Text = _settings.HomeLongitude?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            RadiusBox.Text = _settings.MonitoringRadiusKm.ToString(CultureInfo.CurrentCulture);
            HistoryCheck.IsChecked = _settings.HistoryEnabled;
            MapCheck.IsChecked = _settings.MapOverlayEnabled;
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

        // Recomputed rather than cleared: a session recording is independent of
        // history and must survive history being turned off.
        ApplyRecorders();

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

            ApplyRecorders();
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
    private MonitoringArea? CurrentMonitoringArea()
        => _settings.MonitoringArea.Build(_settings.HomeLatitude, _settings.HomeLongitude);

    private async Task RestartFeedAsync()
    {
        double lat = _settings.HomeLatitude ?? FallbackLat;
        double lon = _settings.HomeLongitude ?? FallbackLon;

        // The configured shape, falling back to the plain radius circle if it
        // cannot be built — an unusable area must not stop the feed starting.
        MonitoringArea area =
            _settings.MonitoringArea.Build(_settings.HomeLatitude, _settings.HomeLongitude)
            ?? new CircleArea(lat, lon, _settings.MonitoringRadiusKm);

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

        _feed.Filter = _settings.Filter;
        ViewModel.Filter = _settings.Filter;

        await ViewModel.StartAsync(provider, area, lat, lon, _settings.RankingMode)
            .ConfigureAwait(true);

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

    private async void OnRankingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || RankingBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out RankingMode mode))
        {
            return;
        }

        _settings = _settings with { RankingMode = mode };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        // Ranking is chosen when the feed starts, so this needs a restart rather
        // than only a property change.
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

        if (!TryReadFilter(out AircraftFilter filter, out string? filterError))
        {
            SettingsError.Text = filterError;
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
            Filter = filter,
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
    /// Parses the display filter from the settings form.
    /// </summary>
    /// <remarks>
    /// A blank altitude box means "no limit", which is why the fields are
    /// nullable rather than defaulted — zero feet is a real altitude.
    /// </remarks>
    private bool TryReadFilter(out AircraftFilter filter, out string? error)
    {
        filter = AircraftFilter.None;
        error = null;

        if (!TryReadOptionalAltitude(MinAltBox.Text, out double? min))
        {
            error = "The 'above' altitude must be a number in feet, or blank.";
            return false;
        }

        if (!TryReadOptionalAltitude(MaxAltBox.Text, out double? max))
        {
            error = "The 'below' altitude must be a number in feet, or blank.";
            return false;
        }

        var candidate = new AircraftFilter
        {
            MinAltitudeFt = min,
            MaxAltitudeFt = max,
            ExcludeOnGround = AirborneOnlyCheck.IsChecked is true,
            RequireCallsign = RequireCallsignCheck.IsChecked is true,
            EmergencyOnly = EmergencyOnlyCheck.IsChecked is true,
        };

        // A filter that can never match would present as an empty sky, which is
        // indistinguishable from a broken feed.
        if (candidate.Validate() is { } problem)
        {
            error = problem;
            return false;
        }

        filter = candidate;
        return true;
    }

    private static bool TryReadOptionalAltitude(string? text, out double? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!double.TryParse(text, CultureInfo.CurrentCulture, out double parsed))
        {
            return false;
        }

        value = parsed;
        return true;
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
        // While compact, the pane is hidden and the compact panel owns the
        // window; letting a stray selection re-show a full page would leave the
        // content far too big for a 360x150 window.
        if (_isCompact)
        {
            return;
        }

        if ((args?.SelectedItem ?? Nav.SelectedItem) is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        RadarPage.Visibility = tag == "radar" ? Visibility.Visible : Visibility.Collapsed;
        BoardPage.Visibility = tag == "board" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPage.Visibility = tag == "sources" ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = tag == "alerts" ? Visibility.Visible : Visibility.Collapsed;
        TrackPage.Visibility = tag == "track" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        AreasPage.Visibility = tag == "areas" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        bool built = tag is "radar" or "board" or "sources" or "settings" or "alerts"
            or "track" or "history" or "diagnostics" or "areas";

        if (tag == "alerts")
        {
            RefreshAlerts();
        }

        if (tag == "history")
        {
            RefreshHistory();
        }

        if (tag == "diagnostics")
        {
            RefreshDiagnostics();
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
            "devices" => ("Devices",
                "Not built yet. Discovery, pairing and configuration for M5Stack Core2 devices " +
                "are planned for Phase 3."),
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

        string? savedAs = await SaveTextAsync(
            content,
            extension,
            description,
            string.Create(
                CultureInfo.InvariantCulture,
                $"openflightdisplay-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"))
            .ConfigureAwait(true);

        if (savedAs is not null)
        {
            ExportStatus.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Exported {aircraft.Count} aircraft to {savedAs}.");
        }
    }

    /// <summary>
    /// Asks for a location and writes text to it.
    /// </summary>
    /// <returns>The file name written, or <c>null</c> if cancelled or failed.</returns>
    /// <remarks>
    /// Shared by the board export and the trail export, so both get the same
    /// unpackaged-window handling and the same failure reporting.
    /// </remarks>
    private async Task<string?> SaveTextAsync(
        string content,
        string extension,
        string description,
        string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };

            picker.FileTypeChoices.Add(description, [extension]);

            // An unpackaged app has no implicit window for the picker to attach
            // to, so the handle has to be supplied explicitly or the call hangs.
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                ExportStatus.Text = "Export cancelled.";
                HistoryStatusLine.Text = "Export cancelled.";
                return null;
            }

            await FileIO.WriteTextAsync(file, content);
            return file.Name;
        }
#pragma warning disable CA1031 // A failed export must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ExportStatus.Text = $"Export failed: {ex.Message}";
            HistoryStatusLine.Text = $"Export failed: {ex.Message}";
            return null;
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

    // ---- map backdrop ----

    /// <summary>
    /// Turns the backdrop on or off and applies it immediately.
    /// </summary>
    /// <remarks>
    /// Attribution is switched with it. OpenStreetMap's licence requires credit
    /// wherever its imagery appears, so the two are set together rather than the
    /// notice being a static piece of layout somebody could later "tidy" away
    /// while leaving the map.
    /// </remarks>
    private async void OnMapToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionEvents)
        {
            return;
        }

        _settings = _settings with { MapOverlayEnabled = MapCheck.IsChecked is true };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        ApplyMapSetting();
        UpdateMapCacheStatus();
    }

    private void ApplyMapSetting()
    {
        Radar.Tiles = _settings.MapOverlayEnabled
            ? _services.GetRequiredService<MapTileCache>()
            : null;

        ViewModel.MapAttributionVisible = _settings.MapOverlayEnabled;

        // Forces the plot to rebuild so the backdrop appears or disappears
        // without waiting for the next poll.
        Radar.RedrawNow();
    }

    private void UpdateMapCacheStatus()
    {
        if (!_settings.MapOverlayEnabled)
        {
            MapCacheStatus.Text = "The map is off. No tiles are being requested.";
            return;
        }

        var cache = _services.GetRequiredService<MapTileCache>();

        MapCacheStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Tiles are cached in {MapTileCache.DefaultCacheDirectory} " +
            $"({cache.CacheBytes() / 1024.0 / 1024.0:N1} MB) and reused for " +
            $"{MapTileCache.MaxCacheAge.TotalDays:N0} days.");
    }

    private void OnClearMapCache(object sender, RoutedEventArgs e)
    {
        long bytes = _services.GetRequiredService<MapTileCache>().Clear();

        MapCacheStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Cleared {bytes / 1024.0 / 1024.0:N1} MB of cached tiles.");

        Radar.RedrawNow();
    }

    // ---- compact mode ----

    /// <summary>Compact window size, in device-independent pixels.</summary>
    private const int CompactWidthDip = 360;

    /// <inheritdoc cref="CompactWidthDip"/>
    private const int CompactHeightDip = 150;

    private bool _isCompact;
    private Windows.Graphics.RectInt32 _restoreBounds;

    private void OnToggleCompact(object sender, RoutedEventArgs e)
    {
        if (_isCompact)
        {
            ExitCompactMode();
        }
        else
        {
            EnterCompactMode();
        }
    }

    /// <summary>
    /// Shrinks to a small always-on-top window showing only the nearest aircraft.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Microsoft.UI.Windowing.AppWindow.MoveAndResize"/> takes
    /// physical pixels, not DIPs.</b> Passing a DIP size straight in produces a
    /// window a third too small on a 150% display and two-thirds too small at
    /// 200%, so the size is scaled by the XAML root's rasterisation scale. This
    /// is the same units confusion that produced a phantom DPI defect in this
    /// project; see the DPI section of docs/WINDOWS_DESKTOP.md.
    /// </para>
    /// <para>
    /// The previous bounds are captured so leaving compact mode restores the
    /// window rather than guessing a size.
    /// </para>
    /// </remarks>
    private void EnterCompactMode()
    {
        Microsoft.UI.Windowing.AppWindow appWindow = AppWindow;
        _restoreBounds = new Windows.Graphics.RectInt32(
            appWindow.Position.X, appWindow.Position.Y,
            appWindow.Size.Width, appWindow.Size.Height);

        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;

        // The banner and attribution bar are hidden so the panel gets the whole
        // window. Leaving them took roughly half of a 150 DIP window and clipped
        // the content it exists to show. The compact panel carries the status
        // and the provider name itself, so nothing is lost.
        Nav.IsPaneVisible = false;
        StatusBanner.Visibility = Visibility.Collapsed;
        AttributionBar.Visibility = Visibility.Collapsed;
        CompactPage.Visibility = Visibility.Visible;
        HideAllPagesExceptCompact();

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        appWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(CompactWidthDip * scale),
            (int)(CompactHeightDip * scale)));

        _isCompact = true;
        CompactButton.Content = "Expand";
        RefreshCompact();
    }

    private void ExitCompactMode()
    {
        Microsoft.UI.Windowing.AppWindow appWindow = AppWindow;

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        // Restores the exact bounds captured on the way in, including position:
        // a compact window is usually dragged to a corner, and expanding in
        // place would leave most of the window off-screen.
        if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
        {
            appWindow.MoveAndResize(_restoreBounds);
        }

        Nav.IsPaneVisible = true;
        StatusBanner.Visibility = Visibility.Visible;
        AttributionBar.Visibility = Visibility.Visible;
        CompactPage.Visibility = Visibility.Collapsed;
        _isCompact = false;
        CompactButton.Content = "Compact";

        // Re-runs the normal navigation logic so the previously selected page
        // comes back, rather than leaving every page collapsed.
        OnNavSelectionChanged(Nav, null!);
    }

    private void HideAllPagesExceptCompact()
    {
        RadarPage.Visibility = Visibility.Collapsed;
        BoardPage.Visibility = Visibility.Collapsed;
        SourcesPage.Visibility = Visibility.Collapsed;
        AlertsPage.Visibility = Visibility.Collapsed;
        TrackPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
        AreasPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        NotBuiltPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Updates the compact readout.
    /// </summary>
    /// <remarks>
    /// Shows the nearest aircraft, and the tracked flight's departure advice
    /// when there is one — the two things worth a window that sits on top of
    /// everything else.
    /// </remarks>
    private void RefreshCompact()
    {
        if (!_isCompact)
        {
            return;
        }

        AircraftRowViewModel? nearest = ViewModel.Aircraft.FirstOrDefault();

        if (nearest is null)
        {
            CompactHeadline.Text = ViewModel.StatusHeadline;
            CompactDetail.Text = ViewModel.StatusDetail;
        }
        else
        {
            CompactHeadline.Text = nearest.Callsign;
            CompactDetail.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{nearest.Distance} · {nearest.Altitude} · {ViewModel.Aircraft.Count:N0} in range");
        }

        if (_tracking.CurrentState is { } tracked
            && tracked.Departure.Advice != DepartureAdvice.Unknown)
        {
            CompactTracked.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{tracked.Callsign}: {FlightTracking.AdviceWord(tracked.Departure.Advice)} " +
                $"(ETA {FlightTracking.FormatMinutesRemaining(tracked.Progress.MinutesRemaining)} min)");
            CompactTracked.Visibility = Visibility.Visible;
        }
        else
        {
            CompactTracked.Visibility = Visibility.Collapsed;
        }
    }

    // ---- monitoring area ----

    private AreaShape SelectedAreaShape =>
        AreaShapeBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out AreaShape shape)
            ? shape
            : AreaShape.Circle;

    private void PopulateAreaForm()
    {
        _suppressSelectionEvents = true;
        try
        {
            MonitoringAreaSetting area = _settings.MonitoringArea;

            foreach (object candidate in AreaShapeBox.Items)
            {
                if (candidate is ComboBoxItem { Tag: string tag }
                    && Enum.TryParse(tag, out AreaShape shape)
                    && shape == area.Shape)
                {
                    AreaShapeBox.SelectedItem = candidate;
                    break;
                }
            }

            AreaShapeBox.SelectedItem ??= AreaShapeBox.Items[0];

            AreaRadiusBox.Text = area.RadiusKm.ToString(CultureInfo.CurrentCulture);
            AreaHeadingBox.Text = area.HeadingDeg.ToString(CultureInfo.CurrentCulture);
            AreaWidthBox.Text = area.WidthDeg.ToString(CultureInfo.CurrentCulture);

            AreaUseHomeCheck.IsChecked = area.CenterLat is null;
            AreaLatBox.Text = area.CenterLat?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            AreaLonBox.Text = area.CenterLon?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

            AreaVerticesBox.Text = string.Join(
                Environment.NewLine,
                area.Vertices.Select(v => string.Create(
                    CultureInfo.CurrentCulture, $"{v.Lat}, {v.Lon}")));

            AreaMinAltBox.Text = area.MinAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            AreaMaxAltBox.Text = area.MaxAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

            AreaSummary.Text = "Currently monitoring: " + area.Summarise();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateAreaPanels();
    }

    private void OnAreaShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        // AreaPolygonPanel is created last on this page; a null means the
        // visual tree is still being built.
        if (AreaPolygonPanel is not null)
        {
            UpdateAreaPanels();
        }
    }

    private void OnAreaCentreToggled(object sender, RoutedEventArgs e) => UpdateAreaPanels();

    /// <summary>Shows only the fields the selected shape actually uses.</summary>
    private void UpdateAreaPanels()
    {
        AreaShape shape = SelectedAreaShape;

        AreaCentrePanel.Visibility = shape == AreaShape.Polygon
            ? Visibility.Collapsed
            : Visibility.Visible;

        AreaConePanel.Visibility = shape == AreaShape.Cone
            ? Visibility.Visible
            : Visibility.Collapsed;

        AreaPolygonPanel.Visibility = shape == AreaShape.Polygon
            ? Visibility.Visible
            : Visibility.Collapsed;

        AreaCentreCoords.Visibility = AreaUseHomeCheck.IsChecked is true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnSaveArea(object sender, RoutedEventArgs e)
    {
        if (!TryReadArea(out MonitoringAreaSetting area, out string? error))
        {
            AreaError.Message = error;
            AreaError.IsOpen = true;
            return;
        }

        // Refuses to save an area that cannot be built. Saving one would show an
        // empty sky with no explanation on the next poll.
        if (area.Build(_settings.HomeLatitude, _settings.HomeLongitude) is null)
        {
            AreaError.Message =
                "This area cannot be used yet. A circle or cone centred on home needs a home "
                + "location, which is set on the Settings page.";
            AreaError.IsOpen = true;
            return;
        }

        AreaError.IsOpen = false;

        _settings = _settings with { MonitoringArea = area };

        if (!await _settingsStore.SaveAsync(_settings).ConfigureAwait(true))
        {
            AreaError.Message = "The area could not be saved. It applies to this session only.";
            AreaError.IsOpen = true;
        }

        AreaSummary.Text = "Currently monitoring: " + area.Summarise();
        await RestartFeedAsync().ConfigureAwait(true);
    }

    private bool TryReadArea(out MonitoringAreaSetting area, out string? error)
    {
        area = new MonitoringAreaSetting();
        error = null;

        AreaShape shape = SelectedAreaShape;

        if (!double.TryParse(AreaRadiusBox.Text, CultureInfo.CurrentCulture, out double radiusKm))
        {
            radiusKm = _settings.MonitoringRadiusKm;
        }

        double heading = 0;
        double width = 90;

        if (shape == AreaShape.Cone)
        {
            if (!double.TryParse(AreaHeadingBox.Text, CultureInfo.CurrentCulture, out heading))
            {
                error = "The facing must be a number of degrees.";
                return false;
            }

            if (!double.TryParse(AreaWidthBox.Text, CultureInfo.CurrentCulture, out width))
            {
                error = "The width must be a number of degrees.";
                return false;
            }
        }

        double? centreLat = null;
        double? centreLon = null;

        if (shape != AreaShape.Polygon && AreaUseHomeCheck.IsChecked is not true)
        {
            if (!double.TryParse(AreaLatBox.Text, CultureInfo.CurrentCulture, out double lat)
                || !double.TryParse(AreaLonBox.Text, CultureInfo.CurrentCulture, out double lon))
            {
                error = "Enter a centre latitude and longitude, or tick 'centre on my home location'.";
                return false;
            }

            centreLat = lat;
            centreLon = lon;
        }

        IReadOnlyList<GeoPoint> vertices = [];
        if (shape == AreaShape.Polygon && !TryReadVertices(AreaVerticesBox.Text, out vertices, out error))
        {
            return false;
        }

        if (!TryReadOptionalAltitude(AreaMinAltBox.Text, out double? minAlt))
        {
            error = "The 'above' altitude must be a number in feet, or blank.";
            return false;
        }

        if (!TryReadOptionalAltitude(AreaMaxAltBox.Text, out double? maxAlt))
        {
            error = "The 'below' altitude must be a number in feet, or blank.";
            return false;
        }

        var candidate = new MonitoringAreaSetting
        {
            Shape = shape,
            CenterLat = centreLat,
            CenterLon = centreLon,
            RadiusKm = radiusKm,
            HeadingDeg = heading,
            WidthDeg = width,
            Vertices = vertices,
            MinAltitudeFt = minAlt,
            MaxAltitudeFt = maxAlt,
        };

        if (candidate.Validate() is { } problem)
        {
            error = problem;
            return false;
        }

        area = candidate;
        return true;
    }

    /// <summary>
    /// Parses the polygon outline.
    /// </summary>
    /// <remarks>
    /// Names the offending line rather than reporting a general failure — a
    /// sixty-point outline with one typo is otherwise miserable to correct.
    /// </remarks>
    private static bool TryReadVertices(
        string? text,
        out IReadOnlyList<GeoPoint> vertices,
        out string? error)
    {
        vertices = [];
        error = null;

        var points = new List<GeoPoint>();
        string[] lines = (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !double.TryParse(parts[0], CultureInfo.CurrentCulture, out double lat)
                || !double.TryParse(parts[1], CultureInfo.CurrentCulture, out double lon))
            {
                error = $"Line {i + 1} is not a latitude and longitude: \"{lines[i]}\".";
                return false;
            }

            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                error = $"Line {i + 1} is out of range. Latitude is -90 to 90, longitude -180 to 180.";
                return false;
            }

            points.Add(new GeoPoint(lat, lon));
        }

        vertices = points;
        return true;
    }

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
    private async void OnLoadRecording(object sender, RoutedEventArgs e)
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
    private async void OnDeleteHistory(object sender, RoutedEventArgs e)
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
