namespace OpenFlightDisplay.Providers.Tests;

using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Providers.AdsbLol;
using Xunit;

/// <summary>
/// Contract tests for the adsb.lol response mapping.
///
/// Deliberately runs against literal fixture bodies rather than the live API —
/// normal automated tests must not depend on an external service. The three
/// documented response quirks each get a dedicated test, because each one was a
/// real bug in the original implementation.
/// </summary>
public class AdsbLolNormalizerTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    // ---- quirk 1: space-padded callsigns ----

    [Fact]
    public void Trims_the_space_padding_adsb_lol_puts_around_callsigns()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "flight": "UAL1234" }
            """);

        Assert.Equal("UAL1234", aircraft.Callsign);
    }

    [Fact]
    public void A_callsign_of_only_spaces_is_treated_as_absent()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "flight": "        " }
            """);

        Assert.Null(aircraft.Callsign);
        Assert.True(aircraft.DataQualityFlags.HasFlag(DataQualityFlags.NoCallsign));
    }

    // ---- quirk 2: alt_baro is the string "ground" ----

    [Fact]
    public void Reads_the_string_ground_as_on_ground_rather_than_failing_to_parse()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "alt_baro": "ground" }
            """);

        Assert.True(aircraft.OnGround);
        Assert.Null(aircraft.BarometricAltitudeFt);
        Assert.True(aircraft.DataQualityFlags.HasFlag(DataQualityFlags.NoAltitude));
    }

    [Fact]
    public void A_numeric_alt_baro_is_an_altitude_and_not_on_ground()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "alt_baro": 35000 }
            """);

        Assert.False(aircraft.OnGround);
        Assert.Equal(35000.0, aircraft.BarometricAltitudeFt);
    }

    // ---- quirk 3: records that omit `flight` entirely ----

    [Fact]
    public void A_record_with_no_flight_field_is_kept_and_flagged_not_dropped()
    {
        // Missing enrichment must never suppress an otherwise valid aircraft.
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "alt_baro": 12000 }
            """);

        Assert.Null(aircraft.Callsign);
        Assert.True(aircraft.DataQualityFlags.HasFlag(DataQualityFlags.NoCallsign));
    }

    // ---- identity and position validation ----

    [Fact]
    public void Strips_the_tilde_prefix_from_non_icao_addresses()
    {
        var aircraft = ParseOne("""
            { "hex": "~abc123", "lat": 47.6, "lon": -122.3 }
            """);

        Assert.Equal("abc123", aircraft.IcaoHex);
    }

    [Fact]
    public void Lowercases_the_icao_hex()
    {
        var aircraft = ParseOne("""
            { "hex": "ABC123", "lat": 47.6, "lon": -122.3 }
            """);

        Assert.Equal("abc123", aircraft.IcaoHex);
    }

    [Theory]
    [InlineData(""" { "lat": 47.6, "lon": -122.3 } """)]
    [InlineData(""" { "hex": "abc", "lat": 47.6, "lon": -122.3 } """)]
    [InlineData(""" { "hex": "zzzzzz", "lat": 47.6, "lon": -122.3 } """)]
    [InlineData(""" { "hex": "abc123", "lon": -122.3 } """)]
    [InlineData(""" { "hex": "abc123", "lat": 47.6 } """)]
    public void Drops_records_with_no_usable_identity_or_position(string json)
    {
        // Better to drop than to emit a garbage aircraft.
        Assert.Empty(Parse($$"""{ "ac": [ {{json}} ] }"""));
    }

    // ---- unexpected field types ----

    [Fact]
    public void A_wrong_typed_numeric_field_is_treated_as_absent_not_fatal()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "gs": "fast", "track": null }
            """);

        Assert.Null(aircraft.GroundSpeedKt);
        Assert.Null(aircraft.TrackHeadingDeg);
    }

    [Fact]
    public void A_non_object_entry_in_the_aircraft_array_is_skipped()
    {
        var result = Parse("""
            { "ac": [ 42, "nonsense", null,
                      { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3 } ] }
            """);

        Assert.Equal("a1b2c3", Assert.Single(result).IcaoHex);
    }

    // ---- squawk ----

    [Theory]
    [InlineData("7700", "7700")]
    [InlineData("1200", "1200")]
    public void Accepts_a_valid_octal_squawk(string raw, string expected)
    {
        var aircraft = ParseOne($$"""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "squawk": "{{raw}}" }
            """);

        Assert.Equal(expected, aircraft.Squawk);
    }

    [Theory]
    [InlineData("8888")]
    [InlineData("129")]
    [InlineData("12345")]
    [InlineData("abcd")]
    public void Rejects_a_squawk_that_is_not_four_octal_digits(string raw)
    {
        var aircraft = ParseOne($$"""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "squawk": "{{raw}}" }
            """);

        Assert.Null(aircraft.Squawk);
    }

    // ---- vertical rate precedence ----

    [Fact]
    public void Prefers_baro_rate_over_geom_rate()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3,
              "baro_rate": -1200, "geom_rate": 900 }
            """);

        Assert.Equal(-1200.0, aircraft.VerticalRateFtPerMin);
    }

    [Fact]
    public void Falls_back_to_geom_rate_when_baro_rate_is_absent()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "geom_rate": 900 }
            """);

        Assert.Equal(900.0, aircraft.VerticalRateFtPerMin);
    }

    // ---- category ----

    [Theory]
    [InlineData("A3", AircraftCategory.FixedWing)]
    [InlineData("B1", AircraftCategory.Glider)]
    [InlineData("B2", AircraftCategory.Balloon)]
    [InlineData("B6", AircraftCategory.Drone)]
    [InlineData("B7", AircraftCategory.Rotorcraft)]
    [InlineData("C1", AircraftCategory.GroundVehicle)]
    [InlineData("Z9", AircraftCategory.Unknown)]
    public void Maps_the_provider_category_codes(string raw, AircraftCategory expected)
    {
        var aircraft = ParseOne($$"""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "category": "{{raw}}" }
            """);

        Assert.Equal(expected, aircraft.AircraftCategory);
    }

    [Fact]
    public void An_absent_category_is_null_rather_than_unknown()
    {
        // "The provider said nothing" and "the provider said something we don't
        // recognise" are different facts, and a category filter must be able to
        // tell them apart.
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3 }
            """);

        Assert.Null(aircraft.AircraftCategory);
    }

    // ---- position age ----

    [Fact]
    public void Uses_seen_pos_to_backdate_the_position_timestamp()
    {
        // Stamping every record as fresh at poll time would make a 90-second-old
        // position look live and defeat the staleness rules entirely.
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3, "seen_pos": 45.0 }
            """);

        Assert.Equal(ObservedAt.AddSeconds(-45), aircraft.PositionTimestamp);
    }

    [Fact]
    public void Falls_back_to_the_observation_time_when_seen_pos_is_absent()
    {
        var aircraft = ParseOne("""
            { "hex": "a1b2c3", "lat": 47.6, "lon": -122.3 }
            """);

        Assert.Equal(ObservedAt, aircraft.PositionTimestamp);
    }

    // ---- whole-response shapes ----

    [Fact]
    public void An_empty_aircraft_array_is_a_successful_empty_result()
        => Assert.Empty(Parse("""{ "ac": [], "msg": "No error", "total": 0 }"""));

    [Fact]
    public void A_body_with_no_ac_key_yields_an_empty_list_rather_than_throwing()
        => Assert.Empty(Parse("""{ "msg": "No error", "total": 0 }"""));

    [Fact]
    public void A_body_where_ac_is_not_an_array_yields_an_empty_list()
        => Assert.Empty(Parse("""{ "ac": "unexpected" }"""));

    [Fact]
    public void A_json_array_at_the_root_yields_an_empty_list()
        => Assert.Empty(Parse("""[ { "hex": "a1b2c3" } ]"""));

    [Fact]
    public void Malformed_json_throws_so_the_provider_can_report_invalid_response()
    {
        // The one case that does throw: the provider adapter catches JsonException
        // and turns it into FeedFailure.InvalidResponse.
        //
        // ThrowsAny, not Throws: System.Text.Json raises the derived
        // JsonReaderException, and asserting the exact type would pass only by
        // accident of which malformed input was chosen.
        Assert.ThrowsAny<JsonException>(() => Parse("{ this is not json"));
    }

    [Fact]
    public void Parses_a_realistic_multi_aircraft_response()
    {
        var result = Parse("""
            {
              "ac": [
                { "hex": "a1b2c3", "flight": "UAL1234 ", "lat": 47.61, "lon": -122.33,
                  "alt_baro": 35000, "gs": 450.2, "track": 182.4, "baro_rate": -640,
                  "squawk": "1200", "category": "A3", "r": "N12345", "t": "B738",
                  "seen_pos": 3.2 },
                { "hex": "d4e5f6", "lat": 47.55, "lon": -122.40, "alt_baro": "ground",
                  "gs": 8.0, "category": "C1" },
                { "hex": "999999", "lat": 47.70, "lon": -122.20, "alt_geom": 4200,
                  "emergency": "general", "squawk": "7700" }
              ],
              "msg": "No error",
              "total": 3
            }
            """);

        Assert.Equal(3, result.Count);

        Assert.Equal("UAL1234", result[0].Callsign);
        Assert.Equal(35000.0, result[0].BarometricAltitudeFt);
        Assert.Equal(VerticalTrend.Descending, result[0].VerticalTrend);

        Assert.True(result[1].OnGround);
        Assert.Equal(VerticalTrend.OnGround, result[1].VerticalTrend);

        Assert.Equal(EmergencyState.General, result[2].EmergencyState);
        Assert.Equal("7700", result[2].Squawk);

        // No vertical rate reported: Unknown, not Level.
        Assert.Equal(VerticalTrend.Unknown, result[2].VerticalTrend);
    }

    private static IReadOnlyList<AircraftState> Parse(string json)
        => AdsbLolNormalizer.ParseResponse(json, "adsblol", ObservedAt);

    private static AircraftState ParseOne(string aircraftJson)
        => Assert.Single(Parse($$"""{ "ac": [ {{aircraftJson}} ] }"""));
}
