namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Ranking;
using Xunit;

/// <summary>
/// Ranking parity with <c>services/gateway/tests/ranking.test.ts</c> and
/// <c>firmware/display/test/native/test_ranking/test_ranking.cpp</c>, plus the
/// desktop-only modes.
/// </summary>
public class AircraftRankerTests
{
    private const double ObserverLat = 47.6062;
    private const double ObserverLon = -122.3321;

    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly CircleArea Area = new(ObserverLat, ObserverLon, RadiusKm: 100.0);

    [Fact]
    public void Orders_by_distance_nearest_first()
    {
        var far = Aircraft("far001", lat: 48.2, lon: -122.3);
        var near = Aircraft("near01", lat: 47.65, lon: -122.33);
        var mid = Aircraft("mid001", lat: 47.9, lon: -122.3);

        var ranked = AircraftRanker.Rank([far, near, mid], Area, ObserverLat, ObserverLon);

        Assert.Equal(["near01", "mid001", "far001"], ranked.Select(a => a.IcaoHex));
    }

    [Fact]
    public void Excludes_aircraft_outside_the_radius()
    {
        var inside = Aircraft("in0001", lat: 47.65, lon: -122.33);
        var outside = Aircraft("out001", lat: 40.0, lon: -122.3);

        var ranked = AircraftRanker.Rank([inside, outside], Area, ObserverLat, ObserverLon);

        Assert.Equal("in0001", Assert.Single(ranked).IcaoHex);
    }

    [Fact]
    public void Fills_in_distance_and_bearing()
    {
        var ranked = AircraftRanker.Rank(
            [Aircraft("abc123", lat: 47.65, lon: -122.33)], Area, ObserverLat, ObserverLon);

        AircraftState only = Assert.Single(ranked);
        Assert.NotNull(only.DistanceFromObserverKm);
        Assert.NotNull(only.BearingFromObserverDeg);
        Assert.InRange(only.BearingFromObserverDeg!.Value, 0.0, 360.0);
    }

    [Fact]
    public void Caps_results_when_a_maximum_is_given()
    {
        var many = Enumerable.Range(0, 25)
            .Select(i => Aircraft($"a{i:d5}", lat: 47.61 + (i * 0.01), lon: -122.33));

        var ranked = AircraftRanker.Rank(
            many, Area, ObserverLat, ObserverLon, maxResults: 10);

        Assert.Equal(10, ranked.Count);
    }

    [Fact]
    public void Returns_everything_when_no_maximum_is_given()
    {
        // The desktop is deliberately not bound by the protocol's 10-aircraft
        // wire cap — the flight board is built to show hundreds of rows.
        var many = Enumerable.Range(0, 25)
            .Select(i => Aircraft($"a{i:d5}", lat: 47.61 + (i * 0.01), lon: -122.33));

        var ranked = AircraftRanker.Rank(many, Area, ObserverLat, ObserverLon);

        Assert.Equal(25, ranked.Count);
    }

    [Fact]
    public void Slant_ranking_prefers_the_lower_of_two_equidistant_aircraft()
    {
        var high = Aircraft("high01", lat: 47.61, lon: -122.3321, altitudeFt: 38000);
        var low = Aircraft("low001", lat: 47.61, lon: -122.3321, altitudeFt: 2000);

        var ranked = AircraftRanker.Rank(
            [high, low], Area, ObserverLat, ObserverLon, RankingMode.NearestSlantRange);

        Assert.Equal("low001", ranked[0].IcaoHex);
    }

    [Fact]
    public void Emergency_priority_puts_a_squawking_aircraft_first_even_when_farther()
    {
        var nearNormal = Aircraft("near01", lat: 47.61, lon: -122.33);
        var farEmergency = Aircraft("emrg01", lat: 48.2, lon: -122.3) with
        {
            EmergencyState = EmergencyState.General,
        };

        var ranked = AircraftRanker.Rank(
            [nearNormal, farEmergency], Area, ObserverLat, ObserverLon, RankingMode.EmergencyPriority);

        Assert.Equal("emrg01", ranked[0].IcaoHex);
    }

    [Fact]
    public void Unknown_altitude_sorts_last_when_ranking_by_lowest()
    {
        var known = Aircraft("known1", lat: 47.61, lon: -122.33, altitudeFt: 10000);
        var unknown = Aircraft("unkwn1", lat: 47.62, lon: -122.33);

        var ranked = AircraftRanker.Rank(
            [unknown, known], Area, ObserverLat, ObserverLon, RankingMode.LowestAltitude);

        // "Altitude unknown" must not read as "lowest".
        Assert.Equal("known1", ranked[0].IcaoHex);
        Assert.Equal("unkwn1", ranked[1].IcaoHex);
    }

    [Fact]
    public void Unknown_altitude_also_sorts_last_when_ranking_by_highest()
    {
        var known = Aircraft("known1", lat: 47.61, lon: -122.33, altitudeFt: 10000);
        var unknown = Aircraft("unkwn1", lat: 47.62, lon: -122.33);

        var ranked = AircraftRanker.Rank(
            [unknown, known], Area, ObserverLat, ObserverLon, RankingMode.HighestAltitude);

        // A null is an absent value, not an extreme one — last in both directions.
        Assert.Equal("known1", ranked[0].IcaoHex);
        Assert.Equal("unkwn1", ranked[1].IcaoHex);
    }

    [Fact]
    public void An_aircraft_with_no_altitude_is_not_excluded_by_an_altitude_band()
    {
        // Missing enrichment must never suppress an otherwise valid aircraft.
        var banded = new CircleArea(ObserverLat, ObserverLon, 100.0)
        {
            MinAltitudeFt = 5000,
            MaxAltitudeFt = 40000,
        };

        var noAltitude = Aircraft("noalt1", lat: 47.61, lon: -122.33);

        var ranked = AircraftRanker.Rank([noAltitude], banded, ObserverLat, ObserverLon);

        Assert.Single(ranked);
    }

    [Fact]
    public void An_aircraft_below_the_altitude_floor_is_excluded()
    {
        var banded = new CircleArea(ObserverLat, ObserverLon, 100.0) { MinAltitudeFt = 5000 };
        var low = Aircraft("low001", lat: 47.61, lon: -122.33, altitudeFt: 1200);

        Assert.Empty(AircraftRanker.Rank([low], banded, ObserverLat, ObserverLon));
    }

    [Fact]
    public void Ranking_an_empty_list_yields_an_empty_list()
        => Assert.Empty(AircraftRanker.Rank([], Area, ObserverLat, ObserverLon));

    private static AircraftState Aircraft(
        string hex,
        double lat,
        double lon,
        double? altitudeFt = null) => new()
        {
            Provider = "test",
            IcaoHex = hex,
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            FirstSeen = Now,
            LastSeen = Now,
            PositionTimestamp = Now,
        };
}
