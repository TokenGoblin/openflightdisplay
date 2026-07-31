namespace OpenFlightDisplay.Core.Geo;

/// <summary>
/// Great-circle geometry.
///
/// This is the third implementation of these functions in the repository —
/// <c>services/gateway/src/lib/geo.ts</c> and
/// <c>firmware/display/src/domain/geo.cpp</c> are the other two. They must agree.
/// The shared fixtures under <c>datasets/parity/</c> exist to make a divergence
/// fail a build rather than surface as a wrong distance on someone's screen.
/// </summary>
public static class GeoMath
{
    /// <summary>
    /// IUGG mean Earth radius, in kilometres.
    ///
    /// Load-bearing: the TypeScript and C++ implementations both use this exact
    /// value. Rounding it to 6371 shifts long-range distances by tens of metres
    /// and breaks fixture parity.
    /// </summary>
    public const double EarthRadiusKm = 6371.0088;

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    /// <summary>Great-circle distance between two points, in kilometres.</summary>
    public static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * DegreesToRadians;
        double dLon = (lon2 - lon1) * DegreesToRadians;

        double sinDLat = Math.Sin(dLat / 2.0);
        double sinDLon = Math.Sin(dLon / 2.0);

        double a = (sinDLat * sinDLat)
                 + (Math.Cos(lat1 * DegreesToRadians)
                    * Math.Cos(lat2 * DegreesToRadians)
                    * sinDLon * sinDLon);

        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return EarthRadiusKm * c;
    }

    /// <summary>Initial bearing from point 1 to point 2, in degrees [0, 360).</summary>
    public static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * DegreesToRadians;
        double phi2 = lat2 * DegreesToRadians;
        double dLon = (lon2 - lon1) * DegreesToRadians;

        double y = Math.Sin(dLon) * Math.Cos(phi2);
        double x = (Math.Cos(phi1) * Math.Sin(phi2))
                 - (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon));

        double bearing = Math.Atan2(y, x) * RadiansToDegrees;

        // Normalise into [0, 360). The modulo alone leaves -0.0 and negative
        // results, which would sort and format inconsistently.
        return (bearing + 360.0) % 360.0;
    }

    /// <summary>True if the point is within <paramref name="radiusKm"/> of the centre.</summary>
    /// <remarks>
    /// Inclusive at the boundary, matching <c>isWithinCircle</c> in the gateway
    /// and firmware. A point exactly on the radius is inside.
    /// </remarks>
    public static bool IsWithinCircle(
        double lat,
        double lon,
        double centerLat,
        double centerLon,
        double radiusKm)
        => HaversineDistanceKm(lat, lon, centerLat, centerLon) <= radiusKm;

    /// <summary>
    /// Slant range: horizontal great-circle distance combined with the height
    /// difference, in kilometres.
    /// </summary>
    /// <remarks>
    /// New in the desktop client — the firmware ranks on horizontal distance
    /// only. An aircraft directly overhead at 37,000 ft is ~11 km away, not
    /// ~0 km, and for a "what is nearest to me" question that difference is the
    /// whole answer.
    /// </remarks>
    public static double SlantRangeKm(double horizontalKm, double altitudeFt)
    {
        const double FeetToKm = 0.0003048;
        double verticalKm = altitudeFt * FeetToKm;
        return Math.Sqrt((horizontalKm * horizontalKm) + (verticalKm * verticalKm));
    }
}
