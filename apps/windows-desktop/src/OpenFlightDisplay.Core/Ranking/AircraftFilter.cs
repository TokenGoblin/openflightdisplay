namespace OpenFlightDisplay.Core.Ranking;

using System.Globalization;
using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Narrows what reaches the board and the plot.
/// </summary>
/// <remarks>
/// <para>
/// Applied after the monitoring area and before ranking. The area answers "could
/// I see it from here"; this answers "do I care about it" — a question only the
/// user can settle, which is why every field defaults to letting everything
/// through.
/// </para>
/// <para>
/// <b>A missing measurement never fails a filter.</b> An aircraft that reported
/// no altitude is not at zero feet, so an altitude filter cannot honestly
/// exclude it — that would silently hide traffic on the basis of a reading that
/// was never taken. Unknown values pass, and the board already marks them as
/// unknown. This is the same rule the rest of the project follows for nullables,
/// applied to filtering.
/// </para>
/// </remarks>
public sealed record AircraftFilter
{
    /// <summary>Lets everything through.</summary>
    public static AircraftFilter None { get; } = new();

    /// <summary>Hide aircraft reported below this altitude, in feet.</summary>
    public double? MinAltitudeFt { get; init; }

    /// <summary>Hide aircraft reported above this altitude, in feet.</summary>
    public double? MaxAltitudeFt { get; init; }

    /// <summary>Hide aircraft reported as on the ground.</summary>
    /// <remarks>
    /// Uses the reported ground flag only. Inferring it from a low altitude
    /// would hide aircraft on short final, which are the ones most worth seeing.
    /// </remarks>
    public bool ExcludeOnGround { get; init; }

    /// <summary>Hide aircraft that have not transmitted a callsign.</summary>
    public bool RequireCallsign { get; init; }

    /// <summary>Show only aircraft squawking an emergency.</summary>
    public bool EmergencyOnly { get; init; }

    /// <summary>True when nothing is being filtered out.</summary>
    public bool IsEmpty =>
        MinAltitudeFt is null
        && MaxAltitudeFt is null
        && !ExcludeOnGround
        && !RequireCallsign
        && !EmergencyOnly;

    /// <summary>True if <paramref name="aircraft"/> should be shown.</summary>
    public bool Admits(AircraftState aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        if (EmergencyOnly && aircraft.EmergencyState == EmergencyState.None)
        {
            return false;
        }

        if (ExcludeOnGround && aircraft.OnGround)
        {
            return false;
        }

        if (RequireCallsign && string.IsNullOrWhiteSpace(aircraft.Callsign))
        {
            return false;
        }

        // Only applied when an altitude was actually reported. Excluding an
        // aircraft whose altitude is unknown would be treating "not reported"
        // as a value, which is the one thing this codebase never does.
        if (aircraft.AltitudeFt is { } altitude)
        {
            if (MinAltitudeFt is { } floor && altitude < floor)
            {
                return false;
            }

            if (MaxAltitudeFt is { } ceiling && altitude > ceiling)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Applies the filter to a sequence.</summary>
    public IEnumerable<AircraftState> Apply(IEnumerable<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return IsEmpty ? aircraft : aircraft.Where(Admits);
    }

    /// <summary>
    /// Checks the filter is satisfiable.
    /// </summary>
    /// <returns>A user-facing reason it is not, or <c>null</c>.</returns>
    public string? Validate()
    {
        if (MinAltitudeFt is { } floor && MaxAltitudeFt is { } ceiling && floor > ceiling)
        {
            return "The minimum altitude is above the maximum, so nothing can match.";
        }

        return null;
    }

    /// <summary>One line describing what is being hidden, for the status bar.</summary>
    public string Summarise()
    {
        if (IsEmpty)
        {
            return "No filter";
        }

        var parts = new List<string>();

        if (MinAltitudeFt is { } floor)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"above {floor:N0} ft"));
        }

        if (MaxAltitudeFt is { } ceiling)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"below {ceiling:N0} ft"));
        }

        if (ExcludeOnGround)
        {
            parts.Add("airborne only");
        }

        if (RequireCallsign)
        {
            parts.Add("with a callsign");
        }

        if (EmergencyOnly)
        {
            parts.Add("emergencies only");
        }

        return "Showing " + string.Join(", ", parts);
    }
}
