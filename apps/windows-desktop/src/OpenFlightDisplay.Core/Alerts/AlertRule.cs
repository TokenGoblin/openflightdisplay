namespace OpenFlightDisplay.Core.Alerts;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;

/// <summary>What an alert rule watches for.</summary>
public enum AlertTrigger
{
    /// <summary>An aircraft came inside a monitoring area.</summary>
    EntersArea,

    /// <summary>An aircraft left a monitoring area it was inside.</summary>
    ExitsArea,

    /// <summary>An aircraft came within a distance threshold of the observer.</summary>
    ApproachesWithin,

    /// <summary>An aircraft is squawking an emergency code.</summary>
    EmergencySquawk,

    /// <summary>An aircraft descended below an altitude threshold.</summary>
    DescendsBelow,
}

/// <summary>How the user is told.</summary>
[Flags]
public enum AlertChannels
{
    None = 0,
    InApp = 1 << 0,
    Toast = 1 << 1,
    Sound = 1 << 2,
    Log = 1 << 3,
}

/// <summary>
/// A window of the day during which alerts are suppressed.
/// </summary>
/// <param name="Start">Inclusive local start time.</param>
/// <param name="End">Exclusive local end time.</param>
public readonly record struct QuietHours(TimeOnly Start, TimeOnly End)
{
    /// <summary>True if <paramref name="localTime"/> falls inside the window.</summary>
    /// <remarks>
    /// Handles windows that wrap past midnight — 22:00 to 07:00 is the normal
    /// case for this feature, and treating it as an ordinary ascending range
    /// would match nothing at all.
    /// </remarks>
    public bool Contains(TimeOnly localTime) => Start <= End
        ? localTime >= Start && localTime < End
        : localTime >= Start || localTime < End;
}

/// <summary>One user-configured alert.</summary>
public sealed record AlertRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool Enabled { get; init; } = true;

    public required AlertTrigger Trigger { get; init; }

    public AlertChannels Channels { get; init; } = AlertChannels.InApp | AlertChannels.Log;

    /// <summary>Area for area-based triggers.</summary>
    public MonitoringArea? Area { get; init; }

    /// <summary>Threshold for <see cref="AlertTrigger.ApproachesWithin"/>, in km.</summary>
    public double? DistanceThresholdKm { get; init; }

    /// <summary>Threshold for <see cref="AlertTrigger.DescendsBelow"/>, in feet.</summary>
    public double? AltitudeThresholdFt { get; init; }

    /// <summary>
    /// Minimum gap between firings for the same aircraft on this rule.
    /// </summary>
    /// <remarks>
    /// The single most important field here. Position updates arrive every few
    /// seconds; without a cooldown an aircraft sitting inside an area would fire
    /// an alert on every poll, which is how a useful feature becomes one the
    /// user disables.
    /// </remarks>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Optional window during which this rule stays silent.</summary>
    public QuietHours? QuietHours { get; init; }

    /// <summary>Evaluates the rule against one aircraft, ignoring cooldown.</summary>
    /// <param name="previous">
    /// The same aircraft's previous state, or <c>null</c> if it was not seen in
    /// the last poll. Edge-triggered rules need it: "entered" means outside then
    /// inside, and without a previous state that transition cannot be observed.
    /// </param>
    public bool Matches(AircraftState aircraft, AircraftState? previous)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        return Trigger switch
        {
            AlertTrigger.EntersArea =>
                Area is not null
                && IsInside(aircraft)
                && (previous is null || !IsInside(previous)),

            // An aircraft that simply stopped being reported has not exited;
            // that is a coverage gap, and treating the two alike would fire
            // spurious exit alerts every time reception dipped.
            AlertTrigger.ExitsArea =>
                Area is not null
                && previous is not null
                && IsInside(previous)
                && !IsInside(aircraft),

            AlertTrigger.ApproachesWithin =>
                DistanceThresholdKm is { } threshold
                && aircraft.DistanceFromObserverKm is { } distance
                && distance <= threshold
                && (previous?.DistanceFromObserverKm is not { } previousDistance
                    || previousDistance > threshold),

            AlertTrigger.EmergencySquawk =>
                aircraft.EmergencyState != EmergencyState.None
                && previous?.EmergencyState is null or EmergencyState.None,

            AlertTrigger.DescendsBelow =>
                AltitudeThresholdFt is { } ceiling
                && aircraft.AltitudeFt is { } altitude
                && altitude < ceiling
                && (previous?.AltitudeFt is not { } previousAltitude
                    || previousAltitude >= ceiling),

            _ => false,
        };
    }

    private bool IsInside(AircraftState aircraft)
        => Area!.Contains(aircraft.Latitude, aircraft.Longitude, aircraft.AltitudeFt);
}

/// <summary>An alert that fired.</summary>
public sealed record AlertEvent(
    string RuleId,
    string RuleName,
    AlertTrigger Trigger,
    AlertChannels Channels,
    string IcaoHex,
    string? Callsign,
    string Message,
    DateTimeOffset FiredAt)
{
    /// <summary>False until the user acknowledges it.</summary>
    public bool Acknowledged { get; init; }
}
