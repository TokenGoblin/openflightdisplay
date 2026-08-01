namespace OpenFlightDisplay.Core.Tracking;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Geo;

/// <summary>Where a tracked flight is in its journey.</summary>
public enum FlightPhase
{
    /// <summary>
    /// Configured, but the aircraft has never been seen.
    /// </summary>
    /// <remarks>
    /// Normal before pushback, and also what a wrong flight number looks like.
    /// A <b>normal state, never an error</b> — ADS-B reports nothing at all
    /// before the transponder is on, so a flight that has not departed is
    /// indistinguishable from one that does not exist.
    /// </remarks>
    AwaitingContact,

    Enroute,
    Descending,
    Approaching,
    Landed,

    /// <summary>
    /// Seen before, now silent, and not near the destination.
    /// </summary>
    /// <remarks>
    /// A coverage gap or a lost feeder. <b>Deliberately distinct from
    /// <see cref="Landed"/></b>: conflating them sends someone to the airport an
    /// hour early, which is the exact failure this feature exists to prevent.
    /// </remarks>
    LostContact,
}

/// <summary>What to tell the user about setting off.</summary>
public enum DepartureAdvice
{
    /// <summary>No ETA yet, or no travel time configured. Nothing honest to say.</summary>
    Unknown,

    Wait,

    /// <summary>Inside the warning window — put your shoes on.</summary>
    LeaveSoon,

    LeaveNow,

    /// <summary>Overdue by a margin, so the display stops escalating.</summary>
    Late,
}

/// <summary>A resolved arrival airport.</summary>
/// <param name="ElevationFt">
/// Field elevation. Load-bearing: "on the ground" is judged against the field,
/// not sea level — Denver's ramp is at 5,400 ft.
/// </param>
public readonly record struct Airport(
    string Icao,
    double Latitude,
    double Longitude,
    double ElevationFt,
    string? Name = null);

/// <summary>How a tracked flight is progressing.</summary>
public readonly record struct FlightProgress
{
    public FlightPhase Phase { get; init; }

    /// <summary>
    /// Minutes to arrival at the current groundspeed, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not a schedule.</b> ADS-B carries no timetable, so this is a
    /// straight-line projection that ignores routing, holding and taxi. Nothing
    /// here computes lateness against a published time, deliberately.
    /// </remarks>
    public int? MinutesRemaining { get; init; }

    /// <summary>Great-circle distance to the destination, or <c>null</c>.</summary>
    public double? DistanceToDestinationKm { get; init; }

    /// <summary>Seconds since the last position report.</summary>
    public int SecondsSinceContact { get; init; }
}

/// <summary>When to set off for the airport.</summary>
public readonly record struct DeparturePlan
{
    public DepartureAdvice Advice { get; init; }

    /// <summary>
    /// Minutes until departure, or <c>null</c> when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <b>Signed.</b> It goes negative once the moment has passed; clamping at
    /// zero would make "leave now" and "you are twenty minutes late" identical.
    /// </remarks>
    public int? MinutesUntilDeparture { get; init; }
}

/// <summary>
/// Following one flight to its destination — the "am I leaving at the right
/// time" case.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>firmware/display/src/domain/flight_tracking.cpp</c>, which is
/// covered by <c>test/native/test_flight_tracking</c>. The thresholds below are
/// the firmware's exact values and the C# tests assert the same behaviour, so
/// the two implementations cannot drift silently.
/// </para>
/// <para>
/// What ADS-B can and cannot say shapes every type here: it reports where an
/// aircraft <i>is</i>, not where it is <i>going</i>; it reports nothing before
/// the transponder is on; and there is no schedule in it at all.
/// </para>
/// </remarks>
public static class FlightTracking
{
    /// <summary>
    /// Deliberately generous, about 30 nm. The point at which "start driving"
    /// stops being a rounding error — not a claim about the final approach fix.
    /// </summary>
    public const double ApproachRadiusKm = 55.0;

