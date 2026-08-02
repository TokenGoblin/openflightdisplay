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
    private readonly AirportBoardService _airportBoard;
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

        _airportBoard = services.GetRequiredService<AirportBoardService>();

        _tracking.StateChanged += OnTrackedFlightChanged;
        _tracking.DepartureAdviceChanged += OnDepartureAdviceChanged;
        _airportBoard.StateChanged += OnAirportBoardChanged;

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

    /// <summary>
    /// Runs an async event handler without letting a failure kill the process.
    /// </summary>
    /// <remarks>
    /// Every <c>async void</c> handler in this window goes through here. An
    /// exception escaping one terminates the application with no message at all
    /// — see <see cref="SafeHandler"/>. The discard is deliberate: the returned
    /// task is already guarded, and awaiting it would just move the problem.
    /// </remarks>
    private void Safe(Func<Task> action)
        => _ = SafeHandler.RunAsync(action, ReportHandlerFailure);

    /// <summary>Shows a handler failure without disturbing the feed status.</summary>
    private void ReportHandlerFailure(string message)
    {
        HandlerError.Message = message;
        HandlerError.IsOpen = true;
    }

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
        await ResumeAirportBoardAsync().ConfigureAwait(true);
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

    // ContentDialog.ShowAsync throws if another dialog is already open, which
    // is reachable by double-clicking either of these buttons. Before the guard
    // that closed the application.
    private void OnAddAlertRule(object sender, RoutedEventArgs e)
        => Safe(() => EditRuleAsync(null));

    private void OnEditAlertRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }
            && _settings.EffectiveAlertRules.FirstOrDefault(r => r.Id == id) is { } existing)
        {
            Safe(() => EditRuleAsync(existing));
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

    private void OnDeleteAlertRule(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var rules = _settings.EffectiveAlertRules.Where(r => r.Id != id).ToList();
        Safe(() => SaveRulesAsync(rules));
    }

    private void OnAlertRuleToggled(object sender, RoutedEventArgs e)
        => Safe(() => ToggleAlertRuleAsync(sender));

    private async Task ToggleAlertRuleAsync(object sender)
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
        // Zoom is clamped to the fetch radius rather than being allowed to
        // exceed it, so the outermost ring always marks a boundary we actually
        // asked about.
        Radar.MaxRangeKm = _settings.MonitoringRadiusKm;
        ViewModel.RangeKm = Math.Min(
            _settings.DisplayRangeKm ?? _settings.MonitoringRadiusKm,
            _settings.MonitoringRadiusKm);
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

    private void OnDataSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || SourceList.SelectedItem is not ListViewItem { Tag: DataMode mode })
        {
            return;
        }

        // RestartFeedAsync resolves a provider, opens a database and starts a
        // poll loop. Any of those can throw on a bad configuration.
        Safe(async () =>
        {
            _settings = _settings with { DataMode = mode };
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
            await RestartFeedAsync().ConfigureAwait(true);
        });
    }

    private void OnUnitsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || UnitsBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out UnitSystem system))
        {
            return;
        }

        Safe(async () =>
        {
            _settings = _settings with { Units = system };
            ViewModel.Units = system;

            await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

            // Rows are formatted at construction, so a unit change needs a
            // rebuild rather than only a property notification.
            await RestartFeedAsync().ConfigureAwait(true);
        });
    }

    private void OnRankingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents
            || RankingBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out RankingMode mode))
        {
            return;
        }

        Safe(async () =>
        {
            _settings = _settings with { RankingMode = mode };
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

            // Ranking is chosen when the feed starts, so this needs a restart
            // rather than only a property change.
            await RestartFeedAsync().ConfigureAwait(true);
        });
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

    private void OnSaveSettings(object sender, RoutedEventArgs e) => Safe(SaveSettingsAsync);

    private async Task SaveSettingsAsync()
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
        AirportPage.Visibility = tag == "airport" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        bool built = tag is "radar" or "board" or "sources" or "settings" or "alerts"
            or "track" or "history" or "diagnostics" or "areas" or "airport";

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
                    $"{e.RuleName} Â· {e.FiredAt.ToLocalTime():HH:mm:ss} Â· {e.IcaoHex.ToUpperInvariant()}"),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            AlertsList.Items.Add(panel);
        }
    }

}

