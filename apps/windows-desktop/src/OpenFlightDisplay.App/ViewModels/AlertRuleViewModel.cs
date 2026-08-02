namespace OpenFlightDisplay.App.ViewModels;

using OpenFlightDisplay.Core.Alerts;

/// <summary>
/// One row in the alert-rules list.
/// </summary>
/// <remarks>
/// Formatted once at construction, like <see cref="AircraftRowViewModel"/>: the
/// list is rebuilt whenever the rules change, so there is nothing to notify
/// about and no need for change tracking.
/// </remarks>
public sealed class AlertRuleViewModel
{
    public AlertRuleViewModel(AlertRuleSetting rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Id = rule.Id;
        Name = rule.Name;
        Enabled = rule.Enabled;
        Summary = rule.Summarise();
    }

    // The underlying AlertRuleSetting is deliberately NOT exposed. XAML's
    // type-info generator emits an activation stub for every bindable property
    // type, and AlertRuleSetting has required members it cannot supply, so
    // exposing it fails the build. Handlers look the rule up by Id instead,
    // which also keeps the settings record the single source of truth.

    public string Id { get; }

    public string Name { get; }

    public bool Enabled { get; }

    /// <summary>Trigger, thresholds, cooldown and quiet hours in one line.</summary>
    public string Summary { get; }

    // Per-row accessible names. Without them a screen reader announces an
    // unlabelled toggle and a column of identical "Edit" and "Delete" buttons,
    // with nothing to say which rule any of them belongs to.

    /// <summary>Accessible name for the enable toggle.</summary>
    public string ToggleAccessibleName => $"Enable {Name}";

    /// <summary>Accessible name for the edit button.</summary>
    public string EditAccessibleName => $"Edit {Name}";

    /// <summary>Accessible name for the delete button.</summary>
    public string DeleteAccessibleName => $"Delete {Name}";
}
