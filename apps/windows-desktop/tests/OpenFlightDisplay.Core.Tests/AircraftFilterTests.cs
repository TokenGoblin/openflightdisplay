namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Ranking;
using Xunit;

/// <summary>
/// Display filtering. The case that matters most is what happens to an aircraft
/// that never reported the thing being filtered on.
/// </summary>
public class AircraftFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_filter_admits_everything()
    {
        Assert.True(AircraftFilter.None.IsEmpty);
        Assert.True(AircraftFilter.None.Admits(Aircraft()));
    }

    [Fact]
    public void An_unreported_altitude_is_never_filtered_out()
    {
        // The whole point. An aircraft with no altitude is not at zero feet, and
        // excluding it would hide traffic on the basis of a reading nobody took.
        var filter = new AircraftFilter { MinAltitudeFt = 10000 };

        AircraftState unknown = Aircraft() with
        {
            GeometricAltitudeFt = null,
            BarometricAltitudeFt = null,
        };

        Assert.Null(unknown.AltitudeFt);
        Assert.True(filter.Admits(unknown));
    }

    [Theory]
    [InlineData(5000, false)]
    [InlineData(10000, true)]
    [InlineData(30000, true)]
    public void A_minimum_altitude_hides_what_is_reported_below_it(double altitude, bool admitted)
    {
        var filter = new AircraftFilter { MinAltitudeFt = 10000 };

        Assert.Equal(admitted, filter.Admits(Aircraft() with { GeometricAltitudeFt = altitude }));
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(10000, true)]
    [InlineData(30000, false)]
    public void A_maximum_altitude_hides_what_is_reported_above_it(double altitude, bool admitted)
    {
        var filter = new AircraftFilter { MaxAltitudeFt = 10000 };

        Assert.Equal(admitted, filter.Admits(Aircraft() with { GeometricAltitudeFt = altitude }));
    }

    [Fact]
    public void Excluding_ground_traffic_uses_the_reported_flag_not_a_low_altitude()
    {
        // Inferring "on the ground" from altitude would hide aircraft on short
        // final, which are exactly the ones worth seeing.
        var filter = new AircraftFilter { ExcludeOnGround = true };

        Assert.False(filter.Admits(Aircraft() with { OnGround = true }));
        Assert.True(filter.Admits(Aircraft() with { OnGround = false, GeometricAltitudeFt = 200 }));
    }

    [Fact]
    public void Requiring_a_callsign_hides_aircraft_that_never_sent_one()
    {
        var filter = new AircraftFilter { RequireCallsign = true };

        Assert.False(filter.Admits(Aircraft() with { Callsign = null }));
        Assert.False(filter.Admits(Aircraft() with { Callsign = "   " }));
        Assert.True(filter.Admits(Aircraft() with { Callsign = "UAL1234" }));
    }

    [Fact]
    public void Emergency_only_hides_everything_normal()
    {
        var filter = new AircraftFilter { EmergencyOnly = true };

        Assert.False(filter.Admits(Aircraft()));
        Assert.True(filter.Admits(Aircraft() with { EmergencyState = EmergencyState.General }));
    }

    [Fact]
    public void Apply_leaves_the_sequence_untouched_when_nothing_is_configured()
    {
        var aircraft = new[] { Aircraft(), Aircraft() with { IcaoHex = "def456" } };

        Assert.Equal(2, AircraftFilter.None.Apply(aircraft).Count());
    }

    [Fact]
    public void An_impossible_altitude_band_is_reported_rather_than_silently_hiding_everything()
    {
        var filter = new AircraftFilter { MinAltitudeFt = 30000, MaxAltitudeFt = 5000 };

        Assert.NotNull(filter.Validate());
    }

    [Fact]
    public void A_sensible_band_validates()
    {
        var filter = new AircraftFilter { MinAltitudeFt = 5000, MaxAltitudeFt = 30000 };

        Assert.Null(filter.Validate());
    }

    [Fact]
    public void The_summary_says_what_is_being_hidden()
    {
        // Shown on screen so a user cannot forget a filter is on and conclude
        // the sky is empty.
        var filter = new AircraftFilter { MinAltitudeFt = 5000, ExcludeOnGround = true };

        string summary = filter.Summarise();

        Assert.Contains("5,000 ft", summary, StringComparison.Ordinal);
        Assert.Contains("airborne only", summary, StringComparison.Ordinal);
    }

    private static AircraftState Aircraft() => new()
    {
        Provider = "test",
        IcaoHex = "abc123",
        Callsign = "UAL1234",
        Latitude = 47.61,
        Longitude = -122.33,
        GeometricAltitudeFt = 30000,
        GroundSpeedKt = 420,
        FirstSeen = Now,
        LastSeen = Now,
        PositionTimestamp = Now,
    };
}
