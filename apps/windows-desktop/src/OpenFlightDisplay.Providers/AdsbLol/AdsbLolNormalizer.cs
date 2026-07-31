namespace OpenFlightDisplay.Providers.AdsbLol;

using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Turns adsb.lol's tar1090-style JSON into <see cref="AircraftState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separated from the HTTP client so the mapping can be tested
/// against recorded fixtures with no network involved. Mirrors
/// <c>normalizeAdsbLolAircraft</c> in
/// <c>services/gateway/src/providers/adsblol.ts</c>.
/// </para>
/// <para>
/// Three response quirks are load-bearing and each has a test:
/// callsigns are space-padded to eight characters; <c>alt_baro</c> is the
/// <b>string</b> <c>"ground"</c> for surface traffic rather than a number; and
/// some records omit <c>flight</c> entirely.
/// </para>
/// <para>
/// The parser is defensive throughout because this is untrusted input from a
/// free community service. A field of the wrong type is treated as absent, and
/// a record without a usable identity or position is dropped rather than
/// emitted as a garbage aircraft.
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
            if (Normalize(element, providerId, observedAt) is { } aircraft)
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
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The leading '~' marks a TIS-B / non-ICAO address. Stripped to match
        // the gateway, which normalises it away before validating the hex.
        string? hex = ReadString(raw, "hex")?.TrimStart('~').ToLowerInvariant();
        if (!IsValidIcaoHex(hex))
        {
            return null;
        }

        double? lat = ReadDouble(raw, "lat");
        double? lon = ReadDouble(raw, "lon");
        if (lat is not { } latitude || lon is not { } longitude)
        {
            return null;
        }

        // Quirk: alt_baro is the string "ground" for surface traffic. Reading it
        // as a number silently loses the fact that the aircraft is on the ground.
        bool onGround = raw.TryGetProperty("alt_baro", out JsonElement altBaro)
            && altBaro.ValueKind == JsonValueKind.String
            && altBaro.ValueEquals("ground");

        double? barometricAltitudeFt = ReadDouble(raw, "alt_baro");

        // Quirk: callsigns are space-padded to eight characters.
        string? callsign = ReadString(raw, "flight")?.Trim();
        if (string.IsNullOrEmpty(callsign))
        {
            callsign = null;
        }

        DataQualityFlags flags = DataQualityFlags.None;
        if (callsign is null)
        {
            flags |= DataQualityFlags.NoCallsign;
        }

        if (onGround || barometricAltitudeFt is null)
        {
            flags |= DataQualityFlags.NoAltitude;
        }

        return new AircraftState
        {
            Provider = providerId,
            IcaoHex = hex!,
            Callsign = callsign,
            Registration = ReadString(raw, "r"),
            AircraftTypeCode = ReadString(raw, "t"),
            AircraftCategory = MapCategory(ReadString(raw, "category")),
            Latitude = latitude,
            Longitude = longitude,
            BarometricAltitudeFt = barometricAltitudeFt,
            GeometricAltitudeFt = ReadDouble(raw, "alt_geom"),
            GroundSpeedKt = ReadDouble(raw, "gs"),
            TrackHeadingDeg = ReadDouble(raw, "track"),

            // baro_rate preferred, geom_rate as fallback — same precedence as
            // the gateway.
            VerticalRateFtPerMin = ReadDouble(raw, "baro_rate") ?? ReadDouble(raw, "geom_rate"),

            Squawk = ReadSquawk(raw),
            EmergencyState = MapEmergency(ReadString(raw, "emergency")),
            OnGround = onGround,
            FirstSeen = observedAt,
            LastSeen = observedAt,

            // adsb.lol's `seen_pos` is the age of the position in seconds.
            // Using it keeps staleness honest instead of stamping every record
            // as fresh at the moment we happened to poll.
            PositionTimestamp = ReadDouble(raw, "seen_pos") is { } seenPos and >= 0
                ? observedAt.AddSeconds(-seenPos)
                : observedAt,

            DataQualityFlags = flags,
        };
    }

    private static bool IsValidIcaoHex(string? hex)
    {
        if (hex is not { Length: 6 })
        {
            return false;
        }

        foreach (char c in hex)
        {
            bool isHexDigit = c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a numeric property, or <c>null</c> if absent or not a number.
    /// </summary>
    /// <remarks>
    /// Returning null for a wrong-typed field is what makes <c>alt_baro:
    /// "ground"</c> safe — it becomes "no altitude reported", which is true,
    /// rather than a parse failure that would discard an otherwise valid record.
    /// </remarks>
    private static double? ReadDouble(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double result)
            && !double.IsNaN(result)
            && !double.IsInfinity(result)
                ? result
                : null;

    private static string? ReadSquawk(JsonElement obj)
    {
        string? squawk = ReadString(obj, "squawk");
        if (squawk is not { Length: 4 })
        {
            return null;
        }

        // Squawk codes are octal: 0-7 only. "8888" is malformed, not a code.
        foreach (char c in squawk)
        {
            if (c is < '0' or > '7')
            {
                return null;
            }
        }

        return squawk;
    }

    private static AircraftCategory? MapCategory(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            // Absent, not Unknown. The provider said nothing about the category,
            // which is different from saying something we did not recognise.
            return null;
        }

        return raw switch
        {
            "B1" => AircraftCategory.Glider,
            "B2" => AircraftCategory.Balloon,
            "B6" => AircraftCategory.Drone,
            "B7" => AircraftCategory.Rotorcraft,
            ['A', ..] => AircraftCategory.FixedWing,
            ['C', ..] => AircraftCategory.GroundVehicle,
            _ => AircraftCategory.Unknown,
        };
    }

    private static EmergencyState MapEmergency(string? raw) => raw switch
    {
        "general" => EmergencyState.General,
        "lifeguard" => EmergencyState.Medical,
        "minfuel" => EmergencyState.MinimumFuel,
        "nordo" => EmergencyState.NoCommunications,
        "unlawful" => EmergencyState.UnlawfulInterference,
        "downed" => EmergencyState.Downed,
        _ => EmergencyState.None,
    };
}
