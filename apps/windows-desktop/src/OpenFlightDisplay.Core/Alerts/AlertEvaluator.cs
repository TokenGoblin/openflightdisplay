namespace OpenFlightDisplay.Core.Alerts;

using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Evaluates alert rules against successive polls, applying cooldown,
/// deduplication, quiet hours and a global rate limit.
/// </summary>
/// <remarks>
/// <para>
/// Stateful by necessity: edge-triggered rules ("entered an area") need the
/// previous poll to see a transition, and cooldown needs to remember when a rule
/// last fired for a given aircraft.
/// </para>
/// <para>
/// Deliberately pure otherwise — it raises no notifications and touches no
/// storage. It returns the events that should fire, and the caller decides what
/// to do with them, which is what makes the suppression rules testable without
/// a UI.
/// </para>
/// </remarks>
public sealed class AlertEvaluator
{
    /// <summary>
    /// Most alerts allowed from a single poll.
    /// </summary>
    /// <remarks>
    /// A rule matching broadly — "anything within 50 km" over a busy airport —
    /// could otherwise produce hundreds of notifications from one poll. Beyond
    /// this the remainder are dropped, because a hundred toasts communicates
    /// less than five.
    /// </remarks>
    public const int MaxEventsPerPoll = 5;

    private readonly Dictionary<string, AircraftState> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<(string RuleId, string IcaoHex), DateTimeOffset> _lastFired = new();

    /// <summary>Events raised so far, newest last.</summary>
    private readonly List<AlertEvent> _history = [];

    /// <summary>Everything that has fired this session.</summary>
    public IReadOnlyList<AlertEvent> History => _history;

    /// <summary>
    /// Evaluates one poll's aircraft against the rules.
    /// </summary>
    /// <param name="localNow">
    /// Local time, used for quiet hours. Passed in rather than read from the
    /// clock so the suppression window is testable.
    /// </param>
    public IReadOnlyList<AlertEvent> Evaluate(
        IEnumerable<AircraftState> aircraft,
        IEnumerable<AlertRule> rules,
        DateTimeOffset now,
        TimeOnly localNow)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(rules);

        var current = aircraft.ToList();
        var enabledRules = rules.Where(r => r.Enabled).ToList();
        var fired = new List<AlertEvent>();

        foreach (AircraftState a in current)
        {
            _previous.TryGetValue(a.IcaoHex, out AircraftState? previous);

            foreach (AlertRule rule in enabledRules)
            {
                if (fired.Count >= MaxEventsPerPoll)
                {
                    break;
                }

                if (!rule.Matches(a, previous))
                {
                    continue;
                }

                // Quiet hours suppress the event entirely rather than queueing
                // it. A backlog delivered at 07:00 would be a burst of stale
                // alerts about aircraft that are long gone.
                if (rule.QuietHours?.Contains(localNow) == true)
                {
                    continue;
                }

                var key = (rule.Id, a.IcaoHex);
                if (_lastFired.TryGetValue(key, out DateTimeOffset last)
                    && now - last < rule.Cooldown)
                {
                    continue;
                }

                _lastFired[key] = now;
                fired.Add(BuildEvent(rule, a, now));
            }
        }

        // The previous-state map is replaced, not merged: an aircraft absent
        // from this poll must lose its previous state, so that if it reappears
        // later it is treated as a fresh arrival rather than compared against a
        // position from an hour ago.
        _previous.Clear();
        foreach (AircraftState a in current)
        {
            _previous[a.IcaoHex] = a;
        }

        _history.AddRange(fired);
        return fired;
    }

    /// <summary>Marks an event acknowledged.</summary>
    public void Acknowledge(AlertEvent alertEvent)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        int index = _history.IndexOf(alertEvent);
        if (index >= 0)
        {
            _history[index] = alertEvent with { Acknowledged = true };
        }
    }

    /// <summary>Forgets all transition and cooldown state.</summary>
    /// <remarks>
    /// Called when the data source or monitoring area changes. Carrying state
    /// across would compare aircraft against positions from a different area,
    /// producing entry and exit alerts that never happened.
    /// </remarks>
    public void Reset()
    {
        _previous.Clear();
        _lastFired.Clear();
    }

    private static AlertEvent BuildEvent(AlertRule rule, AircraftState a, DateTimeOffset now)
    {
        string who = a.Callsign ?? a.IcaoHex.ToUpperInvariant();

        string message = rule.Trigger switch
        {
            AlertTrigger.EntersArea => $"{who} entered {rule.Name}.",
            AlertTrigger.ExitsArea => $"{who} left {rule.Name}.",

            AlertTrigger.ApproachesWithin => a.DistanceFromObserverKm is { } d
                ? $"{who} is within {d:N1} km."
                : $"{who} is close.",

            AlertTrigger.EmergencySquawk =>
                $"{who} is squawking {Describe(a.EmergencyState)}.",

            AlertTrigger.DescendsBelow => a.AltitudeFt is { } alt
                ? $"{who} descended to {alt:N0} ft."
                : $"{who} descended.",

            _ => $"{who} matched {rule.Name}.",
        };

        return new AlertEvent(
            rule.Id, rule.Name, rule.Trigger, rule.Channels,
            a.IcaoHex, a.Callsign, message, now);
    }

    private static string Describe(EmergencyState state) => state switch
    {
        EmergencyState.General => "a general emergency",
        EmergencyState.Medical => "a medical emergency",
        EmergencyState.MinimumFuel => "minimum fuel",
        EmergencyState.NoCommunications => "no communications",
        EmergencyState.UnlawfulInterference => "unlawful interference",
        EmergencyState.Downed => "downed aircraft",
        _ => "an emergency",
    };
}
