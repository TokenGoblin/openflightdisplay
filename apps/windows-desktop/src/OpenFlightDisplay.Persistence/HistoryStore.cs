namespace OpenFlightDisplay.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Persistence.Migrations;

/// <summary>Retention policy for recorded observations.</summary>
/// <param name="MaxAge">Observations older than this are pruned.</param>
/// <param name="MaxDatabaseBytes">
/// Soft ceiling. When exceeded, the oldest observations are pruned until the
/// database fits, regardless of <paramref name="MaxAge"/>.
/// </param>
public readonly record struct RetentionPolicy(TimeSpan MaxAge, long MaxDatabaseBytes)
{
    /// <summary>30 days, 512 MB — the defaults in <c>AppSettings</c>.</summary>
    public static RetentionPolicy Default { get; } =
        new(TimeSpan.FromDays(30), 512L * 1024 * 1024);
}

/// <summary>
/// Local SQLite store for aircraft observations.
/// </summary>
/// <remarks>
/// <para>
/// <b>History is opt-in.</b> Nothing here runs unless the user enabled it during
/// onboarding or in settings — it turns a live display into a record of
/// everything that flew over someone's house, so it is not on by default and it
/// never leaves the machine unless exported deliberately.
/// </para>
/// <para>
/// Writes are batched per poll inside one transaction. A poll can carry a
/// thousand aircraft, and a thousand individual inserts with a thousand implicit
/// transactions is the difference between a few milliseconds and several
/// seconds of disk work.
/// </para>
/// </remarks>
public sealed partial class HistoryStore : IDisposable
{
    /// <summary>
    /// Floor the size sweep will not prune below.
    /// </summary>
    /// <remarks>
    /// Guarantees a misconfigured size limit cannot wipe the history. The age
    /// rule has no such floor — an explicit "keep 30 days" genuinely means the
    /// 31st day should go, whereas a byte ceiling is an indirect setting whose
    /// consequences are much harder for a user to predict.
    /// </remarks>
    public const int MinimumRetainedObservations = 500;

    private readonly SqliteConnection _connection;
    private readonly ILogger<HistoryStore> _logger;

    private HistoryStore(SqliteConnection connection, ILogger<HistoryStore> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <summary>Opens (creating if needed) and migrates a history database.</summary>
    /// <param name="databasePath">
    /// File path, or <c>":memory:"</c> for a transient database in tests.
    /// </param>
    public static HistoryStore Open(string databasePath, ILogger<HistoryStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(logger);

        if (databasePath != ":memory:")
        {
            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Shared cache keeps an in-memory database alive for the lifetime of
            // the connection, which is what makes ":memory:" usable in tests.
            Cache = databasePath == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
        }.ToString());

        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            // WAL: readers (history views, trails) do not block the writer (the
            // poll loop). NORMAL synchronous is the usual WAL pairing - a crash
            // can lose the last commits, which for observation history is an
            // acceptable trade against fsync on every poll.
            pragma.CommandText =
                "PRAGMA journal_mode = WAL;" +
                "PRAGMA synchronous = NORMAL;" +
                "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        SchemaMigrator.Migrate(connection);
        return new HistoryStore(connection, logger);
    }

    /// <summary>Schema version currently on disk.</summary>
    public int SchemaVersion => SchemaMigrator.ReadVersion(_connection);

    /// <summary>Total observations stored.</summary>
    public long ObservationCount
    {
        get
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM observation;";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Approximate on-disk size in bytes.</summary>
    public long DatabaseBytes
    {
        get
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            long pages = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            command.CommandText = "PRAGMA page_size;";
            long pageSize = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            return pages * pageSize;
        }
    }

