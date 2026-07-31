namespace OpenFlightDisplay.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Areas;

/// <summary>
/// Main application window: status banner plus flight board.
/// </summary>
/// <remarks>
/// The Phase 1 vertical slice. The left navigation rail, radar, and the
/// remaining pages arrive with the rest of Phase 1 and Phase 2.
/// </remarks>
public sealed partial class MainWindow : Window
{
    // Placeholder observer location for the vertical slice. This is replaced by
    // the persisted setting from first-run onboarding; it is deliberately a
    // well-known public coordinate and NOT anyone's home, since committing a
    // real location is exactly what the privacy rules forbid.
    private const double DefaultObserverLat = 47.6062;
    private const double DefaultObserverLon = -122.3321;
    private const double DefaultRadiusKm = 80.0;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        ViewModel = services.GetRequiredService<FlightBoardViewModel>();

        // Fire-and-forget is deliberate: the feed publishes its own state,
        // including failures, so there is no outcome here worth awaiting. The
        // continuation exists only so a startup bug cannot become an unobserved
        // task exception.
        _ = StartFeedAsync();
    }

    /// <summary>Bound by the XAML.</summary>
    public FlightBoardViewModel ViewModel { get; }

    private async Task StartFeedAsync()
    {
        var area = new CircleArea(DefaultObserverLat, DefaultObserverLon, DefaultRadiusKm);
        await ViewModel.StartAsync(area, DefaultObserverLat, DefaultObserverLon)
            .ConfigureAwait(false);
    }
}