    /// <summary>Touchdown is judged near the field, slow, <b>and</b> low.</summary>
    /// <remarks>
    /// Any one of the three alone is a false positive waiting to happen: a
    /// low-and-slow overflight, a go-around, or a barometric glitch.
    /// </remarks>
    public const double LandedRadiusKm = 8.0;

    /// <inheritdoc cref="LandedRadiusKm"/>
    public const double LandedMaxHeightFt = 500.0;

    /// <inheritdoc cref="LandedRadiusKm"/>
    public const double LandedMaxGroundSpeedKt = 120.0;

    /// <summary>Vertical rate at or below which a flight counts as descending.</summary>
    public const double DescentRateFtPerMin = -300.0;

    /// <summary>Silence beyond this makes a previously-seen flight lost, not landed.</summary>
    public static readonly TimeSpan LostContactAfter = TimeSpan.FromSeconds(300);

    /// <summary>Courtesy floor for a free, community-funded data source.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling, or a flight that departs early goes unnoticed.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(300);

    /// <summary>How far ahead of the leave-now moment to start warning.</summary>
    public const int LeaveSoonWindowMinutes = 15;

    /// <summary>Past this much overdue, escalating stops helping.</summary>
    public const int LateThresholdMinutes = 10;

    private const double KnotsToKmh = 1.852;

    /// <summary>Human-readable word for a phase, as shown on the display.</summary>
    public static string PhaseWord(FlightPhase phase) => phase switch
    {
        FlightPhase.AwaitingContact => "WAITING",
        FlightPhase.Enroute => "ENROUTE",
        FlightPhase.Descending => "DESCENDING",
        FlightPhase.Approaching => "APPROACHING",
        FlightPhase.Landed => "LANDED",
        FlightPhase.LostContact => "NO CONTACT",
        _ => "WAITING",
    };

    /// <summary>Human-readable word for departure advice.</summary>
    public static string AdviceWord(DepartureAdvice advice) => advice switch
    {
        DepartureAdvice.Wait => "WAIT",
        DepartureAdvice.LeaveSoon => "LEAVE SOON",
        DepartureAdvice.LeaveNow => "LEAVE NOW",
        DepartureAdvice.Late => "RUNNING LATE",
        _ => string.Empty,
    };

    /// <summary>
    /// Builds the progress view from the latest position report.
    /// </summary>
    /// <param name="aircraft">
    /// Latest observation. Ignored entirely when <paramref name="everSeen"/> is
    /// false.
    /// </param>
    /// <param name="everSeen">
    /// Distinguishes "has not departed yet" from "we lost it", which the
    /// aircraft state alone cannot express.
    /// </param>
    public static FlightProgress ComputeProgress(
        AircraftState? aircraft,
        Airport? destination,
        bool everSeen,
        int secondsSinceContact)
    {
        // Never seen: the transponder is not on yet, or the identifier is
        // wrong. Either way there is nothing to compute, and pretending
        // otherwise would put a fabricated ETA on screen.
        if (!everSeen || aircraft is null)
        {
            return new FlightProgress
            {
                Phase = FlightPhase.AwaitingContact,
                SecondsSinceContact = secondsSinceContact,
            };
        }

        double? distanceKm = destination is { } airport
            ? GeoMath.HaversineDistanceKm(
                aircraft.Latitude, aircraft.Longitude, airport.Latitude, airport.Longitude)
            : null;

        // Touchdown, judged conservatively. Height is measured against the
        // destination's own elevation, so this works at Denver as well as at
        // sea level.
        if (destination is { } field && distanceKm is { } d && d <= LandedRadiusKm)
        {
            double heightAboveField = aircraft.AltitudeFt is { } alt
                ? alt - field.ElevationFt
                : 0.0;

            bool lowEnough = aircraft.OnGround
                || (aircraft.AltitudeFt is not null && heightAboveField <= LandedMaxHeightFt);

            bool slowEnough = aircraft.OnGround
                || (aircraft.GroundSpeedKt is { } gs && gs <= LandedMaxGroundSpeedKt);

            if (lowEnough && slowEnough)
            {
                return new FlightProgress
                {
                    Phase = FlightPhase.Landed,
                    DistanceToDestinationKm = distanceKm,
                    SecondsSinceContact = secondsSinceContact,
                };
            }
        }

        // Silent for a while and not at the destination: a coverage gap, not an
        // arrival. Its own state so nobody reads a frozen position as a landing
        // and leaves early.
        if (secondsSinceContact >= LostContactAfter.TotalSeconds)
        {
            return new FlightProgress
            {
                Phase = FlightPhase.LostContact,
                DistanceToDestinationKm = distanceKm,
                SecondsSinceContact = secondsSinceContact,
            };
        }

        FlightPhase phase;
        if (distanceKm is { } approach && approach <= ApproachRadiusKm)
        {
            phase = FlightPhase.Approaching;
        }
        else if (aircraft.VerticalRateFtPerMin is { } rate && rate <= DescentRateFtPerMin)
        {
            phase = FlightPhase.Descending;
        }
        else
        {
            phase = FlightPhase.Enroute;
        }

        // ETA is distance over current groundspeed. An aircraft stopped at the
        // gate has a distance but no usable ETA, hence the speed floor rather
        // than a division producing infinity.
        int? minutesRemaining = null;
        if (distanceKm is { } remaining
            && aircraft.GroundSpeedKt is { } speed
            && speed > 1.0)
        {
            double minutes = remaining / (speed * KnotsToKmh) * 60.0;

            // Capped rather than overflowing: anything beyond a day out is a
            // data problem, not a flight worth counting down to.
            if (minutes >= 0.0 && minutes < 1440.0)
            {
                minutesRemaining = (int)(minutes + 0.5);
            }
        }

        return new FlightProgress
        {
            Phase = phase,
            MinutesRemaining = minutesRemaining,
            DistanceToDestinationKm = distanceKm,
            SecondsSinceContact = secondsSinceContact,
        };
    }

