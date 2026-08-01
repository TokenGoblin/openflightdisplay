namespace OpenFlightDisplay.Persistence.Migrations;

using Microsoft.Data.Sqlite;

/// <summary>
/// Forward-only schema migrations.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than EF Core. The schema is small, the write path is a
/// high-frequency append that benefits from explicit control, and an ORM would
/// be a large dependency for little gain here — see the ADR.
/// </para>
/// <para>
/// Each migration is applied inside a transaction and the version is bumped in
/// the same transaction, so an interrupted upgrade leaves the database at the
/// last fully-applied version rather than half-migrated.
/// </para>
/// </remarks>
public static class SchemaMigrator
{
    /// <summary>Schema version this build expects.</summary>
    public const int CurrentVersion = 1;

    private static readonly string[] Migrations =
    [
        // ---- version 1 ----
        """
        CREATE TABLE observation (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            icao_hex            TEXT    NOT NULL,
            callsign            TEXT    NULL,
            registration        TEXT    NULL,
            aircraft_type       TEXT    NULL,
            provider            TEXT    NOT NULL,
            latitude            REAL    NOT NULL,
            longitude           REAL    NOT NULL,
            altitude_ft         REAL    NULL,
            ground_speed_kt     REAL    NULL,
            track_heading_deg   REAL    NULL,
            vertical_rate_fpm   REAL    NULL,
            squawk              TEXT    NULL,
            emergency_state     INTEGER NOT NULL DEFAULT 0,
            on_ground           INTEGER NOT NULL DEFAULT 0,
            distance_km         REAL    NULL,
            bearing_deg         REAL    NULL,
            data_quality_flags  INTEGER NOT NULL DEFAULT 0,
            observed_at_unix_ms INTEGER NOT NULL
        );

        -- Trails and history views both query "this aircraft, ordered by time",
        -- so the composite index matches the access pattern rather than
        -- indexing the two columns separately.
        CREATE INDEX ix_observation_icao_time
            ON observation (icao_hex, observed_at_unix_ms);

        -- Pruning and "what was in the air at time T" scan by time alone.
        CREATE INDEX ix_observation_time
            ON observation (observed_at_unix_ms);
        """,
    ];

    /// <summary>
    /// Brings the database up to <see cref="CurrentVersion"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The database was written by a newer build. Refused rather than
    /// best-effort migrated downwards, which could silently drop columns.
    /// </exception>
    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int version = ReadVersion(connection);

        if (version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The history database is at schema version {version}, but this build " +
                $"understands version {CurrentVersion}. Upgrade the application, or move " +
                "the database aside to start a new one.");
        }

        for (int next = version; next < CurrentVersion; next++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[next];
                command.ExecuteNonQuery();
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                // PRAGMA user_version does not accept a parameter, and the value
                // is an int from a constant-bounded loop, never user input.
                command.CommandText = $"PRAGMA user_version = {next + 1};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Reads the database's schema version.</summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
