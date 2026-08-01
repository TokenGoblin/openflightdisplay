namespace OpenFlightDisplay.Core.Tests;

using System.Globalization;
using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Export;
using Xunit;

public class AircraftExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- CSV ----

    [Fact]
    public void Csv_starts_with_a_header_row()
    {
        string csv = AircraftExporter.ToCsv([]);

        Assert.StartsWith("icao_hex,callsign,registration", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_writes_one_row_per_aircraft()
    {
        string csv = AircraftExporter.ToCsv([Sample("aaa001"), Sample("bbb002")]);

        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Csv_leaves_a_missing_value_empty_rather_than_zero()
    {
        // The rule the whole model rests on, at the export boundary: an
        // aircraft that reported no groundspeed is not doing 0 knots, and a
        // spreadsheet showing 0 would say it was.
        string csv = AircraftExporter.ToCsv([
            Sample("aaa001") with { GroundSpeedKt = null, GeometricAltitudeFt = null },
        ]);

        string row = csv.Split("\r\n")[1];
        string[] fields = row.Split(',');

        // altitude_ft is index 7, ground_speed_kt index 8.
        Assert.Equal(string.Empty, fields[7]);
        Assert.Equal(string.Empty, fields[8]);
    }

    [Fact]
    public void Csv_writes_a_genuine_zero_as_zero()
    {
        string csv = AircraftExporter.ToCsv([Sample("aaa001") with { GroundSpeedKt = 0.0 }]);

        Assert.Equal("0", csv.Split("\r\n")[1].Split(',')[8]);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1)")]
    [InlineData("-2+3")]
    [InlineData("@import")]
    public void Csv_neutralises_spreadsheet_formula_injection(string hostile)
    {
        // Callsigns come from an aviation-data provider and are not trusted.
        // A leading =, +, - or @ is executed as a formula by Excel and others.
        string csv = AircraftExporter.ToCsv([Sample("aaa001") with { Callsign = hostile }]);

        string callsignField = csv.Split("\r\n")[1].Split(',')[1];

        Assert.StartsWith("'", callsignField.TrimStart('"'), StringComparison.Ordinal);
        Assert.Contains(hostile, csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_quotes_a_field_containing_a_comma()
    {
        string csv = AircraftExporter.ToCsv([Sample("aaa001") with { Registration = "N1,234" }]);

        Assert.Contains("\"N1,234\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_doubles_embedded_quotes()
    {
        string csv = AircraftExporter.ToCsv([Sample("aaa001") with { Registration = "N\"1" }]);

        Assert.Contains("\"N\"\"1\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_quotes_a_field_containing_a_newline()
    {
        string escaped = AircraftExporter.EscapeCsvField("line1\nline2");

        Assert.StartsWith("\"", escaped, StringComparison.Ordinal);
        Assert.EndsWith("\"", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_uses_invariant_numbers_regardless_of_the_current_culture()
    {
        // A German locale would otherwise write 47,6 and shift every subsequent
        // column, producing a file that silently reimports wrongly.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            string csv = AircraftExporter.ToCsv([Sample("aaa001")]);

            Assert.Contains("47.6062", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("47,6062", csv, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Csv_writes_timestamps_in_utc_round_trip_format()
    {
        string csv = AircraftExporter.ToCsv([Sample("aaa001")]);

        Assert.Contains("2026-08-01T12:00:00.0000000", csv, StringComparison.Ordinal);
    }

    // ---- JSON ----

    [Fact]
    public void Json_is_a_valid_array()
    {
        string json = AircraftExporter.ToJson([Sample("aaa001"), Sample("bbb002")]);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void Json_writes_enums_as_names_not_numbers()
    {
        string json = AircraftExporter.ToJson([
            Sample("aaa001") with { EmergencyState = EmergencyState.MinimumFuel },
        ]);

        Assert.Contains("MinimumFuel", json, StringComparison.Ordinal);
    }

    // ---- GeoJSON ----

    [Fact]
    public void GeoJson_is_a_feature_collection()
    {
        string geoJson = AircraftExporter.ToGeoJson([Sample("aaa001")]);

        using JsonDocument document = JsonDocument.Parse(geoJson);

        Assert.Equal("FeatureCollection", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public void GeoJson_writes_coordinates_as_longitude_then_latitude()
    {
        // RFC 7946 order. Getting it backwards produces a file that loads
        // without error and plots in the wrong hemisphere — the classic
        // GeoJSON bug, and invisible without a check like this one.
        string geoJson = AircraftExporter.ToGeoJson([Sample("aaa001")]);

        using JsonDocument document = JsonDocument.Parse(geoJson);
        JsonElement coordinates = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry")
            .GetProperty("coordinates");

        Assert.Equal(-122.3321, coordinates[0].GetDouble(), precision: 4);
        Assert.Equal(47.6062, coordinates[1].GetDouble(), precision: 4);
    }

    [Fact]
    public void GeoJson_includes_altitude_as_a_third_coordinate_in_metres()
    {
        string geoJson = AircraftExporter.ToGeoJson([
            Sample("aaa001") with { GeometricAltitudeFt = 10000 },
        ]);

        using JsonDocument document = JsonDocument.Parse(geoJson);
        JsonElement coordinates = document.RootElement
            .GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

        Assert.Equal(3, coordinates.GetArrayLength());
        Assert.Equal(3048.0, coordinates[2].GetDouble(), precision: 3);
    }

    [Fact]
    public void GeoJson_omits_the_third_coordinate_when_altitude_is_unknown()
    {
        // A zero here would claim the aircraft was at sea level.
        string geoJson = AircraftExporter.ToGeoJson([
            Sample("aaa001") with { GeometricAltitudeFt = null, BarometricAltitudeFt = null },
        ]);

        using JsonDocument document = JsonDocument.Parse(geoJson);
        JsonElement coordinates = document.RootElement
            .GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

        Assert.Equal(2, coordinates.GetArrayLength());
    }

    [Fact]
    public void Trail_geo_json_is_a_line_string_in_order()
    {
        string geoJson = AircraftExporter.TrailToGeoJson("aaa001", "TEST01", [
            (47.60, -122.30, 10000.0),
            (47.61, -122.31, 11000.0),
            (47.62, -122.32, 12000.0),
        ]);

        using JsonDocument document = JsonDocument.Parse(geoJson);
        JsonElement geometry = document.RootElement.GetProperty("geometry");

        Assert.Equal("LineString", geometry.GetProperty("type").GetString());

        JsonElement coordinates = geometry.GetProperty("coordinates");
        Assert.Equal(3, coordinates.GetArrayLength());

        // Longitude first here too.
        Assert.Equal(-122.30, coordinates[0][0].GetDouble(), precision: 4);
        Assert.Equal(47.60, coordinates[0][1].GetDouble(), precision: 4);
    }

    [Fact]
    public void Exporting_nothing_still_produces_valid_output()
    {
        Assert.Contains("icao_hex", AircraftExporter.ToCsv([]), StringComparison.Ordinal);

        using (JsonDocument json = JsonDocument.Parse(AircraftExporter.ToJson([])))
        {
            Assert.Equal(0, json.RootElement.GetArrayLength());
        }

        using JsonDocument geo = JsonDocument.Parse(AircraftExporter.ToGeoJson([]));
        Assert.Equal(0, geo.RootElement.GetProperty("features").GetArrayLength());
    }

    private static AircraftState Sample(string hex) => new()
    {
        Provider = "test",
        IcaoHex = hex,
        Callsign = "TST123",
        Registration = "N123TS",
        AircraftTypeCode = "B738",
        Latitude = 47.6062,
        Longitude = -122.3321,
        GeometricAltitudeFt = 35000,
        GroundSpeedKt = 450,
        TrackHeadingDeg = 180,
        VerticalRateFtPerMin = -640,
        Squawk = "1200",
        FirstSeen = Now,
        LastSeen = Now,
        PositionTimestamp = Now,
    };
}
