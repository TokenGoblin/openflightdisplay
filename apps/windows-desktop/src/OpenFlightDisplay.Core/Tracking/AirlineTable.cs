namespace OpenFlightDisplay.Core.Tracking;

/// <summary>One airline's identifying codes.</summary>
/// <param name="Icao">Three-letter designator, as broadcast in a callsign.</param>
/// <param name="Iata">Two-character code, as printed on a boarding pass.</param>
/// <param name="Name">Human-readable name.</param>
public readonly record struct Airline(string Icao, string Iata, string Name);

/// <summary>
/// IATA to ICAO airline lookup.
/// </summary>
/// <remarks>
/// <para>
/// The two codes serve different masters: ADS-B broadcasts the ICAO designator
/// (<c>UAL1234</c>), while every boarding pass, arrivals board and text message
/// from the person being collected says IATA (<c>UA1234</c>). Tracking has to
/// accept what the user has in front of them.
/// </para>
/// <para>
/// <b>This duplicates <c>firmware/display/src/domain/airline.cpp</c>.</b> That is
/// a real cost and the project explicitly warns about it — <c>docs/PROTOCOL.md</c>
/// records that the table was deliberately kept out of TypeScript to avoid a
/// second source of truth. The desktop needs it anyway, because it tracks
/// flights standalone with no device in the path.
/// </para>
/// <para>
/// The duplication is made safe rather than merely accepted: a parity test reads
/// the firmware's table directly and fails if these two ever disagree. Adding an
/// airline in one place and not the other breaks the build instead of quietly
/// making a flight untrackable on one client.
/// </para>
/// </remarks>
public static class AirlineTable
{
    /// <summary>
    /// Airlines known to this build, in the same order as the firmware table.
    /// </summary>
    /// <remarks>
    /// A carrier with no commonly-used IATA code carries an empty string, which
    /// means "never match this row by IATA" rather than "matches an empty
    /// prefix".
    /// </remarks>
    public static IReadOnlyList<Airline> All { get; } =
    [
        new("AAL", "AA", "American Airlines"),
        new("ACA", "AC", "Air Canada"),
        new("AFR", "AF", "Air France"),
        new("ANA", "NH", "All Nippon Airways"),
        new("ANZ", "NZ", "Air New Zealand"),
        new("ASA", "AS", "Alaska Airlines"),
        new("BAW", "BA", "British Airways"),
        new("DAL", "DL", "Delta Air Lines"),
        new("DLH", "LH", "Lufthansa"),
        new("ENY", "MQ", "Envoy Air"),
        new("FDX", "FX", "FedEx Express"),
        new("FFT", "F9", "Frontier Airlines"),
        new("GTI", "5Y", "Atlas Air"),
        new("JAL", "JL", "Japan Airlines"),
        new("JBU", "B6", "JetBlue Airways"),
        new("JIA", "OH", "PSA Airlines"),
        new("KLM", "KL", "KLM"),
        new("NKS", "NK", "Spirit Airlines"),
        new("QFA", "QF", "Qantas"),
        new("QTR", "QR", "Qatar Airways"),
        new("QXE", "QX", "Horizon Air"),
        new("RPA", "YX", "Republic Airways"),
        new("SIA", "SQ", "Singapore Airlines"),
        new("SKW", "OO", "SkyWest Airlines"),
        new("SWA", "WN", "Southwest Airlines"),
        new("THY", "TK", "Turkish Airlines"),
        new("UAE", "EK", "Emirates"),
        new("UAL", "UA", "United Airlines"),
        new("UPS", "5X", "UPS Airlines"),
        new("WJA", "WS", "WestJet"),
    ];

    private static readonly Dictionary<string, string> IataToIcao =
        BuildIataIndex();

    /// <summary>
    /// Returns the ICAO designator for an IATA code, or <c>null</c> if unknown.
    /// </summary>
    public static string? IcaoForIata(string? iata)
    {
        if (string.IsNullOrEmpty(iata))
        {
            return null;
        }

        return IataToIcao.TryGetValue(iata, out string? icao) ? icao : null;
    }

    private static Dictionary<string, string> BuildIataIndex()
    {
        var index = new Dictionary<string, string>(All.Count, StringComparer.OrdinalIgnoreCase);

        foreach (Airline airline in All)
        {
            if (!string.IsNullOrEmpty(airline.Iata))
            {
                index[airline.Iata] = airline.Icao;
            }
        }

        return index;
    }
}
