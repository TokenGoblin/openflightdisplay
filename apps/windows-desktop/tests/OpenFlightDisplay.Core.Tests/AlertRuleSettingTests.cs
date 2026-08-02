namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Settings;
using Xunit;

/// <summary>
/// The saved form of an alert rule. The suppression logic is tested against
/// <see cref="AlertEvaluator"/>; what matters here is that a rule cannot be
/// saved in a state where it silently never fires.
/// </summary>
public class AlertRuleSettingTests
{
    private static readonly CircleArea Area = new(47.6062, -122.3321, RadiusKm: 50.0);

    [Fact]
    public void Never_configured_seeds_the_emergency_rule()
    {
        // Shipping the engine with no rules at all would be dormant code that
        // looks like a feature.
        var settings = new AppSettings();

        AlertRuleSetting rule = Assert.Single(settings.EffectiveAlertRules);
        Assert.Equal(AlertTrigger.EmergencySquawk, rule.Trigger);
    }

    [Fact]
    public void Deleting_every_rule_is_respected_rather_than_reseeded()
    {
        // Distinct from never-configured. Re-adding a rule here would override a
        // decision the user made deliberately.
        var settings = new AppSettings { AlertRules = [] };

        Assert.Empty(settings.EffectiveAlertRules);
    }

    [Fact]
    public void An_area_trigger_binds_to_the_configured_monitoring_area()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Overhead",
            Trigger = AlertTrigger.EntersArea,
        };

        AlertRule rule = setting.ToRule(Area);

        Assert.Same(Area, rule.Area);
    }

    [Fact]
    public void A_non_area_trigger_carries_no_area()
    {
        // Attaching one anyway would be harmless today but would quietly change
        // meaning if Matches ever consulted the area for other triggers.
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Emergency",
            Trigger = AlertTrigger.EmergencySquawk,
        };

        Assert.Null(setting.ToRule(Area).Area);
    }

    [Fact]
    public void An_area_trigger_with_no_area_configured_produces_a_rule_that_cannot_match()
    {
        // Reported by the editor up front; here it must degrade rather than throw.
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Overhead",
            Trigger = AlertTrigger.ExitsArea,
        };

        AlertRule rule = setting.ToRule(null);

        Assert.Null(rule.Area);
    }

    [Fact]
    public void Cooldown_minutes_become_a_timespan()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Close",
            Trigger = AlertTrigger.EmergencySquawk,
            CooldownMinutes = 15,
        };

        Assert.Equal(TimeSpan.FromMinutes(15), setting.ToRule(null).Cooldown);
    }

    [Fact]
    public void Quiet_hours_survive_the_conversion()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Overnight",
            Trigger = AlertTrigger.ApproachesWithin,
            DistanceThresholdKm = 10,
            QuietHoursStart = new TimeOnly(22, 0),
            QuietHoursEnd = new TimeOnly(7, 0),
        };

        QuietHours quiet = Assert.NotNull(setting.ToRule(null).QuietHours);

        // The wrapping window is the normal case for this feature.
        Assert.True(quiet.Contains(new TimeOnly(23, 30)));
        Assert.True(quiet.Contains(new TimeOnly(3, 0)));
        Assert.False(quiet.Contains(new TimeOnly(12, 0)));
    }

    [Theory]
    [InlineData(AlertTrigger.ApproachesWithin, true)]
    [InlineData(AlertTrigger.DescendsBelow, true)]
    [InlineData(AlertTrigger.EmergencySquawk, false)]
    [InlineData(AlertTrigger.EntersArea, false)]
    public void Only_threshold_triggers_need_a_threshold(AlertTrigger trigger, bool expected)
        => Assert.Equal(expected, AlertRuleSetting.NeedsThreshold(trigger));

    [Theory]
    [InlineData(AlertTrigger.EntersArea, true)]
    [InlineData(AlertTrigger.ExitsArea, true)]
    [InlineData(AlertTrigger.ApproachesWithin, false)]
    [InlineData(AlertTrigger.EmergencySquawk, false)]
    public void Only_area_triggers_need_an_area(AlertTrigger trigger, bool expected)
        => Assert.Equal(expected, AlertRuleSetting.NeedsArea(trigger));

    // ---- validation: every case here is a rule that would never fire ----

    [Fact]
    public void A_distance_rule_without_a_distance_is_rejected()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Close",
            Trigger = AlertTrigger.ApproachesWithin,
        };

        Assert.Contains("distance", setting.Validate()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_altitude_rule_without_an_altitude_is_rejected()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Low",
            Trigger = AlertTrigger.DescendsBelow,
        };

        Assert.Contains("altitude", setting.Validate()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_threshold_is_rejected(double threshold)
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Close",
            Trigger = AlertTrigger.ApproachesWithin,
            DistanceThresholdKm = threshold,
        };

        Assert.NotNull(setting.Validate());
    }

    [Fact]
    public void A_nameless_rule_is_rejected()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "   ",
            Trigger = AlertTrigger.EmergencySquawk,
        };

        Assert.NotNull(setting.Validate());
    }

    [Fact]
    public void Half_a_quiet_hours_window_is_rejected()
    {
        // Guessing the other end would silence alerts the user expected to get.
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Overnight",
            Trigger = AlertTrigger.EmergencySquawk,
            QuietHoursStart = new TimeOnly(22, 0),
        };

        Assert.Contains("both", setting.Validate()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quiet_hours_that_cover_the_whole_day_are_rejected()
    {
        // Start == end would silence the rule permanently while still showing as
        // enabled, which is worse than an obviously disabled rule.
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Overnight",
            Trigger = AlertTrigger.EmergencySquawk,
            QuietHoursStart = new TimeOnly(22, 0),
            QuietHoursEnd = new TimeOnly(22, 0),
        };

        Assert.NotNull(setting.Validate());
    }

    [Fact]
    public void A_negative_cooldown_is_rejected()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Close",
            Trigger = AlertTrigger.EmergencySquawk,
            CooldownMinutes = -1,
        };

        Assert.NotNull(setting.Validate());
    }

    [Fact]
    public void The_built_in_emergency_rule_is_valid_and_has_no_quiet_hours()
    {
        AlertRuleSetting rule = AlertRuleSetting.BuiltInEmergency;

        Assert.Null(rule.Validate());

        // A silence window must not apply to an emergency.
        Assert.Null(rule.ToRule(null).QuietHours);
    }

    [Fact]
    public void A_complete_rule_validates()
    {
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Low overhead",
            Trigger = AlertTrigger.DescendsBelow,
            AltitudeThresholdFt = 5000,
            CooldownMinutes = 10,
        };

        Assert.Null(setting.Validate());
    }

    [Fact]
    public void The_summary_states_the_threshold_and_the_cooldown()
    {
        // The list is where a user checks a rule does what they meant, so the
        // numbers that decide whether it fires have to be on it.
        var setting = new AlertRuleSetting
        {
            Id = "r1",
            Name = "Low overhead",
            Trigger = AlertTrigger.DescendsBelow,
            AltitudeThresholdFt = 5000,
            CooldownMinutes = 12,
        };

        string summary = setting.Summarise();

        Assert.Contains("5,000 ft", summary, StringComparison.Ordinal);
        Assert.Contains("12 min", summary, StringComparison.Ordinal);
    }
}
