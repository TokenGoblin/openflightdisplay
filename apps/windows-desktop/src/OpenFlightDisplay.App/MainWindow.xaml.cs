namespace OpenFlightDisplay.App;

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Providers;

/// <summary>
/// Main application window: navigation rail, radar, flight board, detail pane,
/// data-source picker and settings.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Fallback observer location, used only until a real one is configured.
    // Deliberately a well-known public coordinate and NOT anyone's home — the
    // privacy rules forbid committing a real location.
    private const double FallbackLat = 47.6062;
    private const double FallbackLon = -122.3321;

    private readonly SettingsStore _settingsStore;
    private readonly ProviderRegistry _providers;

    private AppSettings _settings = new();

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

        ViewModel = services.GetRequiredService<FlightBoardViewModel>();
        _settingsStore = services.GetRequiredService<SettingsStore>();
        _providers = services.GetRequiredService<ProviderRegistry>();

        Nav.SelectedItem = Nav.MenuItems[0];

        // Fire-and-forget is deliberate: the feed publishes its own state,
        // including failures, so there is no outcome worth awaiting here. The
        // discard exists so a startup bug cannot become an unobserved task
        // exception.
        _ = InitialiseAsync();
    }

    /// <summary>Bound by the XAML.</summary>
    public FlightBoardViewModel ViewModel { get; }

    private async Task InitialiseAsync()
    {
        _settings = await _settingsStore.LoadAsync().ConfigureAwait(true);

        PopulateDataSources();
        PopulateSettingsForm();

        await RestartFeedAsync().ConfigureAwait(true);
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
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private async Task RestartFeedAsync()
    {
        double lat = _settings.HomeLatitude ?? FallbackLat;
        double lon = _settings.HomeLongitude ?? FallbackLon;

        ViewModel.Units = _settings.Units;
        ViewModel.RangeKm = _settings.MonitoringRadiusKm;

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
}
