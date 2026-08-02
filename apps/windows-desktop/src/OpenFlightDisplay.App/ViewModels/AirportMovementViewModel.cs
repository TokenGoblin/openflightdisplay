namespace OpenFlightDisplay.App.ViewModels;

using System.Globalization;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Core.Units;

/// <summary>One row on the airport movements board.</summary>
/// <remarks>
/// Formatted once at construction, like the other row view models. The board is
/// rebuilt on every poll, so there is nothing to notify about.
/// </remarks>
public sealed class AirportMovementViewModel
{
    public AirportMovementViewModel(AirportMovement movement, UnitSystem units)
    {
        Movement = AirportMovements.KindWord(movement.Kind);

        // Never blank: an aircraft with no callsign is still identified, by the
        // hex it must broadcast.
        Callsign = string.IsNullOrWhiteSpace(movement.Aircraft.Callsign)
            ? movement.Aircraft.IcaoHex.ToUpperInvariant()
            : movement.Aircraft.Callsign;

        Distance = string.Create(
            CultureInfo.CurrentCulture,
            $"{UnitConverter.DistanceFromKm(movement.DistanceKm, units):N1} " +
            $"{UnitConverter.DistanceUnitLabel(units)}");

        Height = AirportMovements.FormatHeight(movement.HeightAboveFieldFt);
        MinutesAway = AirportMovements.FormatMinutesAway(movement.Kind, movement.MinutesAway);

        // Type and registration are enrichment, not observation, and are simply
        // absent when the provider did not supply them.
        string type = movement.Aircraft.AircraftTypeCode ?? string.Empty;
        string registration = movement.Aircraft.Registration ?? string.Empty;

        TypeAndRegistration = string.Join(
            "  ",
            new[] { type, registration }.Where(s => !string.IsNullOrWhiteSpace(s)));

        AccessibleDescription = string.Create(
            CultureInfo.CurrentCulture,
            $"{Callsign}, {Movement.ToLowerInvariant()}, {Distance} from the field, " +
            $"{Height} above it.");
    }

    /// <summary>ARRIVING, DEPARTING, ON GROUND, OVERFLIGHT or UNKNOWN.</summary>
    public string Movement { get; }

    public string Callsign { get; }

    public string Distance { get; }

    /// <summary>Height above the field, not sea level.</summary>
    public string Height { get; }

    public string MinutesAway { get; }

    public string TypeAndRegistration { get; }

    /// <summary>The row as one sentence, for a screen reader.</summary>
    public string AccessibleDescription { get; }
}
