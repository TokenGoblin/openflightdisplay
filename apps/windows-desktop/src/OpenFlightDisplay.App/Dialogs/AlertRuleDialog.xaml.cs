namespace OpenFlightDisplay.App.Dialogs;

using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.Core.Alerts;

/// <summary>
/// Creates or edits one alert rule.
/// </summary>
/// <remarks>
/// <para>
/// Validation happens on save and <b>cancels the close</b> when it fails. A rule
/// that is saved incomplete does not fail loudly — it simply never fires, which
/// is the worst possible outcome for an alert and impossible to notice.
/// </para>
/// <para>
/// Only the channels that actually do something are offered.
/// <see cref="AlertChannels.Sound"/> exists in the domain but nothing plays a
/// sound, so a switch for it would be a lie.
/// </para>
/// </remarks>
public sealed partial class AlertRuleDialog : ContentDialog
{
    private readonly string _id;
    private readonly bool _hasMonitoringArea;

    /// <summary>
    /// Creates the dialog.
    /// </summary>
    /// <param name="existing">The rule being edited, or <c>null</c> to add one.</param>
    /// <param name="hasMonitoringArea">
    /// Whether a home location is configured. An area rule without one can never
    /// match, and the user is told before they save rather than left wondering
    /// why nothing fires.
    /// </param>
    public AlertRuleDialog(AlertRuleSetting? existing, bool hasMonitoringArea)
    {
        InitializeComponent();

        _hasMonitoringArea = hasMonitoringArea;

        // A new rule gets a fresh id; an edit keeps its own so the evaluator's
        // cooldown state stays attached to it rather than resetting on save.
        _id = existing?.Id ?? $"rule-{Guid.NewGuid():N}";

        foreach (AlertTrigger trigger in Enum.GetValues<AlertTrigger>())
        {
            TriggerBox.Items.Add(new ComboBoxItem
            {
                Content = AlertRuleSetting.Describe(trigger),
                Tag = trigger,
            });
        }

        if (existing is null)
        {
            Title = "New alert rule";
            TriggerBox.SelectedIndex = 0;
            CooldownBox.Text = "10";
            ToastCheck.IsChecked = true;
            return;
        }

        Title = "Edit alert rule";
        NameBox.Text = existing.Name;

        foreach (object candidate in TriggerBox.Items)
        {
            if (candidate is ComboBoxItem { Tag: AlertTrigger t } && t == existing.Trigger)
            {
                TriggerBox.SelectedItem = candidate;
                break;
            }
        }

        DistanceBox.Text = existing.DistanceThresholdKm?.ToString(CultureInfo.CurrentCulture)
            ?? string.Empty;
        AltitudeBox.Text = existing.AltitudeThresholdFt?.ToString(CultureInfo.CurrentCulture)
            ?? string.Empty;
        CooldownBox.Text = existing.CooldownMinutes.ToString(CultureInfo.CurrentCulture);
        ToastCheck.IsChecked = existing.Channels.HasFlag(AlertChannels.Toast);

        if (existing.QuietHoursStart is { } start && existing.QuietHoursEnd is { } end)
        {
            QuietCheck.IsChecked = true;
            QuietStart.SelectedTime = start.ToTimeSpan();
            QuietEnd.SelectedTime = end.ToTimeSpan();
        }
    }

    /// <summary>The saved rule, or <c>null</c> if the dialog was cancelled.</summary>
    /// <remarks>
    /// Internal, not public. XAML's type-info generator walks every public
    /// property of a XAML-backed class and emits an activation stub for its
    /// type; <see cref="AlertRuleSetting"/> has required members it cannot
    /// supply, so a public property here fails the build. Only MainWindow reads
    /// this, and it is in the same assembly.
    /// </remarks>
    internal AlertRuleSetting? Result { get; private set; }

    private AlertTrigger SelectedTrigger =>
        TriggerBox.SelectedItem is ComboBoxItem { Tag: AlertTrigger trigger }
            ? trigger
            : AlertTrigger.EmergencySquawk;

    private void OnTriggerChanged(object sender, SelectionChangedEventArgs e)
    {
        AlertTrigger trigger = SelectedTrigger;

        DistanceBox.Visibility = trigger == AlertTrigger.ApproachesWithin
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        AltitudeBox.Visibility = trigger == AlertTrigger.DescendsBelow
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // Said now rather than on save: the user is choosing the trigger, which
        // is the moment the missing location is relevant.
        AreaNote.IsOpen = AlertRuleSetting.NeedsArea(trigger) && !_hasMonitoringArea;
    }

    private void OnQuietToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => QuietPanel.Visibility = QuietCheck.IsChecked is true
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        AlertTrigger trigger = SelectedTrigger;

        if (!TryReadThreshold(DistanceBox.Text, trigger == AlertTrigger.ApproachesWithin,
                out double? distanceKm))
        {
            Reject(args, "The distance must be a number, in kilometres.");
            return;
        }

        if (!TryReadThreshold(AltitudeBox.Text, trigger == AlertTrigger.DescendsBelow,
                out double? altitudeFt))
        {
            Reject(args, "The altitude must be a number, in feet.");
            return;
        }

        if (!int.TryParse(CooldownBox.Text, CultureInfo.CurrentCulture, out int cooldown))
        {
            Reject(args, "The cooldown must be a whole number of minutes.");
            return;
        }

        TimeOnly? quietStart = null;
        TimeOnly? quietEnd = null;

        if (QuietCheck.IsChecked is true)
        {
            if (QuietStart.SelectedTime is not { } start || QuietEnd.SelectedTime is not { } end)
            {
                Reject(args, "Choose both a start and an end for quiet hours.");
                return;
            }

            quietStart = TimeOnly.FromTimeSpan(start);
            quietEnd = TimeOnly.FromTimeSpan(end);
        }

        var candidate = new AlertRuleSetting
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Enabled = true,
            Trigger = trigger,

            // InApp and Log are always on: the alert list is the record of what
            // happened, and turning off the record would leave a user unable to
            // tell whether a rule ever fired.
            Channels = AlertChannels.InApp
                | AlertChannels.Log
                | (ToastCheck.IsChecked is true ? AlertChannels.Toast : AlertChannels.None),

            DistanceThresholdKm = distanceKm,
            AltitudeThresholdFt = altitudeFt,
            CooldownMinutes = cooldown,
            QuietHoursStart = quietStart,
            QuietHoursEnd = quietEnd,
        };

        // The domain has the final word, so the rules that decide whether an
        // alert can fire live in one place rather than being restated here.
        if (candidate.Validate() is { } problem)
        {
            Reject(args, problem);
            return;
        }

        Result = candidate;
    }

    /// <summary>
    /// Reports a problem and keeps the dialog open.
    /// </summary>
    private void Reject(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        ValidationError.Message = message;
        ValidationError.IsOpen = true;
    }

    /// <summary>
    /// Reads a threshold field, ignoring it entirely when the trigger does not
    /// use it — so a value left over from a previously selected trigger is not
    /// silently saved onto a rule that has no use for it.
    /// </summary>
    private static bool TryReadThreshold(string? text, bool required, out double? value)
    {
        value = null;

        if (!required)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Left to Validate, which words the "you need a threshold" message.
            return true;
        }

        if (!double.TryParse(text, CultureInfo.CurrentCulture, out double parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
