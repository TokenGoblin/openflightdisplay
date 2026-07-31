namespace OpenFlightDisplay.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Providers;
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

        services.AddSingleton<MockProvider>(_ => new MockProvider());

        // Mock is the startup default so first run works with no network and no
        // configuration. Switching the active provider is a Settings concern
        // and lands with the data-mode picker.
        services.AddSingleton<IAviationDataProvider>(sp => sp.GetRequiredService<MockProvider>());

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