    /// <summary>
    /// Works out when to set off.
    /// </summary>
    /// <param name="travelMinutes">
    /// Door-to-arrivals-hall time. Zero means the user never configured one, and
    /// the answer is <see cref="DepartureAdvice.Unknown"/> rather than a guess.
    /// </param>
    /// <param name="postLandingMinutes">
    /// Estimated touchdown-to-walk-out time: taxi, deplaning, immigration, bags.
    /// </param>
    /// <remarks>
    /// The subtraction is <b>not</b> "leave when the aircraft lands". Touchdown
    /// is not when the person being collected walks into arrivals, and on a
    /// long-haul arrival that gap is routinely longer than the drive. Omitting
    /// it would send people to the airport to stand around for half an hour,
    /// which is precisely what this feature exists to prevent.
    /// </remarks>
    public static DeparturePlan ComputeDeparturePlan(
        FlightProgress progress,
        int travelMinutes,
        int postLandingMinutes)
    {
        // No configured travel time means the user never asked this question.
        // Answering anyway would require inventing a number.
        if (travelMinutes <= 0)
        {
            return new DeparturePlan { Advice = DepartureAdvice.Unknown };
        }

        // Once it is down, the countdown to touchdown is over and only the
        // walk-out is still running. Treated as zero minutes of flight left
        // rather than "no ETA", so the advice stays useful through the part
        // where somebody is collecting their bags.
        bool landed = progress.Phase == FlightPhase.Landed;
        if (!landed && progress.MinutesRemaining is null)
        {
            return new DeparturePlan { Advice = DepartureAdvice.Unknown };
        }

        int flightMinutes = landed ? 0 : progress.MinutesRemaining!.Value;
        int minutesUntilDeparture = flightMinutes + postLandingMinutes - travelMinutes;

        DepartureAdvice advice = minutesUntilDeparture switch
        {
            <= -LateThresholdMinutes => DepartureAdvice.Late,
            <= 0 => DepartureAdvice.LeaveNow,
            <= LeaveSoonWindowMinutes => DepartureAdvice.LeaveSoon,
            _ => DepartureAdvice.Wait,
        };

        return new DeparturePlan
        {
            Advice = advice,
            MinutesUntilDeparture = minutesUntilDeparture,
        };
    }

