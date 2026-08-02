namespace OpenFlightDisplay.App.Tests;

using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Units;
using Xunit;

/// <summary>
/// How an aircraft is rendered on the flight board.
/// </summary>
/// <remarks>
/// This is the last place the project's most load-bearing rule can be broken:
/// nullable means "not reported", never zero. Everything upstream preserves the
/// distinction carefully, and it would all be wasted if the view model printed
/// an unreported groundspeed as "0 kt".
/// </remarks>
public class AircraftRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unreported_value_never_renders_as_zero()
    {
        // The whole point. "0 kt" is a stationary aircraft; "not reported" is an
        // unknown one, and a board that draws them identically is lying.
        var row = Row(Aircraft() with
        {
            GroundSpeedKt = null,
            GeometricAltitudeFt = null,
            BarometricAltitudeFt = null,
            VerticalRateFtPerMin = null,
            Squawk = null,
            Registration = null,
            AircraftTypeCode = null,
        });

        Assert.DoesNotContain("0 kt", row.GroundSpeed, StringComparison.Ordinal);
        Assert.DoesNotContain("0 ft", row.Altitude, StringComparison.Ordinal);
        Assert.DoesNotContain('0', row.VerticalRate);

        // Each falls back to the same explicit "no data" marker.
        Assert.Equal(row.Registration, row.AircraftType);
        Assert.Equal(row.Registration, row.Squawk);
    }

    [Fact]
    public void A_zero_value_that_was_reported_is_shown_as_zero()
    {
        // The other half of the rule, and the one that would silently break if
        // somebody "simplified" a null check into a falsiness check.
        var row = Row(Aircraft() with { GroundSpeedKt = 0 });

        Assert.Contains("0", row.GroundSpeed, StringComparison.Ordinal);
    }

    [Fact]
    public void An_aircraft_with_no_callsign_falls_back_to_its_hex()
    {
        // Never a blank cell: a row must always identify its aircraft somehow.
        var row = Row(Aircraft() with { Callsign = null });

        Assert.Equal("ABC123", row.Callsign);
    }

    [Fact]
    public void The_hex_is_upper_cased_for_display()
        => Assert.Equal("ABC123", Row(Aircraft()).IcaoHex);

    [Fact]
    public void Ground_traffic_says_so_rather_than_showing_an_altitude()
    {
        var row = Row(Aircraft() with { OnGround = true, GeometricAltitudeFt = 20 });

        Assert.DoesNotContain("20", row.Altitude, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnitSystem.Aviation, "NM")]
    [InlineData(UnitSystem.Metric, "km")]
    [InlineData(UnitSystem.Imperial, "mi")]
    public void Distance_is_rendered_in_the_chosen_units(UnitSystem units, string expected)
    {
        var row = new AircraftRowViewModel(
            Aircraft() with { DistanceFromObserverKm = 50 }, units, Now);

        Assert.Contains(expected, row.Distance, StringComparison.Ordinal);
    }

    [Fact]
    public void An_emergency_is_carried_as_a_flag_and_as_words()
    {
        // Never colour alone - the flag drives the symbol, the label drives the
        // row, and a screen reader gets the text.
        var row = Row(Aircraft() with { EmergencyState = EmergencyState.Medical });

        Assert.True(row.HasEmergency);
        Assert.False(string.IsNullOrWhiteSpace(row.EmergencyLabel));
    }

    [Fact]
    public void A_normal_aircraft_has_no_emergency_label()
    {
        var row = Row(Aircraft());

        Assert.False(row.HasEmergency);
    }

    [Fact]
    public void An_unknown_vertical_trend_is_not_reported_as_level()
    {
        // "Level" and "we do not know" are different facts. The domain keeps
        // them apart and the row must not merge them.
        var unknown = Row(Aircraft() with { VerticalRateFtPerMin = null });
        var level = Row(Aircraft() with { VerticalRateFtPerMin = 0 });

        Assert.NotEqual(level.TrendLabel, unknown.TrendLabel);
    }

    [Fact]
    public void Staleness_is_carried_as_a_flag_not_inferred_from_age()
    {
        var stale = Row(Aircraft() with { DataQualityFlags = DataQualityFlags.StalePosition });

        Assert.True(stale.IsStale);
        Assert.False(Row(Aircraft()).IsStale);
    }

    [Fact]
    public void The_accessible_description_names_the_aircraft_and_its_position()
    {
        // Screen readers get the board rather than the radar, so this string is
        // the whole experience for someone not looking at the plot.
        var row = Row(Aircraft() with
        {
            DistanceFromObserverKm = 20,
            BearingFromObserverDeg = 90,
        });

        Assert.Contains("TST123", row.AccessibleDescription, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.AccessibleDescription));
    }

    [Fact]
    public void Age_is_measured_from_the_position_timestamp()
    {
        var row = new AircraftRowViewModel(
            Aircraft() with { PositionTimestamp = Now.AddSeconds(-42) }, UnitSystem.Aviation, Now);

        Assert.Equal(42, row.AgeSeconds);
    }

    private static AircraftRowViewModel Row(AircraftState aircraft)
        => new(aircraft, UnitSystem.Aviation, Now);

    private static AircraftState Aircraft() => new()
    {
        Provider = "test",
        IcaoHex = "abc123",
        Callsign = "TST123",
        Registration = "N12345",
        AircraftTypeCode = "B738",
        Squawk = "1200",
        Latitude = 47.61,
        Longitude = -122.33,
        GeometricAltitudeFt = 30000,
        GroundSpeedKt = 420,
        VerticalRateFtPerMin = 500,
        FirstSeen = Now,
        LastSeen = Now,
        PositionTimestamp = Now,
    };
}
