namespace OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// A normalized aircraft observation, independent of any specific provider.
///
/// Mirrors <c>AircraftStateSchema</c> in
/// <c>packages/shared-models/src/aircraft.ts</c> and <c>ofd::AircraftState</c>
/// in <c>firmware/display/include/domain/aircraft.h</c>.
/// <c>docs/PROTOCOL.md</c> is the contract of record when they disagree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nullable means "the provider did not report this".</b> That distinction is
/// load-bearing throughout the project and is why every optional measurement is
/// <c>double?</c> rather than <c>double</c> with a sentinel. An aircraft with no
/// reported groundspeed is not an aircraft doing 0 knots — one is unknown, the
/// other is stationary, and a UI that renders them identically is lying. The
/// firmware carries explicit <c>hasGroundSpeedKt</c>-style companion booleans to
/// achieve the same thing in C++; C# gets it from the type system.
/// </para>
/// <para>
/// This is an immutable record. Position updates produce a new instance rather
/// than mutating in place, so the ranking and filtering pipeline can never
/// observe a half-updated aircraft.
/// </para>
/// </remarks>
public sealed record AircraftState
{
    // ---- identity ----

    /// <summary>Provider that supplied this observation, e.g. "adsblol".</summary>
    public required string Provider { get; init; }

    /// <summary>24-bit ICAO address, lowercase hex, exactly 6 characters.</summary>
    public required string IcaoHex { get; init; }

    /// <summary>Trimmed ADS-B callsign. adsb.lol space-pads these to 8 chars.</summary>
    public string? Callsign { get; init; }

    public string? Registration { get; init; }
    public string? Operator { get; init; }
    public string? AirlineCode { get; init; }
    public string? FlightNumber { get; init; }

    // ---- type ----

    public string? AircraftTypeCode { get; init; }
    public string? AircraftDescription { get; init; }

    /// <summary><c>null</c> when the provider reported no category at all.</summary>
    public AircraftCategory? AircraftCategory { get; init; }

    // ---- position and movement ----

    public required double Latitude { get; init; }
    public required double Longitude { get; init; }

    public double? GeometricAltitudeFt { get; init; }
    public double? BarometricAltitudeFt { get; init; }
    public double? GroundSpeedKt { get; init; }
    public double? TrackHeadingDeg { get; init; }
    public double? VerticalRateFtPerMin { get; init; }

    /// <summary>Four octal digits, or <c>null</c> if not reported / malformed.</summary>
    public string? Squawk { get; init; }

    public EmergencyState EmergencyState { get; init; } = EmergencyState.None;

    public bool OnGround { get; init; }

    // ---- route ----

    /// <summary>
    /// Origin airport, only ever populated from a provider that legitimately
    /// supplies route data.
    /// </summary>
    /// <remarks>
    /// <b>Never inferred.</b> ADS-B carries no route. adsb.lol's route-inference
    /// endpoint returns an empty 201. If this is <c>null</c> the UI shows nothing
    /// — it does not guess from the callsign prefix.
    /// </remarks>
    public string? OriginAirport { get; init; }

    /// <inheritdoc cref="OriginAirport"/>
    public string? DestinationAirport { get; init; }

    // ---- observer-relative, computed not reported ----

    public double? DistanceFromObserverKm { get; init; }
    public double? BearingFromObserverDeg { get; init; }
    public double? SlantRangeKm { get; init; }
    public bool? Approaching { get; init; }

    // ---- timestamps ----

    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public required DateTimeOffset PositionTimestamp { get; init; }
    public DateTimeOffset? EnrichmentTimestamp { get; init; }

    public DataQualityFlags DataQualityFlags { get; init; } = DataQualityFlags.None;

    // ---- derived ----

    /// <summary>
    /// Best available altitude: geometric preferred, barometric as fallback.
    /// <c>null</c> when neither was reported.
    /// </summary>
    public double? AltitudeFt => GeometricAltitudeFt ?? BarometricAltitudeFt;

    /// <summary>
    /// Vertical trend. Returns <see cref="VerticalTrend.Unknown"/> — not
    /// <see cref="VerticalTrend.Level"/> — when no vertical rate was reported,
    /// so a colour-independent indicator can distinguish "level" from "we do not
    /// know".
    /// </summary>
    public VerticalTrend VerticalTrend
    {
        get
        {
            if (OnGround)
            {
                return VerticalTrend.OnGround;
            }

            if (VerticalRateFtPerMin is not { } rate)
            {
                return VerticalTrend.Unknown;
            }

            // +/-100 ft/min deadband: ADS-B vertical rate is noisy and an
            // aircraft in level cruise reports small nonzero values constantly.
            return rate switch
            {
                > 100.0 => VerticalTrend.Climbing,
                < -100.0 => VerticalTrend.Descending,
                _ => VerticalTrend.Level,
            };
        }
    }
}