    /// <summary>
    /// How long to wait before the next lookup, given where the flight is.
    /// </summary>
    /// <remarks>
    /// The efficiency argument for the whole feature. A flight three hours out
    /// does not need fast polling and one on short final does; ramping by
    /// time-to-arrival cuts request volume by roughly 95% against a fixed fast
    /// poll while being <i>more</i> responsive at the only moment anybody is
    /// watching.
    /// </remarks>
    public static TimeSpan PollIntervalFor(FlightProgress progress)
    {
        switch (progress.Phase)
        {
            case FlightPhase.Landed:
                // Nothing further to learn. The caller stops polling entirely,
                // but the ceiling is returned rather than zero so a caller that
                // keeps going does so as slowly as possible.
                return MaxPollInterval;

            case FlightPhase.AwaitingContact:
                // Waiting for a transponder to come alive. Responsive enough to
                // catch an early pushback without hammering a free API for a
                // flight that may be hours away.
                return TimeSpan.FromSeconds(120);

            case FlightPhase.LostContact:
                // Something is already wrong; polling harder will not fix a
                // coverage gap, and the aircraft usually reappears on its own.
                return TimeSpan.FromSeconds(60);

            case FlightPhase.Approaching:
                return MinPollInterval;

            default:
                break;
        }

        if (progress.MinutesRemaining is not { } minutes)
        {
            return TimeSpan.FromSeconds(60);
        }

        return minutes switch
        {
            > 90 => MaxPollInterval,
            > 30 => TimeSpan.FromSeconds(120),
            > 10 => TimeSpan.FromSeconds(60),
            > 3 => TimeSpan.FromSeconds(20),
            _ => MinPollInterval,
        };
    }

    /// <summary>
    /// Normalizes what a human types into the callsign ADS-B broadcasts.
    /// </summary>
    /// <returns>
    /// The normalized callsign, or <c>null</c> if the input is not a flight
    /// identifier.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>"UA1234"</c>, <c>"ua 1234"</c> and <c>"UA-1234"</c> all become
    /// <c>"UAL1234"</c>. An unrecognised two-letter prefix passes through
    /// uppercased rather than being mangled — somebody tracking a carrier
    /// missing from the table should still get a literal match attempt.
    /// </para>
    /// <para>
    /// Rejects input with no digits, which is the "that is not a flight number"
    /// case worth reporting before someone drives to an airport on it.
    /// </para>
    /// </remarks>
    public static string? NormalizeFlightIdentifier(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // Strip everything that is not alphanumeric and uppercase the rest.
        Span<char> compact = stackalloc char[16];
        int length = 0;

        foreach (char c in input)
        {
            if (length >= compact.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                compact[length++] = char.ToUpperInvariant(c);
            }
        }

        if (length < 3)
        {
            return null;
        }

        ReadOnlySpan<char> normalized = compact[..length];

        // Split the leading letters from the numeric part. An identifier with
        // no digits is not one.
        int letters = 0;
        while (letters < normalized.Length && char.IsAsciiLetter(normalized[letters]))
        {
            letters++;
        }

        if (letters == 0 || letters == normalized.Length)
        {
            return null;
        }

        ReadOnlySpan<char> digits = normalized[letters..];

        // A two-letter prefix is an IATA code needing expansion to the ICAO
        // designator ADS-B actually broadcasts.
        if (letters == 2
            && AirlineTable.IcaoForIata(new string(normalized[..2])) is { } icao)
        {
            return icao + new string(digits);
        }

        return new string(normalized);
    }

    /// <summary>
    /// Time to arrival, readable at a glance from across a room.
    /// </summary>
    /// <remarks>
    /// Writes an em dash when there is no ETA — <b>never a bare "0"</b>, which
    /// reads as "landing now".
    /// </remarks>
    public static string FormatMinutesRemaining(int? minutes)
    {
        if (minutes is not { } value)
        {
            return "—";
        }

        return value < 60
            ? value.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"{value / 60}H{value % 60:D2}");
    }
}
