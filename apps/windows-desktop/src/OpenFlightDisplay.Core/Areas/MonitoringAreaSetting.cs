namespace OpenFlightDisplay.Core.Areas;

using System.Globalization;

/// <summary>Shape of the area being monitored.</summary>
public enum AreaShape
{
    /// <summary>Everything within a radius. The default and the simplest.</summary>
    Circle,

    /// <summary>A wedge: a radius narrowed to an arc of bearings.</summary>
    Cone,

    /// <summary>An arbitrary outline.</summary>
    Polygon,
}

/// <summary>
/// The monitoring area in the form that is saved to disk.
/// </summary>
/// <remarks>
/// Flattened for the same reason <c>AlertRuleSetting</c> is: <see cref="MonitoringArea"/>
/// is polymorphic with three implementations, and serializing it would need a
/// discriminator and a converter. One flat record with a shape field round-trips
/// through plain JSON and cannot deserialize into a type nobody expected.
/// </remarks>
public sealed record MonitoringAreaSetting
{
    /// <summary>Fewest vertices that enclose anything.</summary>
    public const int MinimumVertices = 3;

    /// <summary>Matches the polygon limit the domain documents.</summary>
    public const int MaximumVertices = 64;

    public AreaShape Shape { get; init; } = AreaShape.Circle;

    /// <summary>
    /// Centre, or <c>null</c> to use the configured home location.
    /// </summary>
    /// <remarks>
    /// Defaulting to home rather than duplicating the coordinates means moving
    /// home moves the area, instead of silently leaving it over the old address.
    /// </remarks>
    public double? CenterLat { get; init; }

    /// <inheritdoc cref="CenterLat"/>
    public double? CenterLon { get; init; }

    public double RadiusKm { get; init; } = 80.0;

    /// <summary>Bearing the cone points along, degrees true.</summary>
    public double HeadingDeg { get; init; }

    /// <summary>Total width of the cone's arc, in degrees.</summary>
    public double WidthDeg { get; init; } = 90.0;

    /// <summary>Polygon outline. Ignored for other shapes.</summary>
    public IReadOnlyList<GeoPoint> Vertices { get; init; } = [];

    /// <summary>Ignore aircraft reported below this altitude.</summary>
    public double? MinAltitudeFt { get; init; }

    /// <summary>Ignore aircraft reported above this altitude.</summary>
    public double? MaxAltitudeFt { get; init; }

