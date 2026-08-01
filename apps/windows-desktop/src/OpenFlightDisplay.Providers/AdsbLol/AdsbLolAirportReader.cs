namespace OpenFlightDisplay.Providers.AdsbLol;

using System.Text.Json;
using OpenFlightDisplay.Core.Tracking;

/// <summary>
/// Turns adsb.lol's <c>/api/0/airport/{icao}</c> response into an
/// <see cref="Airport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the HTTP client so the mapping can be tested against recorded
/// bodies with no network involved, matching <see cref="AdsbLolNormalizer"/>.
/// </para>
/// <para>
/// <b>The endpoint answers HTTP 200 with a literal <c>null</c> body for a code
/// it does not know</b>, including any IATA code. "Parsed successfully" is
/// therefore not the same as "found", and conflating the two would hand the
/// tracker an airport at 0°N 0°E with a sea-level field. The firmware makes the
/// same distinction — see <c>resolveDestination</c> in
/// <c>firmware/display/src/app/adsb_provider.cpp</c>.
/// </para>
/// </remarks>
public static class AdsbLolAirportReader
{
    /// <summary>
    /// Parses an airport lookup body.
    /// </summary>
    /// <param name="requestedIcao">
    /// Used as the code when the response omits one, so the result always
    /// carries the identifier it was looked up by.
    /// </param>
    /// <returns>The airport, or <c>null</c> if the response does not describe one.</returns>
    /// <exception cref="JsonException">The body is not valid JSON at all.</exception>
    public static Airport? Parse(string json, string requestedIcao)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            // The unknown-code case: a bare `null`, which is valid JSON and
            // means "no such airport".
            return null;
        }

        // Position is the minimum that makes the record usable. Without it there
        // is no distance to compute and nothing worth returning.
        if (ReadDouble(root, "lat") is not { } latitude
            || ReadDouble(root, "lon") is not { } longitude)
        {
            return null;
        }

        // Elevation is why this lookup is worth a request at all: "on the
        // ground" is judged against the field, and Denver's ramp is at 5,400 ft.
        // A response without it would silently measure against sea level, so it
        // is treated as an unusable record rather than defaulted to zero.
        if (ReadDouble(root, "alt_feet") is not { } elevationFt)
        {
            return null;
        }

        return new Airport(
            Icao: ReadString(root, "icao") ?? requestedIcao,
            Latitude: latitude,
            Longitude: longitude,
            ElevationFt: elevationFt,
            Name: ReadString(root, "name"));
    }

    /// <summary>
    /// Reads a number that may arrive as a JSON string.
    /// </summary>
    /// <remarks>
    /// The aircraft feed already returns <c>alt_baro</c> as a string for surface
    /// traffic, so a numeric field arriving quoted is a known habit of this API
    /// rather than a hypothetical.
    /// </remarks>
    private static double? ReadDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed)
                ? parsed
                : null,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
