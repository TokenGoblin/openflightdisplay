namespace OpenFlightDisplay.App.ViewModels;

using System.Globalization;
using OpenFlightDisplay.Persistence;

/// <summary>
/// One aircraft's aggregate row in the history list.
/// </summary>
/// <remarks>
/// Formatted once at construction. The list is rebuilt whenever the period
/// changes, so there is nothing to notify about.
/// </remarks>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(AircraftSummary summary)
    {
        IcaoHex = summary.IcaoHex.ToUpperInvariant();

        // An aircraft that never transmitted a callsign shows its hex rather
        // than an empty cell, so a row is never anonymous on screen. The hex
        // column still carries it, which is deliberate: matching a blank
        // callsign to an aircraft is exactly what the hex is for.
        Callsign = string.IsNullOrWhiteSpace(summary.Callsign)
            ? "(no callsign)"
            : summary.Callsign;

        Observations = string.Create(
            CultureInfo.CurrentCulture,
            $"{summary.Observations:N0} obs");

        Seen = string.Create(
            CultureInfo.CurrentCulture,
            $"{summary.FirstSeen.ToLocalTime():dd MMM HH:mm} – {summary.LastSeen.ToLocalTime():dd MMM HH:mm}");
    }

    public string IcaoHex { get; }

    public string Callsign { get; }

    public string Observations { get; }

    /// <summary>First and last time this aircraft was seen in the period.</summary>
    public string Seen { get; }

    /// <summary>
    /// Accessible name for this row's export button.
    /// </summary>
    /// <remarks>
    /// Every row's button reads "Export trail", so without this a screen reader
    /// announces a column of identical buttons with no way to tell which
    /// aircraft each one belongs to.
    /// </remarks>
    public string ExportAccessibleName => $"Export trail for {Callsign}";

    /// <summary>
    /// Everything in the row, as one sentence.
    /// </summary>
    /// <remarks>
    /// A screen reader reads the cells separately and loses the relationship
    /// between them; this gives the row a single coherent announcement.
    /// </remarks>
    public string AccessibleDescription =>
        $"{Callsign}, {IcaoHex}, {Observations}, seen {Seen}";
}