    /// <summary>
    /// Compares by value, including the vertex list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written because the compiler-generated version compares
    /// <see cref="Vertices"/> by <b>reference</b> — it is an
    /// <see cref="IReadOnlyList{T}"/>, and interfaces get reference equality.
    /// Two settings with identical outlines therefore compared as different, and
    /// in particular a settings object was never equal to itself after a save
    /// and reload, because serializing produced an array and deserializing
    /// produced a list.
    /// </para>
    /// <para>
    /// <b>Add new properties here as well as above.</b> A property missing from
    /// this method is silently ignored when comparing.
    /// </para>
    /// </remarks>
    public bool Equals(MonitoringAreaSetting? other) =>
        other is not null
        && Shape == other.Shape
        && CenterLat == other.CenterLat
        && CenterLon == other.CenterLon
        && RadiusKm == other.RadiusKm
        && HeadingDeg == other.HeadingDeg
        && WidthDeg == other.WidthDeg
        && MinAltitudeFt == other.MinAltitudeFt
        && MaxAltitudeFt == other.MaxAltitudeFt
        && Vertices.SequenceEqual(other.Vertices);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Shape);
        hash.Add(CenterLat);
        hash.Add(CenterLon);
        hash.Add(RadiusKm);
        hash.Add(HeadingDeg);
        hash.Add(WidthDeg);
        hash.Add(MinAltitudeFt);
        hash.Add(MaxAltitudeFt);

        foreach (GeoPoint vertex in Vertices)
        {
            hash.Add(vertex);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Checks the area encloses something.
    /// </summary>
    /// <returns>A user-facing reason it does not, or <c>null</c>.</returns>
    public string? Validate()
    {
        if (MinAltitudeFt is { } floor && MaxAltitudeFt is { } ceiling && floor > ceiling)
        {
            return "The minimum altitude is above the maximum, so the area contains nothing.";
        }

        if (CenterLat is { } lat && (lat is < -90 or > 90))
        {
            return "Latitude must be between -90 and 90.";
        }

        if (CenterLon is { } lon && (lon is < -180 or > 180))
        {
            return "Longitude must be between -180 and 180.";
        }

        switch (Shape)
        {
            case AreaShape.Circle:
            case AreaShape.Cone:
                if (RadiusKm <= 0)
                {
                    return "The radius must be greater than zero.";
                }

                if (RadiusKm > 500)
                {
                    return "The radius must be 500 km or less.";
                }

                if (Shape == AreaShape.Cone)
                {
                    if (WidthDeg is <= 0 or > 360)
                    {
                        return "The cone width must be between 1 and 360 degrees.";
                    }

                    if (HeadingDeg is < 0 or >= 360)
                    {
                        return "The heading must be between 0 and 359 degrees.";
                    }
                }

                break;

            case AreaShape.Polygon:
                if (Vertices.Count < MinimumVertices)
                {
                    return $"A polygon needs at least {MinimumVertices} points.";
                }

                if (Vertices.Count > MaximumVertices)
                {
                    return $"A polygon can have at most {MaximumVertices} points.";
                }

                break;

            default:
                return "Unknown area shape.";
        }

        return null;
    }

    /// <summary>
    /// Builds the domain area.
    /// </summary>
    /// <param name="homeLat">Used when no explicit centre is set.</param>
    /// <returns>
    /// The area, or <c>null</c> if it is not usable — an invalid setting, or a
    /// centre-relative shape with no home location configured. Returning null
    /// rather than a degenerate area keeps "not configured" from silently
    /// becoming "a circle at 0N 0E".
    /// </returns>
    public MonitoringArea? Build(double? homeLat, double? homeLon)
    {
        if (Validate() is not null)
        {
            return null;
        }

        if (Shape == AreaShape.Polygon)
        {
            return new PolygonArea([.. Vertices])
            {
                MinAltitudeFt = MinAltitudeFt,
                MaxAltitudeFt = MaxAltitudeFt,
            };
        }

        double? lat = CenterLat ?? homeLat;
        double? lon = CenterLon ?? homeLon;

        if (lat is not { } centreLat || lon is not { } centreLon)
        {
            return null;
        }

        return Shape == AreaShape.Cone
            ? new ConeArea(centreLat, centreLon, RadiusKm, HeadingDeg, WidthDeg)
            {
                MinAltitudeFt = MinAltitudeFt,
                MaxAltitudeFt = MaxAltitudeFt,
            }
            : new CircleArea(centreLat, centreLon, RadiusKm)
            {
                MinAltitudeFt = MinAltitudeFt,
                MaxAltitudeFt = MaxAltitudeFt,
            };
    }

    /// <summary>One line describing the area.</summary>
    public string Summarise()
    {
        var c = CultureInfo.CurrentCulture;

        string shape = Shape switch
        {
            AreaShape.Circle => string.Create(c, $"Circle, {RadiusKm:N0} km radius"),
            AreaShape.Cone => string.Create(
                c, $"Cone, {RadiusKm:N0} km radius, {WidthDeg:N0}° wide facing {HeadingDeg:N0}°"),
            AreaShape.Polygon => string.Create(c, $"Polygon, {Vertices.Count} points"),
            _ => "Unknown shape",
        };

        string centre = Shape == AreaShape.Polygon
            ? string.Empty
            : CenterLat is null ? " centred on home" : " centred on a fixed point";

        string altitude = (MinAltitudeFt, MaxAltitudeFt) switch
        {
            (null, null) => string.Empty,
            ({ } lo, null) => string.Create(c, $", above {lo:N0} ft"),
            (null, { } hi) => string.Create(c, $", below {hi:N0} ft"),
            ({ } lo, { } hi) => string.Create(c, $", {lo:N0}–{hi:N0} ft"),
        };

        return shape + centre + altitude;
    }
}
