namespace OpenFlightDisplay.Providers.Tar1090;

using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Reads one aircraft record in the tar1090/dump1090 JSON schema.
/// </summary>
/// <remarks>
/// <para>
/// This schema is shared by several sources: dump1090, readsb and tar1090 serve
/// it directly from a local receiver, and adsb.lol's <c>/v2</c> API returns the
/// same per-aircraft shape. Only the envelope differs — the array key and
/// whether a server timestamp is supplied — so the per-aircraft mapping lives
/// here once rather than being copied per provider.
/// </para>
/// <para>
/// The parser is defensive throughout. This is untrusted input from a free
/// community service or from a receiver on the local network: a field of the
/// wrong type is treated as absent, and a record without a usable identity or
/// position is dropped rather than emitted as a garbage aircraft.
/// </para>
/// </remarks>
public static class Tar1090AircraftReader
{
    /// <summary>
    /// Maps one aircraft object, or returns <c>null</c> if it has no usable
    /// identity or position.
    /// </summary>
    /// <param name="observedAt">
    /// The clock the record's age is measured against. For a local receiver
    /// this is the receiver's own <c>now</c>, not ours — see
    /// <c>LocalReceiverNormalizer</c> for why that distinction matters.
    /// </param>
    public static AircraftState? Read(
        JsonElement raw,
        string providerId,
        DateTimeOffset observedAt)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The leading '~' marks a TIS-B / non-ICAO address. Stripped so the hex
        // validates, because such a track is still worth showing.
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

            // baro_rate preferred, geom_rate as fallback.
            VerticalRateFtPerMin = ReadDouble(raw, "baro_rate") ?? ReadDouble(raw, "geom_rate"),

            Squawk = ReadSquawk(raw),
            EmergencyState = MapEmergency(ReadString(raw, "emergency")),
            OnGround = onGround,
            FirstSeen = observedAt,
            LastSeen = observedAt,

            // seen_pos is the age of the position in seconds. Using it keeps
            // staleness honest instead of stamping every record as fresh at the
            // moment we happened to poll.
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
