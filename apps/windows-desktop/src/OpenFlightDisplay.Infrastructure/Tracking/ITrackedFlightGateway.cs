namespace OpenFlightDisplay.Infrastructure.Tracking;

using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;

/// <summary>
/// The two remote lookups following a flight needs.
/// </summary>
/// <remarks>
/// An interface rather than a direct dependency on <see cref="AdsbLolProvider"/>
/// so the tracking loop can be tested against scripted answers — including the
/// ones that are awkward to provoke live, like a flight that vanishes mid-cruise
/// or a destination that resolves only on the third attempt.
/// </remarks>
public interface ITrackedFlightGateway
{
    /// <summary>Fetches the tracked flight's latest position report.</summary>
    Task<ProviderResult> FetchByCallsignAsync(string callsign, CancellationToken cancellationToken);

    /// <summary>Resolves a destination ICAO code to coordinates and field elevation.</summary>
    Task<AirportLookupResult> ResolveAirportAsync(string? icao, CancellationToken cancellationToken);
}

/// <summary>Live implementation, backed by adsb.lol.</summary>
public sealed class AdsbLolTrackedFlightGateway : ITrackedFlightGateway
{
    private readonly AdsbLolProvider _provider;
    private readonly AirportLookup _airports;

    public AdsbLolTrackedFlightGateway(AdsbLolProvider provider, AirportLookup airports)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(airports);

        _provider = provider;
        _airports = airports;
    }

    /// <inheritdoc/>
    public Task<ProviderResult> FetchByCallsignAsync(
        string callsign,
        CancellationToken cancellationToken)
        => _provider.FetchByCallsignAsync(callsign, cancellationToken);

    /// <inheritdoc/>
    public Task<AirportLookupResult> ResolveAirportAsync(
        string? icao,
        CancellationToken cancellationToken)
        => _airports.ResolveAsync(icao, cancellationToken);
}
