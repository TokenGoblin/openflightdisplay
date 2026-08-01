namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Ranking;
using Xunit;

/// <summary>
/// Alert evaluation, with the emphasis on the suppression rules — cooldown,
/// deduplication, quiet hours and the per-poll cap. Those are what stand between
/// a useful feature and one the user switches off.
/// </summary>
public class AlertEvaluatorTests
{
    private const double ObserverLat = 47.6062;
    private const double ObserverLon = -122.3321;

    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeOnly Midday = new(12, 0);
    private static readonly CircleArea Area = new(ObserverLat, ObserverLon, RadiusKm: 20.0);

    // ---- entering and leaving ----

    [Fact]
    public void Fires_when_an_aircraft_enters_an_area()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule();

        // First poll: outside. Establishes the previous state.
        evaluator.Evaluate([Outside()], [rule], Now, Midday);

        var fired = evaluator.Evaluate([Inside()], [rule], Now.AddSeconds(15), Midday);

        AlertEvent e = Assert.Single(fired);
        Assert.Equal(AlertTrigger.EntersArea, e.Trigger);
        Assert.Contains("entered", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fires_on_the_first_sighting_inside_an_area()
    {
        // No previous state at all still counts as an entry - an aircraft that
        // appears already inside has, from the display's point of view, arrived.
        var evaluator = new AlertEvaluator();

        Assert.Single(evaluator.Evaluate([Inside()], [AreaRule()], Now, Midday));
    }

    [Fact]
    public void Does_not_fire_again_while_the_aircraft_stays_inside()
    {
        // The whole point of edge triggering: position updates arrive every few
        // seconds and a stationary aircraft must not alert on every one.
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule();

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now, Midday));
        Assert.Empty(evaluator.Evaluate([Inside()], [rule], Now.AddSeconds(15), Midday));
        Assert.Empty(evaluator.Evaluate([Inside()], [rule], Now.AddSeconds(30), Midday));
    }

    [Fact]
    public void Fires_when_an_aircraft_leaves_an_area()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with { Id = "exit", Trigger = AlertTrigger.ExitsArea };

        evaluator.Evaluate([Inside()], [rule], Now, Midday);
        var fired = evaluator.Evaluate([Outside()], [rule], Now.AddSeconds(15), Midday);

        Assert.Equal(AlertTrigger.ExitsArea, Assert.Single(fired).Trigger);
    }

    [Fact]
    public void An_aircraft_that_simply_stops_being_reported_does_not_count_as_leaving()
    {
        // Loss of reception is a coverage gap, not a departure. Conflating them
        // would fire spurious exit alerts every time the signal dipped.
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with { Id = "exit", Trigger = AlertTrigger.ExitsArea };

        evaluator.Evaluate([Inside()], [rule], Now, Midday);

        Assert.Empty(evaluator.Evaluate([], [rule], Now.AddSeconds(15), Midday));
    }

    // ---- cooldown ----

    [Fact]
    public void Cooldown_suppresses_a_repeat_within_the_window()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with { Cooldown = TimeSpan.FromMinutes(10) };

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now, Midday));

        // Leave and re-enter inside the cooldown window.
        evaluator.Evaluate([Outside()], [rule], Now.AddMinutes(1), Midday);
        Assert.Empty(evaluator.Evaluate([Inside()], [rule], Now.AddMinutes(2), Midday));
    }

    [Fact]
    public void Cooldown_allows_a_repeat_once_the_window_has_passed()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with { Cooldown = TimeSpan.FromMinutes(10) };

        evaluator.Evaluate([Inside()], [rule], Now, Midday);
        evaluator.Evaluate([Outside()], [rule], Now.AddMinutes(11), Midday);

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now.AddMinutes(12), Midday));
    }

    [Fact]
    public void Cooldown_is_tracked_per_aircraft_not_globally()
    {
        // One aircraft's alert must not silence a different aircraft's.
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule();

        evaluator.Evaluate([Inside("aaa001")], [rule], Now, Midday);
        var fired = evaluator.Evaluate(
            [Inside("aaa001"), Inside("bbb002")], [rule], Now.AddSeconds(15), Midday);

        Assert.Equal("bbb002", Assert.Single(fired).IcaoHex);
    }

    // ---- quiet hours ----

    [Fact]
    public void Quiet_hours_suppress_alerts_inside_the_window()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with
        {
            QuietHours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)),
        };

        Assert.Empty(evaluator.Evaluate([Inside()], [rule], Now, new TimeOnly(23, 30)));
    }

    [Fact]
    public void Quiet_hours_allow_alerts_outside_the_window()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule() with
        {
            QuietHours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)),
        };

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now, new TimeOnly(12, 0)));
    }

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    public void Quiet_hours_wrap_correctly_past_midnight(int hour, int minute, bool expected)
    {
        // 22:00-07:00 is the normal case for this feature. Treating it as an
        // ordinary ascending range would match nothing at all.
        var window = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));

        Assert.Equal(expected, window.Contains(new TimeOnly(hour, minute)));
    }

    [Fact]
    public void A_non_wrapping_quiet_window_still_works()
    {
        var window = new QuietHours(new TimeOnly(9, 0), new TimeOnly(17, 0));

        Assert.True(window.Contains(new TimeOnly(12, 0)));
        Assert.False(window.Contains(new TimeOnly(20, 0)));
        Assert.False(window.Contains(new TimeOnly(3, 0)));
    }

    // ---- rate limiting ----

    [Fact]
    public void A_single_poll_cannot_produce_more_than_the_event_cap()
    {
        // A broad rule over a busy airport could otherwise emit hundreds of
        // notifications at once, which communicates less than five.
        var evaluator = new AlertEvaluator();

        var many = Enumerable.Range(0, 50).Select(i => Inside($"a{i:d5}"));

        Assert.Equal(
            AlertEvaluator.MaxEventsPerPoll,
            evaluator.Evaluate(many, [AreaRule()], Now, Midday).Count);
    }

    // ---- other triggers ----

    [Fact]
    public void Fires_on_an_emergency_squawk()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRule
        {
            Id = "emg",
            Name = "Emergency",
            Trigger = AlertTrigger.EmergencySquawk,
        };

        var emergency = Inside() with { EmergencyState = EmergencyState.General };

        AlertEvent e = Assert.Single(evaluator.Evaluate([emergency], [rule], Now, Midday));
        Assert.Contains("general emergency", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_re_fire_while_an_emergency_persists()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRule
        {
            Id = "emg",
            Name = "Emergency",
            Trigger = AlertTrigger.EmergencySquawk,
        };

        var emergency = Inside() with { EmergencyState = EmergencyState.General };

        Assert.Single(evaluator.Evaluate([emergency], [rule], Now, Midday));
        Assert.Empty(evaluator.Evaluate([emergency], [rule], Now.AddSeconds(15), Midday));
    }

    [Fact]
    public void Fires_when_an_aircraft_descends_below_a_threshold()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRule
        {
            Id = "low",
            Name = "Low approach",
            Trigger = AlertTrigger.DescendsBelow,
            AltitudeThresholdFt = 5000,
        };

        evaluator.Evaluate([Inside() with { GeometricAltitudeFt = 8000 }], [rule], Now, Midday);

        var fired = evaluator.Evaluate(
            [Inside() with { GeometricAltitudeFt = 4000 }], [rule], Now.AddSeconds(15), Midday);

        Assert.Single(fired);
    }

    [Fact]
    public void An_aircraft_with_no_altitude_does_not_trigger_a_descent_alert()
    {
        // Unknown altitude is not "below the threshold".
        var evaluator = new AlertEvaluator();
        var rule = new AlertRule
        {
            Id = "low",
            Name = "Low approach",
            Trigger = AlertTrigger.DescendsBelow,
            AltitudeThresholdFt = 5000,
        };

        Assert.Empty(evaluator.Evaluate(
            [Inside() with { GeometricAltitudeFt = null }], [rule], Now, Midday));
    }

    // ---- rule state ----

    [Fact]
    public void A_disabled_rule_never_fires()
    {
        var evaluator = new AlertEvaluator();

        Assert.Empty(evaluator.Evaluate([Inside()], [AreaRule() with { Enabled = false }], Now, Midday));
    }

    [Fact]
    public void Reset_clears_transition_and_cooldown_state()
    {
        // Called when the area or data source changes; carrying state across
        // would compare aircraft against positions from a different area.
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule();

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now, Midday));
        evaluator.Reset();

        Assert.Single(evaluator.Evaluate([Inside()], [rule], Now.AddSeconds(15), Midday));
    }

    [Fact]
    public void History_accumulates_fired_events()
    {
        var evaluator = new AlertEvaluator();
        AlertRule rule = AreaRule();

        evaluator.Evaluate([Inside("aaa001")], [rule], Now, Midday);
        evaluator.Evaluate([Inside("aaa001"), Inside("bbb002")], [rule], Now.AddSeconds(15), Midday);

        Assert.Equal(2, evaluator.History.Count);
    }

    [Fact]
    public void Acknowledging_an_event_marks_it_in_history()
    {
        var evaluator = new AlertEvaluator();
        AlertEvent e = Assert.Single(evaluator.Evaluate([Inside()], [AreaRule()], Now, Midday));

        Assert.False(e.Acknowledged);
        evaluator.Acknowledge(e);

        Assert.True(Assert.Single(evaluator.History).Acknowledged);
    }

    private static AlertRule AreaRule() => new()
    {
        Id = "area-1",
        Name = "Overhead",
        Trigger = AlertTrigger.EntersArea,
        Area = Area,
    };

    private static AircraftState Inside(string hex = "aaa001")
        => Ranked(hex, ObserverLat + 0.02, ObserverLon);

    private static AircraftState Outside(string hex = "aaa001")
        => Ranked(hex, ObserverLat + 2.0, ObserverLon);

    private static AircraftState Ranked(string hex, double lat, double lon)
        => AircraftRanker.WithObserverGeometry(
            new AircraftState
            {
                Provider = "test",
                IcaoHex = hex,
                Latitude = lat,
                Longitude = lon,
                GeometricAltitudeFt = 10000,
                FirstSeen = Now,
                LastSeen = Now,
                PositionTimestamp = Now,
            },
            ObserverLat,
            ObserverLon);
}
