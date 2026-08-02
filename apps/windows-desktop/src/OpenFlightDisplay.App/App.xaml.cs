namespace OpenFlightDisplay.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Infrastructure.Maps;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Infrastructure.Tracking;
using OpenFlightDisplay.Providers.AdsbLol;
using OpenFlightDisplay.Providers.Mock;

/// <summary>
/// Application entry point and composition root.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>The configured DI container.</summary>
    public IServiceProvider Services { get; }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Services);
        _window.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(TimeProvider.System);

        // HttpClientFactory owns connection pooling and rotation. Creating
        // HttpClient by hand leaks sockets and pins stale DNS.
        services.AddHttpClient<AdsbLolProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.adsb.lol");
            client.Timeout = TimeSpan.FromSeconds(8);

            // adsb.lol is free and community-funded; identifying the client is
            // the polite minimum and helps them attribute load.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenFlightDisplay-Desktop/0.1");
        });

        // The airport lookup shares adsb.lol's base address and courtesy
        // user-agent, but not its 8 second timeout — resolving a destination
        // happens once per flight and can afford to be patient.
        services.AddHttpClient<AirportLookup>(client =>
        {
            client.BaseAddress = new Uri("https://api.adsb.lol");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenFlightDisplay-Desktop/0.1");
        });

        // OpenStreetMap's tile usage policy requires a User-Agent that identifies
        // the application and a way to contact whoever runs it. A generic or
        // absent one is grounds for being blocked, and rightly so.
        services.AddHttpClient<MapTileCache>(client =>
        {
            client.BaseAddress = new Uri("https://tile.openstreetmap.org");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "OpenFlightDisplay-Desktop/0.1 (+https://github.com/TokenGoblin/openflightdisplay)");
        });

        services.AddSingleton<MockProvider>(_ => new MockProvider());

        // Flight tracking always goes to adsb.lol, whatever the radar is using:
        // it is the only configured source with a callsign lookup, and the page
        // says so rather than silently doing something else.
        services.AddSingleton<ITrackedFlightGateway>(sp => new AdsbLolTrackedFlightGateway(
            sp.GetRequiredService<AdsbLolProvider>(),
            sp.GetRequiredService<AirportLookup>()));

        services.AddSingleton<FlightTrackingService>();

        // The active provider is chosen at runtime from persisted settings via
        // ProviderRegistry, not bound here — switching data source must not
        // require rebuilding the container.
        services.AddSingleton<ProviderRegistry>();

        services.AddSingleton<AircraftFeedService>();

        services.AddSingleton(sp => new SettingsStore(
            SettingsStore.DefaultFilePath,
            sp.GetRequiredService<ILogger<SettingsStore>>()));

        // The dispatcher is captured on the UI thread at construction, which is
        // where ConfigureServices runs.
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton<FlightBoardViewModel>();

        return services.BuildServiceProvider();
    }
}
