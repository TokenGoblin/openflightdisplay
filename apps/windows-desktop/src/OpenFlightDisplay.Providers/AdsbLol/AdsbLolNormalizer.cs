namespace OpenFlightDisplay.Providers.AdsbLol;

using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Providers.Tar1090;

/// <summary>
/// Turns adsb.lol's <c>/v2</c> response into <see cref="AircraftState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separated from the HTTP client so the mapping can be tested
/// against recorded fixtures with no network involved.
/// </para>
/// <para>
/// Only the envelope is adsb.lol-specific: the aircraft array lives under
/// <c>ac</c>, and no server timestamp is used. The per-aircraft schema is
/// tar1090's, shared with dump1090 and readsb, so the mapping itself lives in
/// <see cref="Tar1090AircraftReader"/> rather than being duplicated here.
/// </para>
/// <para>
/// Three response quirks are load-bearing and each has a test: callsigns are
/// space-padded to eight characters; <c>alt_baro</c> is the <b>string</b>
/// <c>"ground"</c> for surface traffic; and some records omit <c>flight</c>
/// entirely.
/// </para>
/// </remarks>
public static class AdsbLolNormalizer
{
    /// <summary>
    /// Parses a full <c>/v2/point</c> response body.
    /// </summary>
    /// <returns>
    /// The aircraft that could be normalized. Returns empty — never throws —
    /// for a body that is valid JSON but not the expected shape.
    /// </returns>
    /// <exception cref="JsonException">The body is not valid JSON at all.</exception>
    public static IReadOnlyList<AircraftState> ParseResponse(
        string json,
        string providerId,
        DateTimeOffset observedAt)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("ac", out JsonElement ac)
            || ac.ValueKind != JsonValueKind.Array)
        {
            // Valid JSON, unexpected shape. The gateway returns [] here too.
            return [];
        }

        var results = new List<AircraftState>(ac.GetArrayLength());
        foreach (JsonElement element in ac.EnumerateArray())
        {
            if (Tar1090AircraftReader.Read(element, providerId, observedAt) is { } aircraft)
            {
                results.Add(aircraft);
            }
        }

        return results;
    }

    /// <summary>
    /// Normalizes a single aircraft record, or returns <c>null</c> if it has no
    /// usable identity or position.
    /// </summary>
    public static AircraftState? Normalize(
        JsonElement raw,
        string providerId,
        DateTimeOffset observedAt)
        => Tar1090AircraftReader.Read(raw, providerId, observedAt);
}
