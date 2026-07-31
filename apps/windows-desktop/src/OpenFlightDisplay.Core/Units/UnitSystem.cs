namespace OpenFlightDisplay.Core.Units;

/// <summary>
/// Which family of units to present measurements in.
/// </summary>
/// <remarks>
/// The firmware is metric-only (Phase 1 of the original roadmap); the desktop
/// ships all three from the start. <see cref="Aviation"/> is not a cosmetic
/// variant of <see cref="Imperial"/> — it is what the domain actually speaks.
/// ADS-B reports altitude in feet and groundspeed in knots regardless of the
/// user's locale, so aviation units are the only mode that involves no
/// conversion at all.
/// </remarks>
public enum UnitSystem
{
    /// <summary>Kilometres, metres, km/h.</summary>
    Metric,

    /// <summary>Miles, feet, mph.</summary>
    Imperial,

    /// <summary>Nautical miles, feet, knots. The units ADS-B is reported in.</summary>
    Aviation,
}

/// <summary>Conversions between the internal canonical units and display units.</summary>
/// <remarks>
/// <para>
/// The canonical internal representation is fixed and must not drift:
/// <b>distance in kilometres, altitude in feet, speed in knots, vertical rate in
/// feet per minute.</b> That mix looks inconsistent but is deliberate — it
/// matches <c>AircraftState</c>, which matches the wire protocol, which matches
/// what providers actually send. Converting on ingest would mean converting
/// back for every protocol interaction.
/// </para>
/// <para>Conversion happens once, at the display boundary.</para>
/// </remarks>
public static class UnitConverter
{
    public const double KmPerNauticalMile = 1.852;
    public const double KmPerStatuteMile = 1.609344;
    public const double MetresPerFoot = 0.3048;

    // ---- distance (canonical: kilometres) ----

    /// <summary>Converts a canonical distance in km to the given system.</summary>
    public static double DistanceFromKm(double km, UnitSystem system) => system switch
    {
        UnitSystem.Metric => km,
        UnitSystem.Imperial => km / KmPerStatuteMile,
        UnitSystem.Aviation => km / KmPerNauticalMile,
        _ => km,
    };

    /// <summary>Converts a display distance back to canonical kilometres.</summary>
    public static double DistanceToKm(double value, UnitSystem system) => system switch
    {
        UnitSystem.Metric => value,
        UnitSystem.Imperial => value * KmPerStatuteMile,
        UnitSystem.Aviation => value * KmPerNauticalMile,
        _ => value,
    };

    /// <summary>Short label for a distance in the given system.</summary>
    public static string DistanceUnitLabel(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "km",
        UnitSystem.Imperial => "mi",
        UnitSystem.Aviation => "NM",
        _ => "km",
    };

    // ---- altitude (canonical: feet) ----

    /// <summary>
    /// Converts a canonical altitude in feet to the given system.
    /// </summary>
    /// <remarks>
    /// Only <see cref="UnitSystem.Metric"/> converts. Imperial and aviation both
    /// use feet — flight levels are feet worldwide, including in countries that
    /// are otherwise entirely metric.
    /// </remarks>
    public static double AltitudeFromFeet(double feet, UnitSystem system) => system switch
    {
        UnitSystem.Metric => feet * MetresPerFoot,
        _ => feet,
    };

    /// <summary>Short label for an altitude in the given system.</summary>
    public static string AltitudeUnitLabel(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "m",
        _ => "ft",
    };

    // ---- speed (canonical: knots) ----

    /// <summary>Converts a canonical groundspeed in knots to the given system.</summary>
    public static double SpeedFromKnots(double knots, UnitSystem system) => system switch
    {
        UnitSystem.Metric => knots * KmPerNauticalMile,
        UnitSystem.Imperial => knots * KmPerNauticalMile / KmPerStatuteMile,
        UnitSystem.Aviation => knots,
        _ => knots,
    };

    /// <summary>Short label for a speed in the given system.</summary>
    public static string SpeedUnitLabel(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "km/h",
        UnitSystem.Imperial => "mph",
        UnitSystem.Aviation => "kt",
        _ => "kt",
    };

    // ---- vertical rate (canonical: feet per minute) ----

    /// <summary>Converts a canonical vertical rate in ft/min to the given system.</summary>
    /// <remarks>
    /// Metric uses metres per second, which is the convention for vertical
    /// speed indicators outside the US — not metres per minute.
    /// </remarks>
    public static double VerticalRateFromFeetPerMinute(double ftPerMin, UnitSystem system)
        => system switch
        {
            UnitSystem.Metric => ftPerMin * MetresPerFoot / 60.0,
            _ => ftPerMin,
        };

    /// <summary>Short label for a vertical rate in the given system.</summary>
    public static string VerticalRateUnitLabel(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "m/s",
        _ => "ft/min",
    };
}