    /// <summary>Records a batch of observations in a single transaction.</summary>
    /// <returns>How many rows were written.</returns>
    public int RecordBatch(IEnumerable<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO observation (
                icao_hex, callsign, registration, aircraft_type, provider,
                latitude, longitude, altitude_ft, ground_speed_kt,
                track_heading_deg, vertical_rate_fpm, squawk, emergency_state,
                on_ground, distance_km, bearing_deg, data_quality_flags,
                observed_at_unix_ms)
            VALUES (
                $hex, $callsign, $registration, $type, $provider,
                $lat, $lon, $alt, $gs,
                $track, $vrate, $squawk, $emergency,
                $onGround, $distance, $bearing, $flags,
                $observedAt);
            """;

        // Parameters are created once and their values swapped per row, so the
        // statement is prepared a single time for the whole batch.
        SqliteParameter hex = command.Parameters.Add("$hex", SqliteType.Text);
        SqliteParameter callsign = command.Parameters.Add("$callsign", SqliteType.Text);
        SqliteParameter registration = command.Parameters.Add("$registration", SqliteType.Text);
        SqliteParameter type = command.Parameters.Add("$type", SqliteType.Text);
        SqliteParameter provider = command.Parameters.Add("$provider", SqliteType.Text);
        SqliteParameter lat = command.Parameters.Add("$lat", SqliteType.Real);
        SqliteParameter lon = command.Parameters.Add("$lon", SqliteType.Real);
        SqliteParameter alt = command.Parameters.Add("$alt", SqliteType.Real);
        SqliteParameter gs = command.Parameters.Add("$gs", SqliteType.Real);
        SqliteParameter track = command.Parameters.Add("$track", SqliteType.Real);
        SqliteParameter vrate = command.Parameters.Add("$vrate", SqliteType.Real);
        SqliteParameter squawk = command.Parameters.Add("$squawk", SqliteType.Text);
        SqliteParameter emergency = command.Parameters.Add("$emergency", SqliteType.Integer);
        SqliteParameter onGround = command.Parameters.Add("$onGround", SqliteType.Integer);
        SqliteParameter distance = command.Parameters.Add("$distance", SqliteType.Real);
        SqliteParameter bearing = command.Parameters.Add("$bearing", SqliteType.Real);
        SqliteParameter flags = command.Parameters.Add("$flags", SqliteType.Integer);
        SqliteParameter observedAt = command.Parameters.Add("$observedAt", SqliteType.Integer);

        int written = 0;
        foreach (AircraftState a in aircraft)
        {
            hex.Value = a.IcaoHex;
            callsign.Value = (object?)a.Callsign ?? DBNull.Value;
            registration.Value = (object?)a.Registration ?? DBNull.Value;
            type.Value = (object?)a.AircraftTypeCode ?? DBNull.Value;
            provider.Value = a.Provider;
            lat.Value = a.Latitude;
            lon.Value = a.Longitude;

            // Nullable measurements are stored as SQL NULL, never as 0. The
            // distinction between "not reported" and "zero" survives a round
            // trip through the database or the whole model is undermined.
            alt.Value = (object?)a.AltitudeFt ?? DBNull.Value;
            gs.Value = (object?)a.GroundSpeedKt ?? DBNull.Value;
            track.Value = (object?)a.TrackHeadingDeg ?? DBNull.Value;
            vrate.Value = (object?)a.VerticalRateFtPerMin ?? DBNull.Value;
            squawk.Value = (object?)a.Squawk ?? DBNull.Value;
            emergency.Value = (int)a.EmergencyState;
            onGround.Value = a.OnGround ? 1 : 0;
            distance.Value = (object?)a.DistanceFromObserverKm ?? DBNull.Value;
            bearing.Value = (object?)a.BearingFromObserverDeg ?? DBNull.Value;
            flags.Value = (int)a.DataQualityFlags;
            observedAt.Value = a.PositionTimestamp.ToUnixTimeMilliseconds();

            command.ExecuteNonQuery();
            written++;
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// Returns one aircraft's positions in time order — its trail.
    /// </summary>
    public IReadOnlyList<TrailPoint> ReadTrail(
        string icaoHex,
        DateTimeOffset since,
        int maxPoints = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icaoHex);

        using SqliteCommand command = _connection.CreateCommand();

        // Newest-first with a LIMIT so a long-lived aircraft cannot return an
        // unbounded trail; the order is flipped afterwards for drawing.
        command.CommandText =
            """
            SELECT latitude, longitude, altitude_ft, observed_at_unix_ms
            FROM observation
            WHERE icao_hex = $hex AND observed_at_unix_ms >= $since
            ORDER BY observed_at_unix_ms DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$hex", icaoHex);
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", maxPoints);

        var points = new List<TrailPoint>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new TrailPoint(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }

        points.Reverse();
        return points;
    }

