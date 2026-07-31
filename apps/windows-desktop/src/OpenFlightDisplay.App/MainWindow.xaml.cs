namespace OpenFlightDisplay.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Infrastructure.Settings;

/// <summary>
/// Main application window: navigation rail, radar, flight board, detail pane.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Fallback observer location, used only until onboarding stores a real one.
    // Deliberately a well-known public coordinate and NOT anyone's home — the
    // privacy rules forbid committing a real location.
    private const double FallbackLat = 47.6062;
    private const double FallbackLon = -122.3321;

    private readonly SettingsStore _settingsStore;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        ViewModel = services.GetRequiredService<FlightBoardViewModel>();
        _settingsStore = services.GetRequiredService<SettingsStore>();

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
        AppSettings settings = await _settingsStore.LoadAsync().ConfigureAwait(true);

        double lat = settings.HomeLatitude ?? FallbackLat;
        double lon = settings.HomeLongitude ?? FallbackLon;

        ViewModel.Units = settings.Units;
        ViewModel.RangeKm = settings.MonitoringRadiusKm;

        var area = new CircleArea(lat, lon, settings.MonitoringRadiusKm);
        await ViewModel.StartAsync(area, lat, lon).ConfigureAwait(true);
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

        bool built = tag is "radar" or "board";
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
            "sources" => ("Data Sources",
                "Not built yet. Mock, replay and adsb.lol adapters all exist and are tested, but " +
                "the picker that lets you switch between them is planned for Phase 3. " +
                "Mock is currently active."),
            "diagnostics" => ("Diagnostics",
                "Not built yet. Provider latency, record counts, update cadence and log location " +
                "are planned for Phase 4."),
            "settings" => ("Settings",
                "Not built yet. Settings persist correctly and are read at startup, but the " +
                "editor and the first-run onboarding flow are still to come in Phase 1."),
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
