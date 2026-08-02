namespace OpenFlightDisplay.Core.Geo;

/// <summary>One raster tile in the standard slippy-map scheme.</summary>
public readonly record struct TileId(int Zoom, int X, int Y);

/// <summary>Where a tile goes on the plot, in device-independent pixels.</summary>
/// <param name="Size">
/// Drawn size. Usually <b>not</b> 256: the tile is scaled so the map's metres
/// per pixel matches the radar's, which is what keeps a symbol over the road it
/// is actually above.
/// </param>
public readonly record struct TilePlacement(TileId Tile, double Left, double Top, double Size);

/// <summary>
/// Web Mercator tile maths for the radar's map backdrop.
/// </summary>
/// <remarks>
/// <para>
/// Pure geometry, no network and no I/O, so the part that decides <i>where</i>
/// imagery lands is testable without fetching anything. Fetching and caching
/// live in the infrastructure layer.
/// </para>
/// <para>
/// <b>The radar stays the source of truth for scale.</b> Tiles are chosen and
/// scaled to match the radar's existing pixels-per-kilometre rather than the
/// radar being rebuilt around a map, so range rings keep meaning exactly what
/// they meant before and aircraft positions are unaffected by whether the
/// backdrop drew.
/// </para>
/// </remarks>
public static class SlippyMap
{
    /// <summary>Edge length of a standard raster tile, in pixels.</summary>
    public const int TileSizePx = 256;

    /// <summary>Equatorial circumference in metres, WGS-84.</summary>
    public const double EarthCircumferenceM = 40075016.686;

    /// <summary>
    /// Coarsest zoom worth requesting. Below this the whole continent is one
    /// tile and the backdrop tells the user nothing.
    /// </summary>
    public const int MinZoom = 3;

    /// <summary>
    /// Finest zoom this feature requests.
    /// </summary>
    /// <remarks>
    /// Deliberately well short of OpenStreetMap's maximum. A radar covering tens
    /// of kilometres never needs building-level detail, and asking for it would
    /// mean hundreds of tiles from a service running on donated bandwidth.
    /// </remarks>
    public const int MaxZoom = 14;

    /// <summary>Ground resolution at a given zoom and latitude, in metres per pixel.</summary>
    public static double MetresPerPixel(double latitudeDeg, int zoom)
        => EarthCircumferenceM
            * Math.Cos(latitudeDeg * Math.PI / 180.0)
            / (TileSizePx * Math.Pow(2, zoom));

    /// <summary>
    /// Finest zoom whose tiles are no finer than the requested resolution.
    /// </summary>
    /// <remarks>
    /// Rounded <b>down</b> so tiles are never upscaled past their native
    /// resolution into a blurry mess; a slightly coarser tile scaled up a little
    /// looks better than a fine one that had to be invented.
    /// </remarks>
    public static int ZoomForResolution(double latitudeDeg, double metresPerPixel)
    {
        if (metresPerPixel <= 0)
        {
            return MaxZoom;
        }

        double exact = Math.Log2(
            EarthCircumferenceM
            * Math.Cos(latitudeDeg * Math.PI / 180.0)
            / (TileSizePx * metresPerPixel));

        return Math.Clamp((int)Math.Floor(exact), MinZoom, MaxZoom);
    }

    /// <summary>
    /// Position in the global pixel plane at a zoom level.
    /// </summary>
    /// <remarks>
    /// Fractional on purpose. Truncating here would snap the map to whole tiles
    /// and put the observer up to a hundred metres from the centre of their own
    /// radar.
    /// </remarks>
    public static (double X, double Y) WorldPixel(double latitudeDeg, double longitudeDeg, int zoom)
    {
        double scale = TileSizePx * Math.Pow(2, zoom);

        // Latitude is clamped to the Mercator limit; the projection is undefined
        // at the poles and would otherwise produce infinity.
        double clampedLat = Math.Clamp(latitudeDeg, -85.05112878, 85.05112878);
        double latRad = clampedLat * Math.PI / 180.0;

        double x = (longitudeDeg + 180.0) / 360.0 * scale;
        double y = (1.0 - (Math.Log(Math.Tan(latRad) + (1.0 / Math.Cos(latRad))) / Math.PI))
            / 2.0 * scale;

        return (x, y);
    }

    /// <summary>
    /// Chooses the tiles covering a plot and where each one goes.
    /// </summary>
    /// <param name="centreLat">Observer position — the centre of the plot.</param>
    /// <param name="widthPx">Plot size in device-independent pixels.</param>
    /// <param name="metresPerPixel">
    /// The radar's own scale, so the backdrop lines up with the range rings.
    /// </param>
    /// <param name="maxTiles">
    /// Hard ceiling on tiles per draw. A bound rather than a suggestion: this is
    /// someone else's donated bandwidth, and an unbounded cover on a large
    /// window would ask for hundreds of tiles at once.
    /// </param>
    /// <returns>
    /// Placements in draw order, or empty when the inputs cannot describe a plot.
    /// </returns>
    public static IReadOnlyList<TilePlacement> Cover(
        double centreLat,
        double centreLon,
        double widthPx,
        double heightPx,
        double metresPerPixel,
        int maxTiles = 64)
    {
        if (widthPx <= 0 || heightPx <= 0 || metresPerPixel <= 0 || maxTiles <= 0)
        {
            return [];
        }

        int zoom = ZoomForResolution(centreLat, metresPerPixel);

        // How large one tile must be drawn for the map's scale to equal the
        // radar's. Greater than 256 when the chosen zoom is coarser than ideal.
        double nativeMetresPerPixel = MetresPerPixel(centreLat, zoom);
        double drawnTileSize = TileSizePx * nativeMetresPerPixel / metresPerPixel;

        if (drawnTileSize <= 0 || double.IsNaN(drawnTileSize) || double.IsInfinity(drawnTileSize))
        {
            return [];
        }

        (double centreX, double centreY) = WorldPixel(centreLat, centreLon, zoom);

        // Convert the plot's corners into world pixels at this zoom, then into
        // tile indices.
        double halfWidthWorld = widthPx / 2.0 * metresPerPixel / nativeMetresPerPixel;
        double halfHeightWorld = heightPx / 2.0 * metresPerPixel / nativeMetresPerPixel;

        int minX = (int)Math.Floor((centreX - halfWidthWorld) / TileSizePx);
        int maxX = (int)Math.Floor((centreX + halfWidthWorld) / TileSizePx);
        int minY = (int)Math.Floor((centreY - halfHeightWorld) / TileSizePx);
        int maxY = (int)Math.Floor((centreY + halfHeightWorld) / TileSizePx);

        int span = 1 << zoom;
        var placements = new List<TilePlacement>();

        for (int y = minY; y <= maxY; y++)
        {
            // Outside the world vertically: there is no tile, and wrapping would
            // draw the far hemisphere above the north pole.
            if (y < 0 || y >= span)
            {
                continue;
            }

            for (int x = minX; x <= maxX; x++)
            {
                if (placements.Count >= maxTiles)
                {
                    return placements;
                }

                // Longitude wraps, so a plot spanning the antimeridian takes
                // tiles from the other edge of the world rather than none.
                int wrappedX = ((x % span) + span) % span;

                double left = (widthPx / 2.0)
                    + ((x * TileSizePx) - centreX) * nativeMetresPerPixel / metresPerPixel;
                double top = (heightPx / 2.0)
                    + ((y * TileSizePx) - centreY) * nativeMetresPerPixel / metresPerPixel;

                placements.Add(new TilePlacement(
                    new TileId(zoom, wrappedX, y), left, top, drawnTileSize));
            }
        }

        return placements;
    }
}
