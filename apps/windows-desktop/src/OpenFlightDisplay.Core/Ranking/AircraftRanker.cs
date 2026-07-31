namespace OpenFlightDisplay.Core.Ranking;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Geo;

/// <summary>How to order aircraft for display.</summary>
public enum RankingMode
{
    /// <summary>Nearest great-circle distance. The project's historical default.</summary>
    NearestHorizontal,

    /// <summary>Nearest accounting for altitude — an overhead jet is ~11 km away.</summary>
    NearestSlantRange,

    LowestAltitude,
    HighestAltitude,
    Fastest,

    /// <summary>Most recently observed first.</summary>
    NewestObservation,

    /// <summary>Emergency squawks first, then nearest.</summary>
    EmergencyPriority,
}

/// <summary>
/// Filters aircraft to a monitoring area, enriches them with observer-relative
/// geometry, and orders them.
/// </summary>
/// <remarks>
/// Supersedes <c>rankNearest</c> in <c>services/gateway/src/lib/ranking.ts</c>,
/// which implements only <see cref="RankingMode.NearestHorizontal"/> and is
/// hard-capped at 10 results for the wire protocol. That cap is a
/// <i>protocol</i> bound, not a display bound: the desktop ranks the full set
/// and the caller decides how many to show.
/// </remarks>
public static class AircraftRanker
{
    /// <summary>
    /// Enriches each aircraft with distance, bearing and slant range measured
    /// from the observer.
    /// </summary>
    public static AircraftState WithObserverGeometry(
        AircraftState aircraft,
        double observerLat,
        double observerLon)
    {
        double distanceKm = GeoMath.HaversineDistanceKm(
            observerLat, observerLon, aircraft.Latitude, aircraft.Longitude);

        double bearingDeg = GeoMath.InitialBearingDeg(
            observerLat, observerLon, aircraft.Latitude, aircraft.Longitude);

        // Slant range needs an altitude. Without one it stays null rather than
        // silently collapsing to the horizontal distance, which would make an
        // unknown-altitude aircraft outrank a known one under slant ranking.
        double? slantKm = aircraft.AltitudeFt is { } alt
            ? GeoMath.SlantRangeKm(distanceKm, alt)
            : null;

        return aircraft with
        {
            DistanceFromObserverKm = distanceKm,
            BearingFromObserverDeg = bearingDeg,
            SlantRangeKm = slantKm,
        };
    }

    /// <summary>
    /// Filters to the area, applies observer geometry, and sorts by
    /// <paramref name="mode"/>.
    /// </summary>
    /// <param name="maxResults">
    /// Optional cap. <c>null</c> returns everything — the desktop's flight board
    /// is built to show hundreds of rows.
    /// </param>
    public static IReadOnlyList<AircraftState> Rank(
        IEnumerable<AircraftState> aircraft,
        MonitoringArea area,
        double observerLat,
        double observerLon,
        RankingMode mode = RankingMode.NearestHorizontal,
        int? maxResults = null)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(area);

        IEnumerable<AircraftState> ranked = aircraft
            .Where(a => area.Contains(a.Latitude, a.Longitude, a.AltitudeFt))
            .Select(a => WithObserverGeometry(a, observerLat, observerLon))
            .OrderBy(a => a, Comparer(mode));

        if (maxResults is { } cap)
        {
            ranked = ranked.Take(cap);
        }

        return ranked.ToList();
    }

    private static Comparer<AircraftState> Comparer(RankingMode mode) => mode switch
    {
        RankingMode.NearestHorizontal =>
            Sort.By(a => a.DistanceFromObserverKm),

        // Falls back to horizontal distance when altitude is unknown, so an
        // aircraft missing an altitude still sorts sensibly instead of sinking
        // to the bottom of the list.
        RankingMode.NearestSlantRange =>
            Sort.By(a => a.SlantRangeKm ?? a.DistanceFromObserverKm),

        RankingMode.LowestAltitude => Sort.By(a => a.AltitudeFt),
        RankingMode.HighestAltitude => Sort.ByDescending(a => a.AltitudeFt),
        RankingMode.Fastest => Sort.ByDescending(a => a.GroundSpeedKt),

        RankingMode.NewestObservation =>
            Sort.ByDescending(a => (double)a.PositionTimestamp.ToUnixTimeMilliseconds()),

        RankingMode.EmergencyPriority => Sort.Composite(
            Sort.ByDescending(a => a.EmergencyState == EmergencyState.None ? 0.0 : 1.0),
            Sort.By(a => a.DistanceFromObserverKm)),

        _ => Sort.By(a => a.DistanceFromObserverKm),
    };

    /// <summary>
    /// Comparer helpers with one consistent rule for missing values.
    /// </summary>
    /// <remarks>
    /// <b>Nulls always sort last, in both directions.</b> Ascending by altitude
    /// must not put "altitude unknown" at the top where the user reads it as
    /// "lowest", and descending must not put it there either. A null is not an
    /// extreme value; it is an absent one, and it belongs at the end regardless
    /// of direction.
    /// </remarks>
    private static class Sort
    {
        public static Comparer<AircraftState> By(Func<AircraftState, double?> key)
            => Comparer<AircraftState>.Create((x, y) => CompareNullsLast(key(x), key(y)));

        public static Comparer<AircraftState> ByDescending(Func<AircraftState, double?> key)
            => Comparer<AircraftState>.Create((x, y) => CompareNullsLast(key(y), key(x), descending: true));

        public static Comparer<AircraftState> Composite(params IComparer<AircraftState>[] comparers)
            => Comparer<AircraftState>.Create((x, y) =>
            {
                foreach (IComparer<AircraftState> c in comparers)
                {
                    int result = c.Compare(x, y);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return 0;
            });

        private static int CompareNullsLast(double? a, double? b, bool descending = false)
        {
            // Under descending the arguments arrive pre-swapped, so the
            // null-handling has to be un-swapped to keep nulls at the end.
            if (descending)
            {
                (a, b) = (b, a);
            }

            return (a, b) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (x, y) => descending
                    ? y!.Value.CompareTo(x!.Value)
                    : x!.Value.CompareTo(y!.Value),
            };
        }
    }
}
