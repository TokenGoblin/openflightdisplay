namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Quality;
using Xunit;

/// <summary>
/// Parity tests for <see cref="Staleness"/> against
/// <c>firmware/display/test/native/test_staleness/test_staleness.cpp</c>.
/// </summary>
public class StalenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Threshold_is_sixty_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(60), Staleness.StalePositionThreshold);

    [Fact]
    public void Flags_old_position_as_stale()
        => Assert.True(Staleness.IsStale(Now.AddSeconds(-61), Now));

    [Fact]
    public void Does_not_flag_fresh_position()
        => Assert.False(Staleness.IsStale(Now.AddSeconds(-5), Now));

    [Fact]
    public void Boundary_exactly_at_threshold_is_not_stale()
    {
        // Pinned by the firmware suite. Strictly-greater-than, not >=.
        Assert.False(Staleness.IsStale(Now.AddSeconds(-60), Now));
    }

    [Fact]
    public void One_millisecond_past_the_threshold_is_stale()
        => Assert.True(Staleness.IsStale(Now.AddSeconds(-60).AddMilliseconds(-1), Now));

    [Fact]
    public void Age_of_a_future_timestamp_clamps_to_zero()
    {
        // Provider clocks run ahead often enough to matter; "-3s ago" reads as
        // a bug to the user even when the underlying data is fine.
        Assert.Equal(TimeSpan.Zero, Staleness.Age(Now.AddSeconds(10), Now));
    }

    [Fact]
    public void Applying_staleness_sets_the_flag_on_an_old_record()
    {
        AircraftState stale = Staleness.WithStalenessApplied(
            Sample(positionAge: TimeSpan.FromSeconds(120)), Now);

        Assert.True(stale.DataQualityFlags.HasFlag(DataQualityFlags.StalePosition));
    }

    [Fact]
    public void Applying_staleness_clears_a_flag_that_no_longer_applies()
    {
        AircraftState previouslyStale = Sample(positionAge: TimeSpan.FromSeconds(2))
            with
        { DataQualityFlags = DataQualityFlags.StalePosition | DataQualityFlags.NoCallsign };

        AircraftState refreshed = Staleness.WithStalenessApplied(previouslyStale, Now);

        Assert.False(refreshed.DataQualityFlags.HasFlag(DataQualityFlags.StalePosition));

        // Unrelated flags must survive — clearing staleness must not wipe the
        // record's other quality information.
        Assert.True(refreshed.DataQualityFlags.HasFlag(DataQualityFlags.NoCallsign));
    }

    [Fact]
    public void Applying_staleness_returns_the_same_instance_when_nothing_changes()
    {
        AircraftState fresh = Sample(positionAge: TimeSpan.FromSeconds(1));
        Assert.Same(fresh, Staleness.WithStalenessApplied(fresh, Now));
    }

    private static AircraftState Sample(TimeSpan positionAge) => new()
    {
        Provider = "test",
        IcaoHex = "abc123",
        Latitude = 47.6,
        Longitude = -122.3,
        FirstSeen = Now - positionAge,
        LastSeen = Now - positionAge,
        PositionTimestamp = Now - positionAge,
    };
}
