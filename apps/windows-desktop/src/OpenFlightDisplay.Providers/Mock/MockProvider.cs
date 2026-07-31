namespace OpenFlightDisplay.Providers.Mock;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;

/// <summary>
/// Synthetic aircraft that actually move. No network, no API key.
/// </summary>
/// <remarks>
/// <para>
/// This is the default data mode on first run and the one the offline
/// requirement rests on — the application must be fully usable with no internet
/// connection. It is also what makes the radar and flight board developable
/// without hammering a free community service.
/// </para>
/// <para>
/// Positions are a deterministic function of the aircraft's seed and the current
/// time, not a random walk. Two consequences worth having: the same instant
/// always produces the same picture, so a screenshot is reproducible; and there
/// is no accumulated drift over a long soak run.
/// </para>
/// </remarks>
public sealed class MockProvider : IAviationDataProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly int _aircraftCount;

    /// <param name="aircraftCount">
    /// How many aircraft to synthesise. Defaults to 12 — enough to exercise a
    /// flight board and overlapping map symbols. The performance tests drive
    /// this up to 1,000.
    /// </param>
    /// <param name="timeProvider">
    /// Injected so tests can advance time deterministically instead of sleeping.
    /// </param>
    public MockProvider(int aircraftCount = 12, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(aircraftCount);

        _aircraftCount = aircraftCount;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Id => "mock";

    /// <inheritdoc/>
    public string DisplayName => "Mock data (offline)";

    /// <inheritdoc/>
    public bool RequiresApiKey => false;

    /// <inheritdoc/>
    public TimeSpan RecommendedPollInterval => TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    public Task<ProviderResult> FetchAircraftAsync(
        MonitoringArea area,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        (double centerLat, double centerLon, double radiusKm) = Center(area);

        var aircraft = new List<AircraftState>(_aircraftCount);
        for (int i = 0; i < _aircraftCount; i++)
        {
            aircraft.Add(Synthesise(i, centerLat, centerLon, radiusKm, now));
        }

        return Task.FromResult<ProviderResult>(new ProviderResult.Success(aircraft, now));
    }

    private static (double Lat, double Lon, double RadiusKm) Center(MonitoringArea area) => area switch
    {
        CircleArea c => (c.CenterLat, c.CenterLon, c.RadiusKm),
        ConeArea c => (c.CenterLat, c.CenterLon, c.RadiusKm),
        PolygonArea p => (p.Vertices.Average(v => v.Lat), p.Vertices.Average(v => v.Lon), 50.0),
        _ => (0.0, 0.0, 50.0),
    };

    private static AircraftState Synthesise(
        int index,
        double centerLat,
        double centerLon,
        double radiusKm,
        DateTimeOffset now)
    {
        // Each aircraft orbits the centre at its own radius, speed and phase.
        // Coprime-ish multipliers keep them from visually synchronising.
        double orbitFraction = 0.15 + (0.75 * ((index % 7) / 7.0));
        double orbitRadiusKm = radiusKm * orbitFraction;
        double angularSpeed = 0.02 + (0.01 * (index % 5));
        double phase = index * 2.399963;

        double seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        double angle = phase + (seconds * angularSpeed);

        // Small-angle flat-earth offset. Fine at these distances and keeps the
        // mock cheap enough to synthesise a thousand aircraft per poll.
        const double KmPerDegreeLat = 111.32;
        double kmPerDegreeLon = KmPerDegreeLat * Math.Cos(centerLat * Math.PI / 180.0);

        double lat = centerLat + (orbitRadiusKm * Math.Sin(angle) / KmPerDegreeLat);
        double lon = centerLon + (orbitRadiusKm * Math.Cos(angle) / kmPerDegreeLon);

        // Heading is the tangent to the orbit, so symbols point where they move.
        double headingDeg = ((angle * 180.0 / Math.PI) + 90.0) % 360.0;
        if (headingDeg < 0)
        {
            headingDeg += 360.0;
        }

        bool onGround = index % 11 == 0;

        // Every eighth aircraft withholds its callsign, and every ninth its
        // altitude. Missing data is the normal case with real providers, and a
        // mock that always returns complete records hides exactly the rendering
        // bugs worth catching.
        bool withholdCallsign = index % 8 == 3;
        bool withholdAltitude = index % 9 == 4;

        DataQualityFlags flags = DataQualityFlags.None;
        if (withholdCallsign)
        {
            flags |= DataQualityFlags.NoCallsign;
        }

        if (withholdAltitude || onGround)
        {
            flags |= DataQualityFlags.NoAltitude;
        }

        double? altitudeFt = withholdAltitude || onGround
            ? null
            : 1500 + (index % 13 * 2800);

        return new AircraftState
        {
            Provider = "mock",
            IcaoHex = $"{0xa00000 + (index * 4919):x6}",
            Callsign = withholdCallsign ? null : $"MOK{index:D3}",
            Registration = $"N{100 + index}MK",
            AircraftTypeCode = (index % 4) switch
            {
                0 => "B738",
                1 => "A320",
                2 => "C172",
                _ => "E175",
            },
            AircraftCategory = onGround
                ? AircraftCategory.GroundVehicle
                : AircraftCategory.FixedWing,
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            GroundSpeedKt = onGround ? 12 : 180 + (index % 17 * 22),
            TrackHeadingDeg = headingDeg,

            // A spread of climbing, descending and level, plus some with no
            // rate at all so the Unknown vertical trend gets exercised.
            VerticalRateFtPerMin = (index % 5) switch
            {
                0 => 1800,
                1 => -1400,
                2 => 0,
                3 => null,
                _ => 64,
            },
            Squawk = $"{1200 + (index % 6):D4}",

            // One aircraft squawks an emergency so the priority path and the
            // non-colour indicators are always visible in mock mode.
            EmergencyState = index == 5 ? EmergencyState.General : EmergencyState.None,
            OnGround = onGround,
            FirstSeen = now.AddMinutes(-10),
            LastSeen = now,
            PositionTimestamp = now,
            DataQualityFlags = flags,
        };
    }
}
