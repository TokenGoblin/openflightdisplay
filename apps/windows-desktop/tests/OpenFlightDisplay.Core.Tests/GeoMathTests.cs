namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Geo;
using Xunit;

/// <summary>
/// Parity tests for <see cref="GeoMath"/>.
///
/// The expected values here are the ones the existing implementations already
/// agree on — <c>firmware/display/test/native/test_geo/test_geo.cpp</c> and
/// <c>services/gateway/tests/ranking.test.ts</c>. If a change to the C# makes
/// these fail, the C# is wrong, not the test.
/// </summary>
public class GeoMathTests
{
    // Reference points used by the firmware suite.
    private const double SeattleLat = 47.6062;
    private const double SeattleLon = -122.3321;
    private const double PortlandLat = 45.5152;
    private const double PortlandLon = -122.6784;

    [Fact]
    public void EarthRadius_matches_the_other_implementations()
    {
        // Rounding this to 6371 shifts long-range distances by tens of metres
        // and silently breaks parity with the gateway and firmware.
        Assert.Equal(6371.0088, GeoMath.EarthRadiusKm);
    }

    [Fact]
    public void Distance_between_identical_points_is_zero()
    {
        double d = GeoMath.HaversineDistanceKm(SeattleLat, SeattleLon, SeattleLat, SeattleLon);
        Assert.Equal(0.0, d, precision: 9);
    }

    [Fact]
    public void Distance_seattle_to_portland_is_about_233_km()
    {
        double d = GeoMath.HaversineDistanceKm(SeattleLat, SeattleLon, PortlandLat, PortlandLon);
        Assert.InRange(d, 232.0, 235.0);
    }

    [Fact]
    public void Distance_is_symmetric()
    {
        double forward = GeoMath.HaversineDistanceKm(SeattleLat, SeattleLon, PortlandLat, PortlandLon);
        double reverse = GeoMath.HaversineDistanceKm(PortlandLat, PortlandLon, SeattleLat, SeattleLon);
        Assert.Equal(forward, reverse, precision: 9);
    }

    [Fact]
    public void Distance_across_the_antimeridian_is_short_not_a_lap_of_the_planet()
    {
        // 1 degree of longitude apart at the equator, straddling +/-180.
        double d = GeoMath.HaversineDistanceKm(0.0, 179.5, 0.0, -179.5);
        Assert.InRange(d, 110.0, 112.0);
    }

    [Fact]
    public void Bearing_due_north_is_zero()
    {
        double b = GeoMath.InitialBearingDeg(0.0, 0.0, 10.0, 0.0);
        Assert.Equal(0.0, b, precision: 6);
    }

    [Fact]
    public void Bearing_due_east_is_ninety()
    {
        double b = GeoMath.InitialBearingDeg(0.0, 0.0, 0.0, 10.0);
        Assert.Equal(90.0, b, precision: 6);
    }

    [Fact]
    public void Bearing_due_south_is_one_eighty()
    {
        double b = GeoMath.InitialBearingDeg(10.0, 0.0, 0.0, 0.0);
        Assert.Equal(180.0, b, precision: 6);
    }

    [Fact]
    public void Bearing_due_west_is_two_seventy_not_negative_ninety()
    {
        // The (bearing + 360) % 360 normalisation is what keeps this from being
        // -90. A negative bearing formats and sorts wrongly downstream.
        double b = GeoMath.InitialBearingDeg(0.0, 0.0, 0.0, -10.0);
        Assert.Equal(270.0, b, precision: 6);
    }

    [Theory]
    [InlineData(0.0, 0.0, 10.0, 10.0)]
    [InlineData(47.6, -122.3, 45.5, -122.6)]
    [InlineData(-33.8, 151.2, 35.6, 139.7)]
    public void Bearing_is_always_in_zero_to_360(double lat1, double lon1, double lat2, double lon2)
    {
        double b = GeoMath.InitialBearingDeg(lat1, lon1, lat2, lon2);
        Assert.InRange(b, 0.0, 360.0);
        Assert.False(double.IsNegative(b), "bearing must never be negative, including -0.0");
    }

    [Fact]
    public void Point_exactly_on_the_radius_is_inside_the_circle()
    {
        // Inclusive boundary, matching isWithinCircle in the gateway and firmware.
        double radius = GeoMath.HaversineDistanceKm(SeattleLat, SeattleLon, PortlandLat, PortlandLon);
        Assert.True(GeoMath.IsWithinCircle(PortlandLat, PortlandLon, SeattleLat, SeattleLon, radius));
    }

    [Fact]
    public void Point_beyond_the_radius_is_outside_the_circle()
    {
        Assert.False(GeoMath.IsWithinCircle(PortlandLat, PortlandLon, SeattleLat, SeattleLon, 50.0));
    }

    [Fact]
    public void Slant_range_of_an_overhead_aircraft_is_its_altitude_not_zero()
    {
        // The whole reason slant range exists: an aircraft directly overhead at
        // 37,000 ft is ~11.3 km away, not ~0 km.
        double slant = GeoMath.SlantRangeKm(horizontalKm: 0.0, altitudeFt: 37000.0);
        Assert.InRange(slant, 11.2, 11.4);
    }

    [Fact]
    public void Slant_range_at_ground_level_equals_horizontal_distance()
    {
        double slant = GeoMath.SlantRangeKm(horizontalKm: 25.0, altitudeFt: 0.0);
        Assert.Equal(25.0, slant, precision: 9);
    }
}
