namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Units;
using Xunit;

/// <summary>
/// Unit conversion. The canonical internal representation is fixed — distance in
/// kilometres, altitude in feet, speed in knots, vertical rate in ft/min — and
/// conversion happens once, at the display boundary.
/// </summary>
public class UnitConverterTests
{
    [Fact]
    public void Metric_distance_is_the_canonical_value_unchanged()
        => Assert.Equal(100.0, UnitConverter.DistanceFromKm(100.0, UnitSystem.Metric));

    [Fact]
    public void One_hundred_km_is_about_62_statute_miles()
        => Assert.Equal(62.137, UnitConverter.DistanceFromKm(100.0, UnitSystem.Imperial), precision: 3);

    [Fact]
    public void One_hundred_km_is_about_54_nautical_miles()
        => Assert.Equal(53.996, UnitConverter.DistanceFromKm(100.0, UnitSystem.Aviation), precision: 3);

    [Theory]
    [InlineData(UnitSystem.Metric)]
    [InlineData(UnitSystem.Imperial)]
    [InlineData(UnitSystem.Aviation)]
    public void Distance_round_trips_through_display_units(UnitSystem system)
    {
        double displayed = UnitConverter.DistanceFromKm(123.456, system);
        double back = UnitConverter.DistanceToKm(displayed, system);

        Assert.Equal(123.456, back, precision: 9);
    }

    [Fact]
    public void Aviation_altitude_stays_in_feet()
        => Assert.Equal(35000.0, UnitConverter.AltitudeFromFeet(35000.0, UnitSystem.Aviation));

    [Fact]
    public void Imperial_altitude_also_stays_in_feet()
    {
        // Not an oversight. Flight levels are feet worldwide.
        Assert.Equal(35000.0, UnitConverter.AltitudeFromFeet(35000.0, UnitSystem.Imperial));
    }

    [Fact]
    public void Metric_altitude_converts_to_metres()
        => Assert.Equal(10668.0, UnitConverter.AltitudeFromFeet(35000.0, UnitSystem.Metric), precision: 6);

    [Fact]
    public void Aviation_speed_stays_in_knots()
        => Assert.Equal(450.0, UnitConverter.SpeedFromKnots(450.0, UnitSystem.Aviation));

    [Fact]
    public void Metric_speed_converts_knots_to_kilometres_per_hour()
        => Assert.Equal(833.4, UnitConverter.SpeedFromKnots(450.0, UnitSystem.Metric), precision: 6);

    [Fact]
    public void Imperial_speed_converts_knots_to_miles_per_hour()
        => Assert.Equal(517.85, UnitConverter.SpeedFromKnots(450.0, UnitSystem.Imperial), precision: 2);

    [Fact]
    public void Metric_vertical_rate_is_metres_per_second_not_per_minute()
    {
        // m/s is the convention for vertical speed indicators outside the US.
        // 1000 ft/min is about 5.08 m/s.
        Assert.Equal(5.08, UnitConverter.VerticalRateFromFeetPerMinute(1000.0, UnitSystem.Metric), precision: 2);
    }

    [Fact]
    public void Aviation_vertical_rate_stays_in_feet_per_minute()
        => Assert.Equal(1000.0, UnitConverter.VerticalRateFromFeetPerMinute(1000.0, UnitSystem.Aviation));

    [Fact]
    public void A_negative_vertical_rate_stays_negative_when_converted()
    {
        // Sign carries the climb/descend distinction; losing it would render a
        // descent as a climb.
        Assert.True(UnitConverter.VerticalRateFromFeetPerMinute(-1000.0, UnitSystem.Metric) < 0);
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "km", "m", "km/h", "m/s")]
    [InlineData(UnitSystem.Imperial, "mi", "ft", "mph", "ft/min")]
    [InlineData(UnitSystem.Aviation, "NM", "ft", "kt", "ft/min")]
    public void Unit_labels_match_the_system(
        UnitSystem system,
        string distance,
        string altitude,
        string speed,
        string verticalRate)
    {
        Assert.Equal(distance, UnitConverter.DistanceUnitLabel(system));
        Assert.Equal(altitude, UnitConverter.AltitudeUnitLabel(system));
        Assert.Equal(speed, UnitConverter.SpeedUnitLabel(system));
        Assert.Equal(verticalRate, UnitConverter.VerticalRateUnitLabel(system));
    }

    [Fact]
    public void Zero_converts_to_zero_in_every_system()
    {
        foreach (UnitSystem system in Enum.GetValues<UnitSystem>())
        {
            Assert.Equal(0.0, UnitConverter.DistanceFromKm(0.0, system));
            Assert.Equal(0.0, UnitConverter.SpeedFromKnots(0.0, system));
            Assert.Equal(0.0, UnitConverter.AltitudeFromFeet(0.0, system));
        }
    }
}
