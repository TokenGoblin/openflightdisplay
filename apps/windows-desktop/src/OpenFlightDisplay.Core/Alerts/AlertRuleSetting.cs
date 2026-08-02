namespace OpenFlightDisplay.Core.Alerts;

using System.Globalization;
using OpenFlightDisplay.Core.Areas;

/// <summary>
/// An alert rule in the form that is saved to disk.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AlertRule"/> because a rule carries a
/// <see cref="MonitoringArea"/>, which is a polymorphic type with three
/// implementations. Serializing that would need a discriminator and a converter,
/// and would let a saved rule disagree with the area the user is actually
/// monitoring.
/// </para>
/// <para>
/// Instead, area-based rules bind to <b>the configured monitoring area</b> at
/// the moment they are used — see <see cref="ToRule"/>. One area, defined in one
/// place, so changing it cannot leave rules pointing at the old one. When a
/// per-rule area editor arrives this is where it plugs in.
/// </para>
/// </remarks>
public sealed record AlertRuleSetting
{
    /// <summary>Identifier of the rule that ships when none are configured.</summary>
    public const string BuiltInEmergencyId = "builtin-emergency";

    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool Enabled { get; init; } = true;

    public required AlertTrigger Trigger { get; init; }

    /// <summary>
    /// How the user is told.
    /// </summary>
    /// <remarks>
    /// Only <see cref="AlertChannels.Toast"/> is a real choice. Every alert is
    /// recorded in the in-app list regardless, and
    /// <see cref="AlertChannels.Sound"/> has no implementation — the editor does
    /// not offer it rather than presenting a switch that does nothing.
    /// </remarks>
    public AlertChannels Channels { get; init; } =
        AlertChannels.InApp | AlertChannels.Toast | AlertChannels.Log;

    /// <summary>Threshold for <see cref="AlertTrigger.ApproachesWithin"/>, in km.</summary>
    public double? DistanceThresholdKm { get; init; }

    /// <summary>Threshold for <see cref="AlertTrigger.DescendsBelow"/>, in feet.</summary>
    public double? AltitudeThresholdFt { get; init; }

    /// <summary>Minimum gap between firings for the same aircraft, in minutes.</summary>
    public int CooldownMinutes { get; init; } = 10;

    /// <summary>Start of the silence window, or <c>null</c> for none.</summary>
    public TimeOnly? QuietHoursStart { get; init; }

    /// <inheritdoc cref="QuietHoursStart"/>
    public TimeOnly? QuietHoursEnd { get; init; }

    /// <summary>
    /// The rule installed when a user has never configured any.
    /// </summary>
    /// <remarks>
    /// Emergency squawks: the one alert unambiguously worth interrupting someone
    /// for, needing no configuration and unable to produce a stream of noise.
    /// Shipping the engine with no rules at all would be dormant code that looks
    /// like a feature. It carries no quiet hours by design — a silence window
    /// should not apply to an emergency.
    /// </remarks>
    public static AlertRuleSetting BuiltInEmergency => new()
    {
        Id = BuiltInEmergencyId,
        Name = "Emergency squawk",
        Trigger = AlertTrigger.EmergencySquawk,
        Channels = AlertChannels.InApp | AlertChannels.Toast | AlertChannels.Log,
        CooldownMinutes = 15,
    };

    /// <summary>True if this trigger needs a monitoring area to mean anything.</summary>
    public static bool NeedsArea(AlertTrigger trigger)
        => trigger is AlertTrigger.EntersArea or AlertTrigger.ExitsArea;

    /// <summary>True if this trigger needs a numeric threshold.</summary>
    public static bool NeedsThreshold(AlertTrigger trigger)
        => trigger is AlertTrigger.ApproachesWithin or AlertTrigger.DescendsBelow;

    /// <summary>Short description of what a trigger watches for.</summary>
    public static string Describe(AlertTrigger trigger) => trigger switch
    {
        AlertTrigger.EntersArea => "An aircraft comes inside the monitoring area",
        AlertTrigger.ExitsArea => "An aircraft leaves the monitoring area",
        AlertTrigger.ApproachesWithin => "An aircraft comes within a distance of you",
        AlertTrigger.EmergencySquawk => "An aircraft squawks an emergency code",
        AlertTrigger.DescendsBelow => "An aircraft descends below an altitude",
        _ => "Unknown trigger",
    };

