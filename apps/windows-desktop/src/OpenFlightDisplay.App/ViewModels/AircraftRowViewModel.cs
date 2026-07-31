namespace OpenFlightDisplay.App.ViewModels;

using System.Globalization;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Units;

/// <summary>
/// One row of the flight board, pre-formatted for display.
/// </summary>
/// <remarks>
/// <para>
/// Formatting happens here rather than in XAML converters so it can be unit
/// tested. The rules it encodes are not cosmetic.
/// </para>
/// <para>
/// <b>Missing data renders as an em dash, never as zero.</b> An aircraft that
/// did not report a groundspeed is not doing 0 knots, and a board that shows
/// "0" for both is lying about one of them. This is the single most repeated
/// rule in the project and the easiest to lose at the view layer.
/// </para>
/// </remarks>
public sealed class AircraftRowViewModel
{
    /// <summary>Shown wherever a provider reported nothing.</summary>
    public const string NoData = "—";

    private readonly AircraftState _aircraft;
    private readonly UnitSystem _units;

    public AircraftRowViewModel(AircraftState aircraft, UnitSystem units, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        _aircraft = aircraft;
        _units = units;
        AgeSeconds = (int)Core.Quality.Staleness.Age(aircraft.PositionTimestamp, now).TotalSeconds;
    }

    /// <summary>Underlying record, for the detail pane and diagnostics.</summary>
    public AircraftState Aircraft => _aircraft;

    public string IcaoHex => _aircraft.IcaoHex.ToUpperInvariant();

    /// <summary>Callsign, or the ICAO hex when none was reported.</summary>
    public string Callsign => _aircraft.Callsign ?? IcaoHex;

    public string Registration => _aircraft.Registration ?? NoData;

    public string AircraftType => _aircraft.AircraftTypeCode ?? NoData;

    public string Squawk => _aircraft.Squawk ?? NoData;

    public string Distance => Format(
        _aircraft.DistanceFromObserverKm is { } km
            ? UnitConverter.DistanceFromKm(km, _units)
            : null,
        "N1",
        UnitConverter.DistanceUnitLabel(_units));

    public string Bearing => _aircraft.BearingFromObserverDeg is { } deg
        ? string.Create(CultureInfo.CurrentCulture, $"{deg:N0}° {Compass(deg)}")
        : NoData;

    public string Altitude => _aircraft.OnGround
        ? "GROUND"
        : Format(
            _aircraft.AltitudeFt is { } ft ? UnitConverter.AltitudeFromFeet(ft, _units) : null,
            "N0",
            UnitConverter.AltitudeUnitLabel(_units));

    public string GroundSpeed => Format(
        _aircraft.GroundSpeedKt is { } kt ? UnitConverter.SpeedFromKnots(kt, _units) : null,
        "N0",
        UnitConverter.SpeedUnitLabel(_units));

    public string VerticalRate => _aircraft.VerticalRateFtPerMin is { } rate
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"{UnitConverter.VerticalRateFromFeetPerMinute(rate, _units):+#,##0;-#,##0;0} " +
            $"{UnitConverter.VerticalRateUnitLabel(_units)}")
        : NoData;

    /// <summary>
    /// Text label for the vertical trend.
    /// </summary>
    /// <remarks>
    /// A word, not just an arrow glyph or a colour. The accessibility rule is
    /// that climb, descend, stale, emergency and provider status must never be
    /// communicated by colour alone.
    /// </remarks>
    public string TrendLabel => _aircraft.VerticalTrend switch
    {
        VerticalTrend.Climbing => "CLIMB",
        VerticalTrend.Descending => "DESCEND",
        VerticalTrend.Level => "LEVEL",
        VerticalTrend.OnGround => "GROUND",
        _ => NoData,
    };

    public string TrendGlyph => _aircraft.VerticalTrend switch
    {
        VerticalTrend.Climbing => "▲",
        VerticalTrend.Descending => "▼",
        VerticalTrend.Level => "▬",
        VerticalTrend.OnGround => "⌂",
        _ => NoData,
    };

    /// <summary>Seconds since the position was observed.</summary>
    public int AgeSeconds { get; }

    public string Age => string.Create(CultureInfo.CurrentCulture, $"{AgeSeconds}s");

    public bool IsStale => _aircraft.DataQualityFlags.HasFlag(DataQualityFlags.StalePosition);

    public bool HasEmergency => _aircraft.EmergencyState != EmergencyState.None;

    /// <summary>Emergency wording, or empty when there is none.</summary>
    public string EmergencyLabel => _aircraft.EmergencyState switch
    {
        EmergencyState.None => string.Empty,
        EmergencyState.General => "EMERGENCY",
        EmergencyState.Medical => "MEDICAL",
        EmergencyState.MinimumFuel => "MIN FUEL",
        EmergencyState.NoCommunications => "NO COMMS",
        EmergencyState.UnlawfulInterference => "UNLAWFUL",
        EmergencyState.Downed => "DOWNED",
        _ => string.Empty,
    };

    /// <summary>Provider that supplied this record, for attribution.</summary>
    public string Source => _aircraft.Provider;

    /// <summary>
    /// Screen-reader description of the whole row.
    /// </summary>
    /// <remarks>
    /// Built explicitly rather than left to the default column concatenation,
    /// which would read a row of raw numbers with no units or context.
    /// </remarks>
    public string AccessibleDescription
    {
        get
        {
            string emergency = HasEmergency ? $", {EmergencyLabel}" : string.Empty;
            string stale = IsStale ? ", position stale" : string.Empty;
            return $"{Callsign}, {Distance} away, bearing {Bearing}, " +
                   $"altitude {Altitude}, {TrendLabel}{emergency}{stale}";
        }
    }

    private static string Format(double? value, string numericFormat, string unitLabel)
        => value is { } v
            ? string.Create(CultureInfo.CurrentCulture, $"{v.ToString(numericFormat, CultureInfo.CurrentCulture)} {unitLabel}")
            : NoData;

    /// <summary>16-point compass abbreviation for a bearing.</summary>
    internal static string Compass(double degrees)
    {
        string[] points =
        [
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
        ];

        int index = (int)Math.Round(degrees / 22.5, MidpointRounding.AwayFromZero) % 16;
        return points[index];
    }
}
