namespace OpenFlightDisplay.Core.Tracking;

using System.Globalization;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Geo;

/// <summary>What an aircraft appears to be doing relative to an airport.</summary>
/// <remarks>
/// <b>Every one of these is an observation, never a schedule.</b> ADS-B carries
/// no timetable, so nothing here means "the 14:05 to Denver" — it means "this
/// aircraft is descending towards this field right now".
/// </remarks>
public enum MovementKind
{
    /// <summary>
    /// Not enough was reported to say.
    /// </summary>
    /// <remarks>
    /// Usually a missing vertical rate. Its own value rather than a fallback to
    /// <see cref="Overflight"/>, because "we cannot tell" and "passing through"
    /// are different facts and the board shows them differently.
    /// </remarks>
    Unknown,

    /// <summary>Descending towards the field.</summary>
    Arriving,

    /// <summary>Climbing away from the field.</summary>
    Departing,

    /// <summary>Reported on the ground at the field.</summary>
    OnGround,

    /// <summary>Near the field but neither arriving nor departing.</summary>
    Overflight,
}

/// <summary>One line on the movements board.</summary>
/// <param name="HeightAboveFieldFt">
/// Height above the airport's own elevation, not sea level. <c>null</c> when no
/// altitude was reported.
/// </param>
/// <param name="MinutesAway">
/// Rough minutes to the field at the current groundspeed, or <c>null</c>.
/// <b>A projection, not an ETA</b> — it ignores routing, approach paths and
/// holding, all of which are exactly what makes a real arrival time differ.
/// </param>
public readonly record struct AirportMovement(
    AircraftState Aircraft,
    MovementKind Kind,
    double DistanceKm,
    double? HeightAboveFieldFt,
    int? MinutesAway);

/// <summary>
/// Classifies observed traffic around an airport into arrivals and departures.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an observed-movements board, not a flight-information display.</b>
/// The distinction is architectural, not cosmetic: ADS-B has no scheduled time,
/// no gate, no flight status and no cancellations, and this project's rules
/// forbid presenting a calculated estimate as though it were published
/// information. A real airport board is built from an airline feed; this is
/// built from what transponders are actually saying.
/// </para>
/// <para>
/// Heights are measured against the airport's <b>field elevation</b>, which is
/// why the lookup that supplies it is worth a request. Judging a climb out of
/// Salt Lake City — a field at 4,200 ft — against sea level would call every
/// departure an arrival for its first thousand feet.
/// </para>
/// </remarks>
public static class AirportMovements
{
    /// <summary>How far from the field traffic is still considered relevant.</summary>
    /// <remarks>
    /// Matches <see cref="FlightTracking.ApproachRadiusKm"/>, so "approaching"
    /// means the same thing on this board as it does when following a flight.
    /// </remarks>
    public const double RadiusKm = FlightTracking.ApproachRadiusKm;

    /// <summary>Climb rate at or above which an aircraft counts as departing.</summary>
    /// <remarks>
    /// The mirror of <see cref="FlightTracking.DescentRateFtPerMin"/>. Symmetric
    /// on purpose: an asymmetric pair would classify a shallow climb and a
    /// shallow descent differently for no reason a user could predict.
    /// </remarks>
    public const double ClimbRateFtPerMin = 300.0;

    /// <summary>
    /// Height above the field beyond which traffic is treated as overflying.
    /// </summary>
    /// <remarks>
    /// Above this an aircraft descending over the field is far more likely to be
    /// en route to somewhere else than arriving here. Without a ceiling, every
    /// airliner beginning its descent across the state would appear as an
    /// arrival.
    /// </remarks>
    public const double MovementCeilingFt = 12000.0;

    private const double KnotsToKmh = 1.852;

