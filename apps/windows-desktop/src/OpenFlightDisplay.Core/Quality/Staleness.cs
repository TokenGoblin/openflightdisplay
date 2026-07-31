namespace OpenFlightDisplay.Core.Quality;

using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Position age policy. Mirrors <c>STALE_POSITION_THRESHOLD_MS</c> in
/// <c>services/gateway/src/lib/ranking.ts</c> and
/// <c>firmware/display/src/domain/staleness.cpp</c>.
/// </summary>
public static class Staleness
{
    /// <summary>
    /// Positions older than this are flagged stale rather than presented as live.
    /// </summary>
    public static readonly TimeSpan StalePositionThreshold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True when the position is older than <see cref="StalePositionThreshold"/>.
    /// </summary>
    /// <remarks>
    /// <b>Strictly greater than.</b> A position exactly at the threshold is
    /// <i>not</i> stale — the firmware suite pins this
    /// (<c>test_boundary_exactly_at_threshold_is_not_stale</c>) and the C#
    /// implementation matches it.
    /// </remarks>
    public static bool IsStale(DateTimeOffset positionTimestamp, DateTimeOffset now)
        => now - positionTimestamp > StalePositionThreshold;

    /// <inheritdoc cref="IsStale(DateTimeOffset, DateTimeOffset)"/>
    public static bool IsStale(AircraftState aircraft, DateTimeOffset now)
        => IsStale(aircraft.PositionTimestamp, now);

    /// <summary>Age of the position at <paramref name="now"/>, never negative.</summary>
    /// <remarks>
    /// Clamped at zero because provider clocks run ahead of local ones often
    /// enough to matter, and a negative age rendered as "-3s ago" reads as a bug
    /// to the user even when the data is fine.
    /// </remarks>
    public static TimeSpan Age(DateTimeOffset positionTimestamp, DateTimeOffset now)
    {
        TimeSpan age = now - positionTimestamp;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>
    /// Returns the aircraft with <see cref="DataQualityFlags.StalePosition"/> set
    /// or cleared to match its actual age.
    /// </summary>
    public static AircraftState WithStalenessApplied(AircraftState aircraft, DateTimeOffset now)
    {
        bool stale = IsStale(aircraft, now);
        DataQualityFlags flags = stale
            ? aircraft.DataQualityFlags | DataQualityFlags.StalePosition
            : aircraft.DataQualityFlags & ~DataQualityFlags.StalePosition;

        return flags == aircraft.DataQualityFlags
            ? aircraft
            : aircraft with { DataQualityFlags = flags };
    }
}