    /// <summary>Distinct aircraft observed in a period, most-seen first.</summary>
    public IReadOnlyList<AircraftSummary> ReadMostObserved(
        DateTimeOffset since,
        int limit = 25)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT icao_hex,
                   MAX(callsign)  AS callsign,
                   COUNT(*)       AS observations,
                   MIN(observed_at_unix_ms) AS first_seen,
                   MAX(observed_at_unix_ms) AS last_seen
            FROM observation
            WHERE observed_at_unix_ms >= $since
            GROUP BY icao_hex
            ORDER BY observations DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AircraftSummary>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AircraftSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }

        return results;
    }

    /// <summary>
    /// Applies the retention policy: age first, then size.
    /// </summary>
    /// <returns>Rows deleted.</returns>
    /// <remarks>
    /// Age is applied before size so the cheap, predictable rule runs first and
    /// the size sweep usually has nothing left to do. The size sweep deletes in
    /// batches from the oldest end rather than computing an exact row count,
    /// because SQLite page usage does not map linearly to rows.
    /// </remarks>
    public int Prune(RetentionPolicy policy, DateTimeOffset now)
    {
        int deleted = DeleteOlderThan(now - policy.MaxAge);

        // VACUUM is what actually returns pages to the filesystem; without it
        // DatabaseBytes never falls and the size rule below never converges.
        if (deleted > 0)
        {
            Vacuum();
        }

        // Size sweep, with a floor. An over-tight limit - one below what the
        // schema and indexes cost on their own - must not be allowed to delete
        // every observation chasing a size it can never reach. Exceeding the
        // limit and saying so is better than silently destroying the user's
        // history because of a misconfigured number.
        int guard = 0;
        while (DatabaseBytes > policy.MaxDatabaseBytes
               && ObservationCount > MinimumRetainedObservations)
        {
            if (++guard > 64)
            {
                LogPruneGaveUp(_logger, DatabaseBytes, policy.MaxDatabaseBytes);
                break;
            }

            long before = DatabaseBytes;

            // Clamped so the batch can never step past the floor. Deleting a
            // full batch out of a nearly-empty table would overshoot straight
            // to zero, which is the exact outcome the floor exists to prevent.
            int batch = (int)Math.Min(1000, ObservationCount - MinimumRetainedObservations);
            if (batch <= 0)
            {
                break;
            }

            int removed = DeleteOldestBatch(batch);
            Vacuum();

            if (removed == 0 || DatabaseBytes >= before)
            {
                deleted += removed;
                LogPruneNoProgress(_logger, DatabaseBytes, policy.MaxDatabaseBytes);
                break;
            }

            deleted += removed;
        }

        if (DatabaseBytes > policy.MaxDatabaseBytes)
        {
            LogPruneNoProgress(_logger, DatabaseBytes, policy.MaxDatabaseBytes);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes every recorded observation.
    /// </summary>
    /// <returns>Rows deleted.</returns>
    /// <remarks>
    /// <para>
    /// History is opt-in precisely because it turns a live display into a record
    /// of everything that flew over someone's house. A feature that can be
    /// turned on for that reason has to be erasable for the same reason —
    /// switching recording off only stops new rows, it does not remove the ones
    /// already there.
    /// </para>
    /// <para>
    /// <b>Vacuums afterwards.</b> A DELETE alone leaves the rows in free pages
    /// that are still on disk and still recoverable, and leaves the file the
    /// same size — so the user would be told their history was deleted while the
    /// file sat there unchanged.
    /// </para>
    /// </remarks>
    public int DeleteAll()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM observation;";
        int deleted = command.ExecuteNonQuery();

        Vacuum();

        LogDeletedAll(_logger, deleted);
        return deleted;
    }

    private int DeleteOlderThan(DateTimeOffset cutoff)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM observation WHERE observed_at_unix_ms < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

    private int DeleteOldestBatch(int batchSize)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM observation
            WHERE id IN (
                SELECT id FROM observation
                ORDER BY observed_at_unix_ms ASC
                LIMIT $batch);
            """;

        command.Parameters.AddWithValue("$batch", batchSize);
        return command.ExecuteNonQuery();
    }

    private void Vacuum()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Warning,
        Message = "Pruning hit its iteration guard at {ActualBytes} bytes, above the {LimitBytes} byte limit")]
    private static partial void LogPruneGaveUp(ILogger logger, long actualBytes, long limitBytes);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Pruning stopped making progress at {ActualBytes} bytes; the {LimitBytes} byte limit is below the database's fixed overhead and cannot be met")]
    private static partial void LogPruneNoProgress(ILogger logger, long actualBytes, long limitBytes);

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Deleted all {Deleted} recorded observations at the user's request")]
    private static partial void LogDeletedAll(ILogger logger, int deleted);
}

/// <summary>One recorded position on an aircraft's trail.</summary>
public readonly record struct TrailPoint(
    double Latitude,
    double Longitude,
    double? AltitudeFt,
    DateTimeOffset ObservedAt);

/// <summary>Aggregate view of one aircraft over a period.</summary>
public readonly record struct AircraftSummary(
    string IcaoHex,
    string? Callsign,
    int Observations,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