    /// <summary>Word for a movement, as shown on the board.</summary>
    public static string KindWord(MovementKind kind) => kind switch
    {
        MovementKind.Arriving => "ARRIVING",
        MovementKind.Departing => "DEPARTING",
        MovementKind.OnGround => "ON GROUND",
        MovementKind.Overflight => "OVERFLIGHT",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// Classifies one aircraft against an airport.
    /// </summary>
    /// <returns>
    /// The movement, or <c>null</c> if the aircraft is outside
    /// <see cref="RadiusKm"/> and does not belong on the board at all.
    /// </returns>
    public static AirportMovement? Classify(AircraftState aircraft, Airport airport)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        double distanceKm = GeoMath.HaversineDistanceKm(
            aircraft.Latitude, aircraft.Longitude, airport.Latitude, airport.Longitude);

        if (distanceKm > RadiusKm)
        {
            return null;
        }

        // Measured against the field, never sea level.
        double? heightAboveField = aircraft.AltitudeFt is { } altitude
            ? altitude - airport.ElevationFt
            : null;

        int? minutesAway = null;
        if (aircraft.GroundSpeedKt is { } speed && speed > 1.0)
        {
            double minutes = distanceKm / (speed * KnotsToKmh) * 60.0;
            if (minutes >= 0 && minutes < 600)
            {
                minutesAway = (int)(minutes + 0.5);
            }
        }

        MovementKind kind = Categorise(aircraft, distanceKm, heightAboveField);

        return new AirportMovement(aircraft, kind, distanceKm, heightAboveField, minutesAway);
    }

    /// <summary>Builds the board, arrivals and departures first, nearest first.</summary>
    /// <remarks>
    /// Ordered by what a person watching an airport cares about: things actually
    /// moving, closest first. Overflights and unknowns sink to the bottom rather
    /// than being hidden — omitting them would make the board disagree with the
    /// radar with no explanation.
    /// </remarks>
    public static IReadOnlyList<AirportMovement> Build(
        IEnumerable<AircraftState> aircraft,
        Airport airport)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        return [.. aircraft
            .Select(a => Classify(a, airport))
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .OrderBy(m => SortRank(m.Kind))
            .ThenBy(m => m.DistanceKm)];
    }

    private static int SortRank(MovementKind kind) => kind switch
    {
        MovementKind.Arriving => 0,
        MovementKind.Departing => 1,
        MovementKind.OnGround => 2,
        MovementKind.Overflight => 3,
        _ => 4,
    };

    private static MovementKind Categorise(
        AircraftState aircraft,
        double distanceKm,
        double? heightAboveField)
    {
        // The reported ground flag, never inferred from a low altitude — an
        // aircraft on short final is not on the ground, and calling it so would
        // hide the arrival everyone is waiting for.
        if (aircraft.OnGround)
        {
            return distanceKm <= FlightTracking.LandedRadiusKm
                ? MovementKind.OnGround
                : MovementKind.Unknown;
        }

        // No vertical rate means no basis for a claim. Reported as unknown
        // rather than assumed level, which is the same distinction
        // VerticalTrend already draws.
        if (aircraft.VerticalRateFtPerMin is not { } rate)
        {
            return MovementKind.Unknown;
        }

        // Too high to be using this field, whatever it is doing vertically.
        if (heightAboveField is { } height && height > MovementCeilingFt)
        {
            return MovementKind.Overflight;
        }

        if (rate <= FlightTracking.DescentRateFtPerMin)
        {
            return MovementKind.Arriving;
        }

        if (rate >= ClimbRateFtPerMin)
        {
            return MovementKind.Departing;
        }

        return MovementKind.Overflight;
    }

    /// <summary>Height above the field, for display, or an em dash.</summary>
    public static string FormatHeight(double? heightAboveFieldFt)
        => heightAboveFieldFt is { } height
            ? string.Create(CultureInfo.CurrentCulture, $"{height:N0} ft")
            : "—";

    /// <summary>
    /// Minutes away, for display.
    /// </summary>
    /// <remarks>
    /// Never a bare number for a departure — "12" beside a departing aircraft
    /// reads as an arrival time. Departures show how long they have been going
    /// nowhere near this field instead.
    /// </remarks>
    public static string FormatMinutesAway(MovementKind kind, int? minutesAway)
    {
        if (kind is MovementKind.OnGround)
        {
            return "at field";
        }

        if (minutesAway is not { } minutes)
        {
            return "—";
        }

        return kind == MovementKind.Arriving
            ? string.Create(CultureInfo.CurrentCulture, $"~{minutes} min")
            : "—";
    }
}