    /// <summary>
    /// Checks the rule is complete enough to do what it claims.
    /// </summary>
    /// <returns>
    /// A reason the rule is unusable, or <c>null</c> if it is fine. Wording is
    /// user-facing: a rule saved without its threshold would never fire, and
    /// silently never firing is the worst outcome for an alert.
    /// </returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Give the rule a name so you can recognise it in the list.";
        }

        if (CooldownMinutes < 0)
        {
            return "Cooldown cannot be negative.";
        }

        // No upper bound is enforced, but a rule that fires at most once a day
        // is almost certainly a typo rather than an intention.
        if (CooldownMinutes > 1440)
        {
            return "A cooldown longer than a day means the rule fires at most once. "
                + "Enter a smaller number, or disable the rule instead.";
        }

        if (Trigger == AlertTrigger.ApproachesWithin)
        {
            if (DistanceThresholdKm is not { } distance)
            {
                return "Enter the distance to alert within.";
            }

            if (distance <= 0)
            {
                return "The distance must be greater than zero.";
            }
        }

        if (Trigger == AlertTrigger.DescendsBelow)
        {
            if (AltitudeThresholdFt is not { } altitude)
            {
                return "Enter the altitude to alert below.";
            }

            if (altitude <= 0)
            {
                return "The altitude must be greater than zero.";
            }
        }

        // Both or neither: one half of a window cannot be interpreted, and
        // guessing the other end would silence alerts the user expected.
        if (QuietHoursStart is null != QuietHoursEnd is null)
        {
            return "Set both a start and an end for quiet hours, or neither.";
        }

        if (QuietHoursStart is { } start && QuietHoursEnd is { } end && start == end)
        {
            return "Quiet hours that start and end at the same time would silence "
                + "the rule permanently. Disable the rule instead.";
        }

        return null;
    }

    /// <summary>
    /// Converts to the evaluator's rule, binding the current monitoring area.
    /// </summary>
    /// <param name="monitoringArea">
    /// The area the application is monitoring, or <c>null</c> if none is
    /// configured. An area-based rule with no area produces a rule that cannot
    /// match — <see cref="AlertRule.Matches"/> already requires a non-null area —
    /// rather than throwing. The editor warns about this case up front.
    /// </param>
    public AlertRule ToRule(MonitoringArea? monitoringArea) => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Trigger = Trigger,
        Channels = Channels,
        Area = NeedsArea(Trigger) ? monitoringArea : null,
        DistanceThresholdKm = DistanceThresholdKm,
        AltitudeThresholdFt = AltitudeThresholdFt,
        Cooldown = TimeSpan.FromMinutes(CooldownMinutes),
        QuietHours = QuietHoursStart is { } start && QuietHoursEnd is { } end
            ? new QuietHours(start, end)
            : null,
    };

    /// <summary>One line describing the rule, for the list.</summary>
    public string Summarise()
    {
        string detail = Trigger switch
        {
            AlertTrigger.ApproachesWithin when DistanceThresholdKm is { } km =>
                string.Create(CultureInfo.CurrentCulture, $"Within {km:N1} km of you"),

            AlertTrigger.DescendsBelow when AltitudeThresholdFt is { } ft =>
                string.Create(CultureInfo.CurrentCulture, $"Below {ft:N0} ft"),

            _ => Describe(Trigger),
        };

        string cooldown = string.Create(
            CultureInfo.CurrentCulture,
            $"{CooldownMinutes} min cooldown");

        string quiet = QuietHoursStart is { } start && QuietHoursEnd is { } end
            ? string.Create(CultureInfo.CurrentCulture, $", quiet {start:HH\\:mm}–{end:HH\\:mm}")
            : string.Empty;

        string toast = Channels.HasFlag(AlertChannels.Toast) ? ", toast" : string.Empty;

        return $"{detail} · {cooldown}{quiet}{toast}";
    }
}
