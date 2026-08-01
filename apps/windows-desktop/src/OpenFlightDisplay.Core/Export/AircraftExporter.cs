namespace OpenFlightDisplay.Core.Export;

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Units;

/// <summary>Formats aircraft data for export.</summary>
/// <remarks>
/// Pure string generation with no file I/O, so every format decision is
/// testable. The caller writes the result wherever it wants.
/// </remarks>
public static class AircraftExporter
{
    /// <summary>Columns written by <see cref="ToCsv"/>, in order.</summary>
    private static readonly string[] CsvHeaders =
    [
        "icao_hex", "callsign", "registration", "aircraft_type", "provider",
        "latitude", "longitude", "altitude_ft", "ground_speed_kt",
        "track_heading_deg", "vertical_rate_fpm", "squawk", "emergency_state",
        "on_ground", "distance_km", "bearing_deg", "vertical_trend",
        "data_quality_flags", "observed_at_utc",
    ];

    /// <summary>
    /// Renders aircraft as RFC 4180 CSV.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always in canonical units (km, feet, knots) with UTC timestamps and
    /// invariant number formatting, regardless of the user's display units. An
    /// export is data for another tool, not a screenshot — a file whose numbers
    /// depend on a UI setting is a file nobody can safely re-import.
    /// </para>
    /// <para>
    /// A missing value is an empty field, never <c>0</c>.
    /// </para>
    /// </remarks>
    public static string ToCsv(IEnumerable<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        var builder = new StringBuilder();
        builder.AppendJoin(',', CsvHeaders).Append("\r\n");

        foreach (AircraftState a in aircraft)
        {
            string[] fields =
            [
                a.IcaoHex,
                a.Callsign ?? string.Empty,
                a.Registration ?? string.Empty,
                a.AircraftTypeCode ?? string.Empty,
                a.Provider,
                Number(a.Latitude),
                Number(a.Longitude),
                Number(a.AltitudeFt),
                Number(a.GroundSpeedKt),
                Number(a.TrackHeadingDeg),
                Number(a.VerticalRateFtPerMin),
                a.Squawk ?? string.Empty,
                a.EmergencyState.ToString(),
                a.OnGround ? "true" : "false",
                Number(a.DistanceFromObserverKm),
                Number(a.BearingFromObserverDeg),
                a.VerticalTrend.ToString(),
                a.DataQualityFlags.ToString(),
                a.PositionTimestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ];

            builder.AppendJoin(',', fields.Select(EscapeCsvField)).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes one CSV field, including neutralising spreadsheet formulas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callsigns and registrations come from an aviation-data provider and are
    /// not trusted input. A field beginning <c>=</c>, <c>+</c>, <c>-</c>,
    /// <c>@</c>, tab or carriage return is interpreted as a formula by Excel and
    /// several other spreadsheets, which is a well-known injection route out of
    /// a "harmless" data file.
    /// </para>
    /// <para>
    /// Prefixing a single quote is the conventional mitigation: the spreadsheet
    /// treats the value as text and the original characters are still visible.
    /// </para>
    /// </remarks>
    internal static string EscapeCsvField(string value)
    {
        string escaped = value;

        if (escaped.Length > 0 && escaped[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            escaped = "'" + escaped;
        }

        bool needsQuotes = escaped.Contains(',', StringComparison.Ordinal)
            || escaped.Contains('"', StringComparison.Ordinal)
            || escaped.Contains('\n', StringComparison.Ordinal)
            || escaped.Contains('\r', StringComparison.Ordinal);

        return needsQuotes
            ? '"' + escaped.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : escaped;
    }

    /// <summary>Renders aircraft as a JSON array.</summary>
    /// <remarks>
    /// Written explicitly with <see cref="Utf8JsonWriter"/> rather than by
    /// reflection. This project is marked AOT-compatible, and
    /// <c>JsonSerializer.Serialize</c> over arbitrary object graphs needs
    /// runtime code generation that trimming and AOT cannot provide. Writing the
    /// fields by hand is also what lets a missing value be omitted rather than
    /// emitted as null or zero.
    /// </remarks>
    public static string ToJson(IEnumerable<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();

            foreach (AircraftState a in aircraft)
            {
                WriteAircraftObject(writer, a);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAircraftObject(Utf8JsonWriter writer, AircraftState a)
    {
        writer.WriteStartObject();

        writer.WriteString("provider", a.Provider);
        writer.WriteString("icaoHex", a.IcaoHex);
        WriteOptionalString(writer, "callsign", a.Callsign);
        WriteOptionalString(writer, "registration", a.Registration);
        WriteOptionalString(writer, "aircraftTypeCode", a.AircraftTypeCode);

        writer.WriteNumber("latitude", a.Latitude);
        writer.WriteNumber("longitude", a.Longitude);

        WriteOptionalNumber(writer, "altitudeFt", a.AltitudeFt);
        WriteOptionalNumber(writer, "geometricAltitudeFt", a.GeometricAltitudeFt);
        WriteOptionalNumber(writer, "barometricAltitudeFt", a.BarometricAltitudeFt);
        WriteOptionalNumber(writer, "groundSpeedKt", a.GroundSpeedKt);
        WriteOptionalNumber(writer, "trackHeadingDeg", a.TrackHeadingDeg);
        WriteOptionalNumber(writer, "verticalRateFtPerMin", a.VerticalRateFtPerMin);
        WriteOptionalString(writer, "squawk", a.Squawk);

        writer.WriteString("emergencyState", a.EmergencyState.ToString());
        writer.WriteBoolean("onGround", a.OnGround);
        writer.WriteString("verticalTrend", a.VerticalTrend.ToString());

        WriteOptionalNumber(writer, "distanceFromObserverKm", a.DistanceFromObserverKm);
        WriteOptionalNumber(writer, "bearingFromObserverDeg", a.BearingFromObserverDeg);
        WriteOptionalNumber(writer, "slantRangeKm", a.SlantRangeKm);

        writer.WriteString("dataQualityFlags", a.DataQualityFlags.ToString());
        writer.WriteString(
            "positionTimestamp",
            a.PositionTimestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        writer.WriteEndObject();
    }

    /// <summary>Writes a string property, or omits it entirely when absent.</summary>
    /// <remarks>
    /// Omitted rather than written as <c>null</c>, matching the wire protocol's
    /// rule that fields a provider did not report are left out.
    /// </remarks>
    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    /// <inheritdoc cref="WriteOptionalString"/>
    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    /// <summary>
    /// Renders aircraft as a GeoJSON <c>FeatureCollection</c> of points.
    /// </summary>
    /// <remarks>
    /// GeoJSON coordinates are <b>[longitude, latitude]</b>, in that order
    /// (RFC 7946). Emitting them the other way round is the classic GeoJSON bug
    /// and produces a file that loads without error and plots in the wrong
    /// hemisphere, so it is pinned by a test.
    /// </remarks>
    public static string ToGeoJson(IEnumerable<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            foreach (AircraftState a in aircraft)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                writer.WriteStartObject("geometry");
                writer.WriteString("type", "Point");
                WriteCoordinates(writer, a.Latitude, a.Longitude, a.AltitudeFt);
                writer.WriteEndObject();

                writer.WriteStartObject("properties");
                writer.WriteString("icaoHex", a.IcaoHex);
                WriteOptionalString(writer, "callsign", a.Callsign);
                WriteOptionalString(writer, "registration", a.Registration);
                WriteOptionalString(writer, "aircraftType", a.AircraftTypeCode);
                writer.WriteString("provider", a.Provider);
                WriteOptionalNumber(writer, "altitudeFt", a.AltitudeFt);
                WriteOptionalNumber(writer, "groundSpeedKt", a.GroundSpeedKt);
                WriteOptionalNumber(writer, "trackHeadingDeg", a.TrackHeadingDeg);
                WriteOptionalNumber(writer, "verticalRateFtPerMin", a.VerticalRateFtPerMin);
                WriteOptionalString(writer, "squawk", a.Squawk);
                writer.WriteString("emergencyState", a.EmergencyState.ToString());
                writer.WriteBoolean("onGround", a.OnGround);
                writer.WriteString(
                    "observedAt",
                    a.PositionTimestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a GeoJSON coordinate array: longitude, latitude, then optional
    /// altitude in metres.
    /// </summary>
    /// <remarks>
    /// Longitude first, per RFC 7946. Altitude is included only when it was
    /// actually reported — a zero would claim sea level.
    /// </remarks>
    private static void WriteCoordinates(
        Utf8JsonWriter writer,
        double latitude,
        double longitude,
        double? altitudeFt)
    {
        writer.WriteStartArray("coordinates");
        writer.WriteNumberValue(longitude);
        writer.WriteNumberValue(latitude);

        if (altitudeFt is { } altitude)
        {
            writer.WriteNumberValue(altitude * UnitConverter.MetresPerFoot);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Renders a recorded track as a GeoJSON <c>LineString</c> feature.
    /// </summary>
    /// <param name="positions">Positions in time order, oldest first.</param>
    public static string TrailToGeoJson(
        string icaoHex,
        string? callsign,
        IEnumerable<(double Latitude, double Longitude, double? AltitudeFt)> positions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icaoHex);
        ArgumentNullException.ThrowIfNull(positions);

        var points = positions.ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");

            writer.WriteStartObject("geometry");
            writer.WriteString("type", "LineString");
            writer.WriteStartArray("coordinates");

            foreach ((double latitude, double longitude, double? altitudeFt) in points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(longitude);
                writer.WriteNumberValue(latitude);

                if (altitudeFt is { } altitude)
                {
                    writer.WriteNumberValue(altitude * UnitConverter.MetresPerFoot);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("properties");
            writer.WriteString("icaoHex", icaoHex);
            WriteOptionalString(writer, "callsign", callsign);
            writer.WriteNumber("pointCount", points.Count);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Invariant number formatting, empty for a missing value.</summary>
    private static string Number(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
}
