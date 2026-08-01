namespace OpenFlightDisplay.App.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;
using OpenFlightDisplay.Providers.LocalReceiver;
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
        DataMode.LocalReceiver,
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
            "Read directly from your own dump1090, readsb or tar1090 receiver. " +
            "No rate limits, no internet needed, lowest latency. Set the receiver " +
            "URL in Settings - a bare host like http://192.168.1.10 is enough.",

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
    public IAviationDataProvider Resolve(DataMode mode) => Resolve(mode, receiverUrl: null);

    /// <inheritdoc cref="Resolve(DataMode)"/>
    /// <param name="receiverUrl">
    /// Base URL of a local receiver. Required for
    /// <see cref="DataMode.LocalReceiver"/> and ignored otherwise.
    /// </param>
    public IAviationDataProvider Resolve(DataMode mode, string? receiverUrl) => mode switch
    {
        DataMode.Mock => _services.GetRequiredService<MockProvider>(),

        DataMode.DirectProvider => _services.GetRequiredService<AdsbLolProvider>(),

        DataMode.LocalReceiver => CreateLocalReceiver(receiverUrl),

        // An empty recording is honest: the feed reports "replay complete"
        // immediately rather than pretending to play something.
        DataMode.Replay => new ReplayProvider("no recording loaded", []),

        _ => throw new NotSupportedException(
            $"Data mode '{mode}' is not implemented yet. {Describe(mode)}"),
    };

    /// <summary>
    /// Builds a receiver client for the configured URL.
    /// </summary>
    /// <remarks>
    /// Constructed per call rather than injected, because the base address is a
    /// user setting that can change at runtime. A bare host is accepted and the
    /// provider probes the well-known paths, so the user does not need to know
    /// whether their install serves under <c>/data/</c> or <c>/dump1090/data/</c>.
    /// </remarks>
    private LocalReceiverProvider CreateLocalReceiver(string? receiverUrl)
    {
        if (string.IsNullOrWhiteSpace(receiverUrl))
        {
            throw new NotSupportedException(
                "No receiver URL is configured. Set one in Settings, for example " +
                "http://192.168.1.10 or http://raspberrypi.local.");
        }

        if (!Uri.TryCreate(receiverUrl, UriKind.Absolute, out Uri? baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new NotSupportedException(
                $"'{receiverUrl}' is not a usable receiver address. It should look like " +
                "http://192.168.1.10 or http://raspberrypi.local.");
        }

        var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(5) };

        return new LocalReceiverProvider(
            client,
            _services.GetRequiredService<ILogger<LocalReceiverProvider>>());
    }
}
