namespace OpenFlightDisplay.Persistence.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Persistence;
using OpenFlightDisplay.Persistence.Migrations;
using Xunit;

/// <summary>
/// Persistence behaviour: migrations, round-tripping, trails, retention.
/// </summary>
/// <remarks>
/// Uses real on-disk databases rather than <c>:memory:</c> for anything
/// involving size or VACUUM, because page accounting is what the retention
/// rules act on and an in-memory database does not exercise it.
/// </remarks>
public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;

    public HistoryStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"ofd-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    // ---- migrations ----

    [Fact]
    public void A_new_database_is_migrated_to_the_current_schema()
    {
        using HistoryStore store = Open();
        Assert.Equal(SchemaMigrator.CurrentVersion, store.SchemaVersion);
    }

    [Fact]
    public void Reopening_an_existing_database_does_not_re_run_migrations()
    {
        string path = Path.Combine(_directory, "history.db");

        using (HistoryStore first = Open(path))
        {
            first.RecordBatch([Aircraft("abc123", Now)]);
        }

        // A second Migrate() pass over an already-migrated database would throw
        // on CREATE TABLE, so surviving this proves migrations are idempotent.
        using HistoryStore second = Open(path);

        Assert.Equal(SchemaMigrator.CurrentVersion, second.SchemaVersion);
        Assert.Equal(1, second.ObservationCount);
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused_rather_than_downgraded()
    {
        string path = Path.Combine(_directory, "future.db");

        using (HistoryStore store = Open(path))
        {
            // Migrated normally, then pushed to a version this build cannot know.
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }

        // Best-effort migrating downwards could silently drop columns.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Open(path));
        Assert.Contains("999", ex.Message, StringComparison.Ordinal);
    }

    // ---- round tripping ----

    [Fact]
    public void Records_and_counts_a_batch()
    {
        using HistoryStore store = Open();

        int written = store.RecordBatch([
            Aircraft("aaa001", Now),
            Aircraft("bbb002", Now),
            Aircraft("ccc003", Now),
        ]);

        Assert.Equal(3, written);
        Assert.Equal(3, store.ObservationCount);
    }

    [Fact]
    public void Recording_an_empty_batch_writes_nothing_and_does_not_throw()
    {
        using HistoryStore store = Open();

        Assert.Equal(0, store.RecordBatch([]));
        Assert.Equal(0, store.ObservationCount);
    }

    [Fact]
    public void A_missing_measurement_round_trips_as_null_not_zero()
    {
        // The distinction between "not reported" and "zero" has to survive the
        // database, or every downstream rule built on it is undermined.
        using HistoryStore store = Open();

        store.RecordBatch([Aircraft("aaa001", Now) with
        {
            GeometricAltitudeFt = null,
            BarometricAltitudeFt = null,
        }]);

        TrailPoint point = Assert.Single(store.ReadTrail("aaa001", Now.AddHours(-1)));
        Assert.Null(point.AltitudeFt);
    }

    [Fact]
    public void A_zero_measurement_round_trips_as_zero()
    {
        using HistoryStore store = Open();

        store.RecordBatch([Aircraft("aaa001", Now) with { GeometricAltitudeFt = 0.0 }]);

        TrailPoint point = Assert.Single(store.ReadTrail("aaa001", Now.AddHours(-1)));
        Assert.Equal(0.0, point.AltitudeFt);
    }

    // ---- trails ----

    [Fact]
    public void A_trail_is_returned_oldest_first()
    {
        using HistoryStore store = Open();

        store.RecordBatch([
            Aircraft("aaa001", Now.AddMinutes(-3)),
            Aircraft("aaa001", Now.AddMinutes(-1)),
            Aircraft("aaa001", Now.AddMinutes(-2)),
        ]);

        var trail = store.ReadTrail("aaa001", Now.AddHours(-1));

        Assert.Equal(3, trail.Count);
        Assert.True(trail[0].ObservedAt < trail[1].ObservedAt);
        Assert.True(trail[1].ObservedAt < trail[2].ObservedAt);
    }

    [Fact]
    public void A_trail_only_includes_the_requested_aircraft()
    {
        using HistoryStore store = Open();

        store.RecordBatch([Aircraft("aaa001", Now), Aircraft("bbb002", Now)]);

        Assert.Single(store.ReadTrail("aaa001", Now.AddHours(-1)));
    }

    [Fact]
    public void A_trail_excludes_points_before_the_requested_time()
    {
        using HistoryStore store = Open();

        store.RecordBatch([
            Aircraft("aaa001", Now.AddHours(-5)),
            Aircraft("aaa001", Now.AddMinutes(-1)),
        ]);

        Assert.Single(store.ReadTrail("aaa001", Now.AddMinutes(-10)));
    }

    [Fact]
    public void A_trail_is_capped_and_keeps_the_most_recent_points()
    {
        // An unbounded trail on a long-lived aircraft is a memory problem, and
        // keeping the oldest points would be the wrong half to keep.
        using HistoryStore store = Open();

        store.RecordBatch(Enumerable.Range(0, 50)
            .Select(i => Aircraft("aaa001", Now.AddMinutes(-i))));

        var trail = store.ReadTrail("aaa001", Now.AddHours(-2), maxPoints: 10);

        Assert.Equal(10, trail.Count);
        Assert.Equal(Now.AddMinutes(-9), trail[0].ObservedAt);
        Assert.Equal(Now, trail[^1].ObservedAt);
    }

    // ---- summaries ----

    [Fact]
    public void Most_observed_ranks_by_observation_count()
    {
        using HistoryStore store = Open();

        store.RecordBatch(Enumerable.Range(0, 5).Select(i => Aircraft("busy01", Now.AddMinutes(-i))));
        store.RecordBatch([Aircraft("rare01", Now)]);

        var summaries = store.ReadMostObserved(Now.AddHours(-1));

        Assert.Equal("busy01", summaries[0].IcaoHex);
        Assert.Equal(5, summaries[0].Observations);
        Assert.Equal("rare01", summaries[1].IcaoHex);
    }

    // ---- retention ----

    [Fact]
    public void Pruning_removes_observations_older_than_the_age_limit()
    {
        using HistoryStore store = Open();

        store.RecordBatch([
            Aircraft("old001", Now.AddDays(-40)),
            Aircraft("new001", Now.AddHours(-1)),
        ]);

        int deleted = store.Prune(
            new RetentionPolicy(TimeSpan.FromDays(30), long.MaxValue), Now);

        Assert.Equal(1, deleted);
        Assert.Equal(1, store.ObservationCount);
        Assert.Empty(store.ReadTrail("old001", DateTimeOffset.UnixEpoch));
        Assert.Single(store.ReadTrail("new001", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Pruning_keeps_an_observation_exactly_at_the_age_boundary()
    {
        using HistoryStore store = Open();
        store.RecordBatch([Aircraft("edge01", Now.AddDays(-30))]);

        store.Prune(new RetentionPolicy(TimeSpan.FromDays(30), long.MaxValue), Now);

        Assert.Equal(1, store.ObservationCount);
    }

    [Fact]
    public void Pruning_an_empty_database_is_a_no_op()
    {
        using HistoryStore store = Open();
        Assert.Equal(0, store.Prune(RetentionPolicy.Default, Now));
    }

    [Fact]
    public void Pruning_enforces_the_size_limit_by_dropping_the_oldest_first()
    {
        using HistoryStore store = Open(Path.Combine(_directory, "big.db"));

        store.RecordBatch(Enumerable.Range(0, 4000)
            .Select(i => Aircraft($"a{i:d5}", Now.AddSeconds(-i))));

        long before = store.DatabaseBytes;

        // Target half the current size: comfortably reachable, so the sweep has
        // to actually converge rather than hit the no-progress guard.
        long target = before / 2;

        // Age limit generous, size limit binding.
        store.Prune(new RetentionPolicy(TimeSpan.FromDays(3650), target), Now);

        Assert.True(store.ObservationCount < 4000, "size pruning should have removed rows");
        Assert.True(store.ObservationCount > 0, "size pruning should not have emptied the database");
        Assert.True(store.DatabaseBytes <= before, "the database should not have grown");

        // Aircraft are stamped Now.AddSeconds(-i), so a03999 is the OLDEST and
        // a00000 the newest. Oldest goes first; newest survives.
        Assert.Empty(store.ReadTrail("a03999", DateTimeOffset.UnixEpoch));
        Assert.Single(store.ReadTrail("a00000", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Pruning_does_not_empty_the_database_chasing_an_unreachable_size()
    {
        // A limit below the schema's own fixed overhead can never be met.
        // Deleting every observation in pursuit of it would lose all history to
        // a misconfiguration, which is worse than exceeding the limit.
        using HistoryStore store = Open(Path.Combine(_directory, "tiny-limit.db"));
        store.RecordBatch(Enumerable.Range(0, 3000).Select(i => Aircraft($"a{i:d5}", Now)));

        store.Prune(new RetentionPolicy(TimeSpan.FromDays(3650), MaxDatabaseBytes: 1), Now);

        Assert.Equal(
            HistoryStore.MinimumRetainedObservations,
            (int)store.ObservationCount);
    }

    [Fact]
    public void The_age_rule_has_no_floor_and_can_empty_the_database()
    {
        // Deliberately different from the size rule. "Keep 30 days" is explicit
        // and means the 31st day should go, even if that is everything; a byte
        // ceiling is indirect and its consequences are far harder to predict.
        using HistoryStore store = Open(Path.Combine(_directory, "all-old.db"));
        store.RecordBatch(Enumerable.Range(0, 100).Select(i => Aircraft($"a{i:d5}", Now.AddDays(-90))));

        store.Prune(new RetentionPolicy(TimeSpan.FromDays(30), long.MaxValue), Now);

        Assert.Equal(0, store.ObservationCount);
    }

    [Fact]
    public void Pruning_terminates_when_the_size_limit_can_never_be_met()
    {
        // A ceiling below an empty database's own size must not spin forever.
        using HistoryStore store = Open(Path.Combine(_directory, "impossible.db"));
        store.RecordBatch(Enumerable.Range(0, 200).Select(i => Aircraft($"a{i:d5}", Now)));

        store.Prune(new RetentionPolicy(TimeSpan.FromDays(3650), MaxDatabaseBytes: 1), Now);

        // Terminating at all is the assertion; the guard bounds the loop.
        Assert.True(store.ObservationCount >= 0);
    }

    [Fact]
    public void Data_survives_closing_and_reopening_the_database()
    {
        string path = Path.Combine(_directory, "durable.db");

        using (HistoryStore store = Open(path))
        {
            store.RecordBatch([Aircraft("aaa001", Now)]);
        }

        using HistoryStore reopened = Open(path);
        Assert.Equal(1, reopened.ObservationCount);
    }

    private HistoryStore Open(string? path = null)
        => HistoryStore.Open(
            path ?? Path.Combine(_directory, "history.db"),
            NullLogger<HistoryStore>.Instance);

    private static AircraftState Aircraft(string hex, DateTimeOffset observedAt) => new()
    {
        Provider = "test",
        IcaoHex = hex,
        Callsign = $"CS{hex[..3].ToUpperInvariant()}",
        Latitude = 47.6 + (hex.GetHashCode(StringComparison.Ordinal) % 100 * 0.0001),
        Longitude = -122.3,
        GeometricAltitudeFt = 30000,
        GroundSpeedKt = 420,
        FirstSeen = observedAt,
        LastSeen = observedAt,
        PositionTimestamp = observedAt,
    };
}
