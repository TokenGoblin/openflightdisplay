namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Areas;
using Xunit;

/// <summary>
/// The saved form of the monitoring area. What matters is that an area which
/// cannot contain anything is refused rather than silently showing an empty sky.
/// </summary>
public class MonitoringAreaSettingTests
{
    private const double HomeLat = 47.6062;
    private const double HomeLon = -122.3321;

    [Fact]
    public void A_default_setting_is_a_circle_on_the_home_location()
    {
        var setting = new MonitoringAreaSetting();

        var area = Assert.IsType<CircleArea>(setting.Build(HomeLat, HomeLon));

        Assert.Equal(HomeLat, area.CenterLat);
        Assert.Equal(HomeLon, area.CenterLon);
        Assert.Equal(80.0, area.RadiusKm);
    }

    [Fact]
    public void Moving_home_moves_a_home_centred_area()
    {
        // The reason the centre is nullable rather than copied. Duplicating the
        // coordinates would leave the area over the old address after a move.
        var setting = new MonitoringAreaSetting();

        var moved = Assert.IsType<CircleArea>(setting.Build(51.4775, -0.4614));

        Assert.Equal(51.4775, moved.CenterLat);
    }

    [Fact]
    public void An_explicit_centre_overrides_home()
    {
        var setting = new MonitoringAreaSetting { CenterLat = 51.4775, CenterLon = -0.4614 };

        var area = Assert.IsType<CircleArea>(setting.Build(HomeLat, HomeLon));

        Assert.Equal(51.4775, area.CenterLat);
    }

    [Fact]
    public void A_centre_relative_area_with_no_home_builds_nothing_rather_than_a_circle_at_zero()
    {
        // 0N 0E is a real coordinate in the Gulf of Guinea. Defaulting there
        // would show an empty sky instead of prompting for a location.
        var setting = new MonitoringAreaSetting();

        Assert.Null(setting.Build(null, null));
    }

    [Fact]
    public void A_cone_builds_with_its_heading_and_width()
    {
        var setting = new MonitoringAreaSetting
        {
            Shape = AreaShape.Cone,
            RadiusKm = 50,
            HeadingDeg = 90,
            WidthDeg = 60,
        };

        var cone = Assert.IsType<ConeArea>(setting.Build(HomeLat, HomeLon));

        Assert.Equal(90, cone.HeadingDeg);
        Assert.Equal(60, cone.WidthDeg);

        // Due east is inside; due west is not.
        Assert.True(cone.ContainsPosition(HomeLat, HomeLon + 0.2));
        Assert.False(cone.ContainsPosition(HomeLat, HomeLon - 0.2));
    }

    [Fact]
    public void A_polygon_builds_from_its_vertices_and_needs_no_home()
    {
        var setting = new MonitoringAreaSetting
        {
            Shape = AreaShape.Polygon,
            Vertices =
            [
                new GeoPoint(47.5, -122.5),
                new GeoPoint(47.7, -122.5),
                new GeoPoint(47.7, -122.1),
                new GeoPoint(47.5, -122.1),
            ],
        };

        var polygon = Assert.IsType<PolygonArea>(setting.Build(null, null));

        Assert.Equal(4, polygon.Vertices.Count);
        Assert.True(polygon.ContainsPosition(47.6, -122.3));
        Assert.False(polygon.ContainsPosition(48.5, -122.3));
    }

    [Fact]
    public void An_altitude_band_reaches_the_built_area()
    {
        var setting = new MonitoringAreaSetting { MinAltitudeFt = 5000, MaxAltitudeFt = 30000 };

        MonitoringArea area = setting.Build(HomeLat, HomeLon)!;

        Assert.True(area.ContainsAltitude(10000));
        Assert.False(area.ContainsAltitude(1000));
        Assert.False(area.ContainsAltitude(40000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(501)]
    public void An_unusable_radius_is_rejected(double radiusKm)
        => Assert.NotNull(new MonitoringAreaSetting { RadiusKm = radiusKm }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(361)]
    public void An_unusable_cone_width_is_rejected(double widthDeg)
        => Assert.NotNull(
            new MonitoringAreaSetting { Shape = AreaShape.Cone, WidthDeg = widthDeg }.Validate());

    [Fact]
    public void A_polygon_with_too_few_points_is_rejected()
    {
        var setting = new MonitoringAreaSetting
        {
            Shape = AreaShape.Polygon,
            Vertices = [new GeoPoint(47.5, -122.5), new GeoPoint(47.7, -122.5)],
        };

        Assert.Contains("at least 3", setting.Validate()!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inverted_altitude_band_is_rejected()
        => Assert.NotNull(
            new MonitoringAreaSetting { MinAltitudeFt = 30000, MaxAltitudeFt = 5000 }.Validate());

    [Fact]
    public void An_out_of_range_coordinate_is_rejected()
        => Assert.NotNull(new MonitoringAreaSetting { CenterLat = 95, CenterLon = 0 }.Validate());

    [Fact]
    public void An_invalid_setting_builds_nothing()
        => Assert.Null(new MonitoringAreaSetting { RadiusKm = -1 }.Build(HomeLat, HomeLon));

    [Fact]
    public void Two_areas_with_the_same_outline_are_equal()
    {
        // The generated record equality compared Vertices by reference, so an
        // area was not even equal to itself after a save and reload: writing
        // produced an array and reading produced a list.
        var a = new MonitoringAreaSetting
        {
            Shape = AreaShape.Polygon,
            Vertices = new[] { new GeoPoint(1, 2), new GeoPoint(3, 4), new GeoPoint(5, 6) },
        };

        var b = new MonitoringAreaSetting
        {
            Shape = AreaShape.Polygon,
            Vertices = new List<GeoPoint> { new(1, 2), new(3, 4), new(5, 6) },
        };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Areas_with_different_outlines_are_not_equal()
    {
        var a = new MonitoringAreaSetting
        {
            Shape = AreaShape.Polygon,
            Vertices = [new GeoPoint(1, 2), new GeoPoint(3, 4), new GeoPoint(5, 6)],
        };

        var b = a with { Vertices = [new GeoPoint(1, 2), new GeoPoint(3, 4), new GeoPoint(9, 9)] };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_summary_names_the_shape_and_the_altitude_band()
    {
        var setting = new MonitoringAreaSetting
        {
            Shape = AreaShape.Cone,
            RadiusKm = 50,
            HeadingDeg = 90,
            WidthDeg = 60,
            MinAltitudeFt = 5000,
        };

        string summary = setting.Summarise();

        Assert.Contains("Cone", summary, StringComparison.Ordinal);
        Assert.Contains("60", summary, StringComparison.Ordinal);
        Assert.Contains("5,000 ft", summary, StringComparison.Ordinal);
    }
}
