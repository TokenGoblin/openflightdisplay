namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Geo;
using Xunit;

/// <summary>
/// Tile maths for the radar backdrop. The property that matters is that the map
/// lands where the aircraft do — a backdrop offset from the symbols is worse
/// than no backdrop, because it looks authoritative.
/// </summary>
public class SlippyMapTests
{
    private const double SeattleLat = 47.6062;
    private const double SeattleLon = -122.3321;

    [Fact]
    public void The_world_is_one_tile_at_zoom_zero()
    {
        // Null Island sits at the centre of the single zoom-0 tile.
        (double x, double y) = SlippyMap.WorldPixel(0, 0, 0);

        Assert.Equal(128, x, 6);
        Assert.Equal(128, y, 6);
    }

    [Theory]
    [InlineData(-180, 0)]
    [InlineData(0, 128)]
    [InlineData(180, 256)]
    public void Longitude_maps_linearly_across_the_world(double lon, double expectedX)
        => Assert.Equal(expectedX, SlippyMap.WorldPixel(0, lon, 0).X, 6);

    [Fact]
    public void Northern_latitudes_map_above_the_equator()
    {
        double equator = SlippyMap.WorldPixel(0, 0, 8).Y;
        double north = SlippyMap.WorldPixel(47.6062, 0, 8).Y;

        // Y grows downward in the tile scheme.
        Assert.True(north < equator, $"expected {north} above {equator}");
    }

    [Fact]
    public void The_poles_do_not_produce_infinity()
    {
        // Mercator is undefined at the poles; the latitude is clamped instead.
        (double _, double y) = SlippyMap.WorldPixel(90, 0, 4);

        Assert.False(double.IsNaN(y));
        Assert.False(double.IsInfinity(y));
    }

    [Fact]
    public void Resolution_halves_with_each_zoom_level()
    {
        double atEight = SlippyMap.MetresPerPixel(0, 8);
        double atNine = SlippyMap.MetresPerPixel(0, 9);

        Assert.Equal(atEight / 2, atNine, 6);
    }

    [Fact]
    public void Resolution_shrinks_with_latitude()
    {
        // A pixel covers less ground further from the equator, which is why the
        // zoom choice has to know the latitude.
        Assert.True(SlippyMap.MetresPerPixel(60, 10) < SlippyMap.MetresPerPixel(0, 10));
    }

    [Fact]
    public void The_chosen_zoom_is_never_finer_than_asked_for()
    {
        // Rounding down keeps tiles from being upscaled into a blurry mess.
        for (int zoom = SlippyMap.MinZoom; zoom <= SlippyMap.MaxZoom; zoom++)
        {
            double target = SlippyMap.MetresPerPixel(SeattleLat, zoom);
            int chosen = SlippyMap.ZoomForResolution(SeattleLat, target);

            Assert.True(
                SlippyMap.MetresPerPixel(SeattleLat, chosen) >= target - 1e-6,
                $"zoom {chosen} is finer than the requested {target} m/px");
        }
    }

    [Fact]
    public void The_zoom_is_clamped_to_the_polite_range()
    {
        // A radar covering tens of km never needs building-level detail, and
        // asking for it would mean hundreds of tiles from donated bandwidth.
        Assert.Equal(SlippyMap.MaxZoom, SlippyMap.ZoomForResolution(SeattleLat, 0.01));
        Assert.Equal(SlippyMap.MinZoom, SlippyMap.ZoomForResolution(SeattleLat, 100000));
    }

    [Fact]
    public void The_cover_is_centred_on_the_observer()
    {
        // The whole point. The observer marker sits at the centre of the plot,
        // so the map pixel for the observer's coordinates must land there too.
        const double width = 800;
        const double height = 600;
        double metresPerPixel = 80_000.0 / 350.0;

        var placements = SlippyMap.Cover(
            SeattleLat, SeattleLon, width, height, metresPerPixel);

        Assert.NotEmpty(placements);

        // Find where the observer falls inside whichever tile contains them.
        int zoom = placements[0].Tile.Zoom;
        (double worldX, double worldY) = SlippyMap.WorldPixel(SeattleLat, SeattleLon, zoom);
        double scale = placements[0].Size / SlippyMap.TileSizePx;

        TilePlacement host = placements.First(p =>
        {
            double tileWorldX = p.Tile.X * SlippyMap.TileSizePx;
            double tileWorldY = p.Tile.Y * SlippyMap.TileSizePx;
            return worldX >= tileWorldX && worldX < tileWorldX + SlippyMap.TileSizePx
                && worldY >= tileWorldY && worldY < tileWorldY + SlippyMap.TileSizePx;
        });

        double observerOnPlotX = host.Left + ((worldX - (host.Tile.X * SlippyMap.TileSizePx)) * scale);
        double observerOnPlotY = host.Top + ((worldY - (host.Tile.Y * SlippyMap.TileSizePx)) * scale);

        Assert.Equal(width / 2, observerOnPlotX, 3);
        Assert.Equal(height / 2, observerOnPlotY, 3);
    }

    [Fact]
    public void The_cover_spans_the_whole_plot()
    {
        var placements = SlippyMap.Cover(SeattleLat, SeattleLon, 800, 600, 80_000.0 / 350.0);

        double left = placements.Min(p => p.Left);
        double top = placements.Min(p => p.Top);
        double right = placements.Max(p => p.Left + p.Size);
        double bottom = placements.Max(p => p.Top + p.Size);

        // No gap at any edge, or the backdrop would show bare canvas.
        Assert.True(left <= 0, $"left edge uncovered: {left}");
        Assert.True(top <= 0, $"top edge uncovered: {top}");
        Assert.True(right >= 800, $"right edge uncovered: {right}");
        Assert.True(bottom >= 600, $"bottom edge uncovered: {bottom}");
    }

    [Fact]
    public void The_tile_count_is_capped()
    {
        // Someone else's bandwidth. A bound, not a suggestion.
        var placements = SlippyMap.Cover(
            SeattleLat, SeattleLon, 4000, 4000, 1.0, maxTiles: 12);

        Assert.True(placements.Count <= 12, $"got {placements.Count} tiles");
    }

    [Fact]
    public void Tiles_never_fall_outside_the_world_vertically()
    {
        // Wrapping vertically would draw the far hemisphere above the pole.
        var placements = SlippyMap.Cover(84.0, 0, 1200, 1200, 500);

        foreach (TilePlacement p in placements)
        {
            int span = 1 << p.Tile.Zoom;
            Assert.InRange(p.Tile.Y, 0, span - 1);
        }
    }

    [Fact]
    public void Tiles_wrap_around_the_antimeridian()
    {
        // A plot straddling 180 degrees takes tiles from the far edge of the
        // world rather than leaving half the backdrop blank.
        var placements = SlippyMap.Cover(0, 179.99, 1200, 400, 2000);

        Assert.NotEmpty(placements);

        foreach (TilePlacement p in placements)
        {
            int span = 1 << p.Tile.Zoom;
            Assert.InRange(p.Tile.X, 0, span - 1);
        }
    }

    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(100, 0, 10)]
    [InlineData(100, 100, 0)]
    public void Nonsense_input_yields_no_tiles_rather_than_throwing(
        double width, double height, double metresPerPixel)
        => Assert.Empty(SlippyMap.Cover(SeattleLat, SeattleLon, width, height, metresPerPixel));
}
