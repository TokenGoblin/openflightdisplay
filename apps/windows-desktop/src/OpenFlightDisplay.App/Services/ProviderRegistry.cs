namespace OpenFlightDisplay.App.Services;

using Microsoft.Extensions.DependencyInjection;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;
using OpenFlightDisplay.Providers.Mock;
using OpenFlightDisplay.Providers.Replay;

/// <summary>
/// Resolves the <see cref="IAviationDataProvider"/> for a configured data mode.
/// </summary>
/// <remarks>
/// Kept separate from the DI registrations so that switching data source at
/// runtime does not mean rebuilding the container. Modes that are not
/// implemented yet report that plainly instead of silently falling back to
/// mock data, which would look like working live data.
/// </remarks>
public sealed class ProviderRegistry
{
    private readonly IServiceProvider _services;

    public ProviderRegistry(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>Data modes the desktop can actually poll today.</summary>
    public static IReadOnlyList<DataMode> AvailableModes =>
    [
        DataMode.Mock,
        DataMode.DirectProvider,
        DataMode.Replay,
    ];

    /// <summary>True if <paramref name="mode"/> can be selected right now.</summary>
    public static bool IsImplemented(DataMode mode) => AvailableModes.Contains(mode);

    /// <summary>Short description shown in the data-source picker.</summary>
    public static string Describe(DataMode mode) => mode switch
    {
        DataMode.Mock =>
            "Synthetic aircraft generated locally. No network, no API key. Best for " +
            "trying the app out and for development.",

        DataMode.DirectProvider =>
            "Poll adsb.lol directly over the internet. Free and open, no API key " +
            "required. Shows real aircraft near your configured location.",

        DataMode.Replay =>
            "Play back a recorded session. Useful for demos and for reproducing a " +
            "problem against the exact data that caused it.",

        DataMode.Gateway =>
            "Consume the feed from an existing OpenFlightDisplay gateway on your " +
            "network. Not implemented yet (Phase 3).",

        DataMode.LocalReceiver =>
            "Read from a dump1090, readsb or tar1090 JSON feed on your own " +
            "receiver. Not implemented yet (Phase 3).",

        _ => string.Empty,
    };

    /// <summary>Human-readable name for the picker.</summary>
    public static string DisplayName(DataMode mode) => mode switch
    {
        DataMode.Mock => "Mock data (offline)",
        DataMode.DirectProvider => "adsb.lol (live)",
        DataMode.Replay => "Replay a recording",
        DataMode.Gateway => "OpenFlightDisplay gateway",
        DataMode.LocalReceiver => "Local ADS-B receiver",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Creates the provider for a mode.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The mode is recognised but not implemented. Thrown rather than quietly
    /// substituting mock data, which would present synthetic aircraft as live.
    /// </exception>
    public IAviationDataProvider Resolve(DataMode mode) => mode switch
    {
        DataMode.Mock => _services.GetRequiredService<MockProvider>(),

        DataMode.DirectProvider => _services.GetRequiredService<AdsbLolProvider>(),

        // An empty recording is honest: the feed reports "replay complete"
        // immediately rather than pretending to play something.
        DataMode.Replay => new ReplayProvider("no recording loaded", []),

        _ => throw new NotSupportedException(
            $"Data mode '{mode}' is not implemented yet. {Describe(mode)}"),
    };
}
