namespace OpenFlightDisplay.Core.Areas;

using OpenFlightDisplay.Core.Geo;

/// <summary>A latitude/longitude pair in degrees.</summary>
public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>
/// A region of interest. Mirrors <c>MonitoringAreaSchema</c> in
/// <c>packages/shared-models/src/monitoringArea.ts</c>, which already models
/// circle, cone and polygon so that adding them needed no schema migration.
/// </summary>
/// <remarks>
/// The gateway and firmware implement only <c>circle</c> and reject the other
/// two with an explicit "not yet supported" error. The desktop implements all
/// three — it is the client with the screen space and the CPU to make them
/// worth editing. Geometry round-trips to GeoJSON for import/export.
/// </remarks>
public abstract record MonitoringArea
{
    /// <summary>Inclusive altitude floor in feet, or <c>null</c> for no floor.</summary>
    public double? MinAltitudeFt { get; init; }

    /// <summary>Inclusive altitude ceiling in feet, or <c>null</c> for no ceiling.</summary>
    public double? MaxAltitudeFt { get; init; }

    /// <summary>True if the horizontal position falls inside this shape.</summary>
    public abstract bool ContainsPosition(double lat, double lon);

    /// <summary>
    /// True if the altitude falls within the configured band.
    /// </summary>
    /// <remarks>
    /// An aircraft with <b>no reported altitude</b> passes any altitude band.
    /// Excluding it would silently drop valid traffic for a missing enrichment
    /// field, which the product rules forbid — the record is surfaced carrying
    /// <c>DataQualityFlags.NoAltitude</c> instead.
    /// </remarks>
    public bool ContainsAltitude(double? altitudeFt)
    {
        if (altitudeFt is not { } alt)
        {
            return true;
        }

        if (MinAltitudeFt is { } min && alt < min)
        {
            return false;
        }

        return MaxAltitudeFt is not { } max || alt <= max;
    }

    /// <summary>True if both the position and the altitude qualify.</summary>
    public bool Contains(double lat, double lon, double? altitudeFt)
        => ContainsPosition(lat, lon) && ContainsAltitude(altitudeFt);
}

/// <summary>A circle of <paramref name="RadiusKm"/> around a centre point.</summary>
public sealed record CircleArea(double CenterLat, double CenterLon, double RadiusKm) : MonitoringArea
{
    /// <inheritdoc/>
    /// <remarks>Inclusive at the boundary, matching the gateway and firmware.</remarks>
    public override bool ContainsPosition(double lat, double lon)
        => GeoMath.IsWithinCircle(lat, lon, CenterLat, CenterLon, RadiusKm);
}

/// <summary>
/// A directional wedge: everything within <paramref name="RadiusKm"/> whose
/// bearing from the centre falls inside a <paramref name="WidthDeg"/>-wide arc
/// centred on <paramref name="HeadingDeg"/>.
/// </summary>
public sealed record ConeArea(
    double CenterLat,
    double CenterLon,
    double RadiusKm,
    double HeadingDeg,
    double WidthDeg) : MonitoringArea
{
    /// <inheritdoc/>
    public override bool ContainsPosition(double lat, double lon)
    {
        if (!GeoMath.IsWithinCircle(lat, lon, CenterLat, CenterLon, RadiusKm))
        {
            return false;
        }

        // A full-width cone is a circle. Short-circuit so floating-point
        // arithmetic can't exclude a point at exactly the wrap-around bearing.
        if (WidthDeg >= 360.0)
        {
            return true;
        }

        double bearing = GeoMath.InitialBearingDeg(CenterLat, CenterLon, lat, lon);

        // Smallest absolute angular difference, correct across the 0/360 seam.
        double delta = Math.Abs(((bearing - HeadingDeg + 540.0) % 360.0) - 180.0);
        return delta <= WidthDeg / 2.0;
    }
}

/// <summary>An arbitrary polygon, 3 to 64 vertices.</summary>
public sealed record PolygonArea(IReadOnlyList<GeoPoint> Vertices) : MonitoringArea
{
    /// <inheritdoc/>
    /// <remarks>
    /// Ray casting in the lon/lat plane. Adequate for the neighbourhood-scale
    /// areas this feature targets; it does not follow great circles, so a
    /// polygon whose edges span hundreds of kilometres will disagree slightly
    /// with the geodesic truth near the edges. Documented rather than hidden.
    /// Does not handle antimeridian crossing.
    /// </remarks>
    public override bool ContainsPosition(double lat, double lon)
    {
        if (Vertices.Count < 3)
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = Vertices.Count - 1; i < Vertices.Count; j = i++)
        {
            GeoPoint a = Vertices[i];
            GeoPoint b = Vertices[j];

            bool straddles = (a.Lat > lat) != (b.Lat > lat);
            if (!straddles)
            {
                continue;
            }

            double intersectLon = ((b.Lon - a.Lon) * (lat - a.Lat) / (b.Lat - a.Lat)) + a.Lon;
            if (lon < intersectLon)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
