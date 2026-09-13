using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// The SQLite layer. It is ONLY a store: the rollup arithmetic and the retention decisions
/// live in <see cref="RollupMath"/> and <see cref="RetentionPolicy"/>, where they can be
/// tested without touching a file.
/// </summary>
/// <remarks>
/// <para>
/// Every method opens and closes its own connection. With Microsoft.Data.Sqlite's pool opening
/// costs almost nothing, and in exchange there is no shared state to synchronise between the
/// service that writes and the HTTP requests that read.
/// </para>
/// <para>
/// The journal is in WAL mode: readers do not wait for the writer, which is exactly the
/// requirement "HTTP responses must not slow down while writing".
/// </para>
/// </remarks>
public sealed class MetricStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS series (
            series_id    INTEGER PRIMARY KEY,
            collector_id TEXT    NOT NULL,
            metric_id    TEXT    NOT NULL,
            instance     TEXT    NOT NULL,
            value_kind   INTEGER NOT NULL
        );

        -- A series' identity. instance is NOT NULL and holds the empty string when the
        -- metric is unique per machine: in a SQLite UNIQUE index two NULLs are NOT equal,
        -- so with NULL the same series would be re-inserted every second.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_series_identity
            ON series (collector_id, metric_id, instance);

        -- WITHOUT ROWID: the row is almost all key, and this way neither the hidden rowid
        -- nor the separate index on the primary key exists. On a table that grows by one
        -- row a second per series that is the difference between a file and a problem.
        CREATE TABLE IF NOT EXISTS sample_raw (
            series_id   INTEGER NOT NULL REFERENCES series (series_id),
            captured_at INTEGER NOT NULL,
            value       REAL    NOT NULL,
            PRIMARY KEY (series_id, captured_at)
        ) WITHOUT ROWID;

        -- Needed by the deletion, which filters by time across ALL the series: without it,
        -- every maintenance pass would scan the whole table.
        CREATE INDEX IF NOT EXISTS ix_raw_time ON sample_raw (captured_at);

        -- A single table for every level, with the width as a column: adding a third level
        -- will be one line of configuration, not a schema migration.
        -- value_sum and sample_count instead of the average: recombining buckets with a
        -- different number of samples, the average of averages is a credible, false number.
        CREATE TABLE IF NOT EXISTS sample_rollup (
            series_id      INTEGER NOT NULL REFERENCES series (series_id),
            bucket_seconds INTEGER NOT NULL,
            bucket_start   INTEGER NOT NULL,
            sample_count   INTEGER NOT NULL,
            value_sum      REAL    NOT NULL,
            value_min      REAL    NOT NULL,
            value_max      REAL    NOT NULL,
            value_last     REAL    NOT NULL,
            PRIMARY KEY (series_id, bucket_seconds, bucket_start)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_rollup_time
            ON sample_rollup (bucket_seconds, bucket_start);

        -- Each level's marker: how far it has already aggregated. It is what lets a
        -- maintenance pass read only what is new instead of rescanning everything, and it
        -- is also the constraint that prevents deleting data not yet summarised.
        CREATE TABLE IF NOT EXISTS rollup_state (
            bucket_seconds       INTEGER PRIMARY KEY,
            consolidated_through INTEGER NOT NULL
        ) WITHOUT ROWID;
        """;

    private const string InsertSeriesSql = """
        INSERT INTO series (collector_id, metric_id, instance, value_kind)
        VALUES ($collector, $metric, $instance, $kind)
        ON CONFLICT (collector_id, metric_id, instance) DO NOTHING;
        """;

    private const string SelectSeriesIdSql = """
        SELECT series_id FROM series
        WHERE collector_id = $collector AND metric_id = $metric AND instance = $instance;
        """;

    private const string UpsertSampleSql = """
        INSERT INTO sample_raw (series_id, captured_at, value)
        VALUES ($series, $captured, $value)
        ON CONFLICT (series_id, captured_at) DO UPDATE SET value = excluded.value;
        """;

    private const string UpsertRollupSql = """
        INSERT INTO sample_rollup (
            series_id, bucket_seconds, bucket_start,
            sample_count, value_sum, value_min, value_max, value_last)
        VALUES ($series, $width, $start, $count, $sum, $min, $max, $last)
        ON CONFLICT (series_id, bucket_seconds, bucket_start) DO UPDATE SET
            sample_count = excluded.sample_count,
            value_sum    = excluded.value_sum,
            value_min    = excluded.value_min,
            value_max    = excluded.value_max,
            value_last   = excluded.value_last;
        """;

    private const string SelectRawWindowSql = """
        SELECT series_id, captured_at, value FROM sample_raw
        WHERE captured_at >= $from AND captured_at < $to
        ORDER BY series_id, captured_at;
        """;

    private const string SelectRollupWindowSql = """
        SELECT series_id, bucket_start, sample_count, value_sum, value_min, value_max, value_last
        FROM sample_rollup
        WHERE bucket_seconds = $width AND bucket_start >= $from AND bucket_start < $to
        ORDER BY series_id, bucket_start;
        """;

    private const string SelectRawHistorySql = """
        SELECT r.captured_at, r.value
        FROM sample_raw r
        JOIN series s ON s.series_id = r.series_id
        WHERE s.collector_id = $collector AND s.metric_id = $metric AND s.instance = $instance
          AND r.captured_at >= $from AND r.captured_at < $to
        ORDER BY r.captured_at DESC
        LIMIT $limit;
        """;

    private const string SelectRollupHistorySql = """
        SELECT b.bucket_start, b.sample_count, b.value_sum, b.value_min, b.value_max, b.value_last
        FROM sample_rollup b
        JOIN series s ON s.series_id = b.series_id
        WHERE s.collector_id = $collector AND s.metric_id = $metric AND s.instance = $instance
          AND b.bucket_seconds = $width AND b.bucket_start >= $from AND b.bucket_start < $to
        ORDER BY b.bucket_start DESC
        LIMIT $limit;
        """;

    private const string SelectStatsSql = """
        SELECT
            (SELECT COUNT(*) FROM series),
            (SELECT COUNT(*) FROM sample_raw),
            (SELECT COUNT(*) FROM sample_rollup WHERE bucket_seconds = 60),
            (SELECT COUNT(*) FROM sample_rollup WHERE bucket_seconds = 300);
        """;

    private const string UpsertRollupStateSql = """
        INSERT INTO rollup_state (bucket_seconds, consolidated_through)
        VALUES ($width, $through)
        ON CONFLICT (bucket_seconds) DO UPDATE SET consolidated_through = excluded.consolidated_through;
        """;

    /// <summary>The three files SQLite uses in WAL mode.</summary>
    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm"];

    /// <summary>
    /// The series already seen. Avoids two queries a second for every metric: the series are
    /// a few dozen and never disappear, so the cache cannot age badly.
    /// </summary>
    private readonly ConcurrentDictionary<SeriesKey, long> seriesIds = new();

    private readonly string connectionString;

    /// <summary>Creates the store on the given file. Opens nothing until it is needed.</summary>
    /// <param name="databasePath">Path of the SQLite file.</param>
    public MetricStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,

            // Microsoft.Data.Sqlite turns this timeout into a wait on SQLITE_BUSY. It is
            // needed because writer and readers are different logical processes on the same
            // file: without it, a read during a commit would fail instead of waiting.
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Absolute path of the file.</summary>
    public string DatabasePath { get; }

    /// <summary>Creates the schema if it is missing. Idempotent.</summary>
    public void Initialize()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        // WAL stays written in the file and holds for ever; synchronous, instead, is per
        // connection and has to be set again at every open (see Open).
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();

        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    /// <summary>Writes a batch of raw samples in a single transaction.</summary>
    /// <param name="samples">The samples to write.</param>
    /// <returns>How many raw rows were written.</returns>
    public int WriteSamples(IReadOnlyList<SeriesSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return 0;
        }

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand insertSample = connection.CreateCommand();

        insertSample.Transaction = transaction;
        insertSample.CommandText = UpsertSampleSql;

        SqliteParameter seriesParam = insertSample.Parameters.Add("$series", SqliteType.Integer);
        SqliteParameter capturedParam = insertSample.Parameters.Add("$captured", SqliteType.Integer);
        SqliteParameter valueParam = insertSample.Parameters.Add("$value", SqliteType.Real);

        // The identifiers resolved now enter the shared cache ONLY after the commit: if the
        // transaction failed, the cache would otherwise be left full of identifiers of rows
        // that do not exist.
        Dictionary<SeriesKey, long> resolvedNow = [];
        int written = 0;

        foreach (SeriesSample sample in samples)
        {
            long seriesId = ResolveSeriesId(connection, transaction, sample, resolvedNow);

            seriesParam.Value = seriesId;
            capturedParam.Value = sample.TimestampMs;
            valueParam.Value = sample.Value;
            written += insertSample.ExecuteNonQuery();
        }

        transaction.Commit();

        foreach (KeyValuePair<SeriesKey, long> resolved in resolvedNow)
        {
            seriesIds[resolved.Key] = resolved.Value;
        }

        return written;
    }

    /// <summary>Consolidates the raw samples into one-minute buckets.</summary>
    /// <param name="now">Now.</param>
    /// <param name="grace">Wait after a bucket closes.</param>
    /// <param name="maxSpanPerPass">At most how much history in this pass.</param>
    /// <returns>How many buckets were written.</returns>
    public int ConsolidateMinutes(DateTimeOffset now, TimeSpan grace, TimeSpan maxSpanPerPass) =>
        Consolidate(BucketWidths.RawSeconds, BucketWidths.MinuteSeconds, now, grace, maxSpanPerPass);

    /// <summary>Consolidates the one-minute buckets into five-minute buckets.</summary>
    /// <param name="now">Now.</param>
    /// <param name="grace">Wait after a bucket closes.</param>
    /// <param name="maxSpanPerPass">At most how much history in this pass.</param>
    /// <returns>How many buckets were written.</returns>
    public int ConsolidateFiveMinutes(DateTimeOffset now, TimeSpan grace, TimeSpan maxSpanPerPass) =>
        Consolidate(BucketWidths.MinuteSeconds, BucketWidths.FiveMinuteSeconds, now, grace, maxSpanPerPass);

    /// <summary>Deletes raw samples already consolidated and older than the retention.</summary>
    /// <param name="now">Now.</param>
    /// <param name="retention">How long the raw samples are to be kept.</param>
    /// <returns>How many rows were deleted.</returns>
    public int PurgeRaw(DateTimeOffset now, TimeSpan retention)
    {
        using SqliteConnection connection = Open();

        long? cutoff = RetentionPolicy.PurgeCutoff(
            now.ToUnixTimeMilliseconds(),
            retention,
            ReadConsolidatedThrough(connection, transaction: null, BucketWidths.MinuteSeconds));

        if (cutoff is not { } limit)
        {
            return 0;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sample_raw WHERE captured_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", limit);

        return command.ExecuteNonQuery();
    }

    /// <summary>Deletes a level's buckets, never going past the next level.</summary>
    /// <param name="bucketSeconds">Width of the level to clean up.</param>
    /// <param name="now">Now.</param>
    /// <param name="retention">How long that level is to be kept.</param>
    /// <returns>How many rows were deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If the level does not exist.</exception>
    public int PurgeRollup(int bucketSeconds, DateTimeOffset now, TimeSpan retention)
    {
        using SqliteConnection connection = Open();

        long? downstream = bucketSeconds switch
        {
            BucketWidths.MinuteSeconds =>
                ReadConsolidatedThrough(connection, transaction: null, BucketWidths.FiveMinuteSeconds),

            // The last level has nobody downstream: if it waited for a later consolidation
            // it would NEVER delete anything and the file would grow for ever.
            BucketWidths.FiveMinuteSeconds => long.MaxValue,

            _ => throw new ArgumentOutOfRangeException(
                nameof(bucketSeconds),
                bucketSeconds,
                "Unknown aggregation level: only 60 and 300 seconds are supported."),
        };

        long? cutoff = RetentionPolicy.PurgeCutoff(now.ToUnixTimeMilliseconds(), retention, downstream);

        if (cutoff is not { } limit)
        {
            return 0;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM sample_rollup WHERE bucket_seconds = $width AND bucket_start < $cutoff;";
        command.Parameters.AddWithValue("$width", bucketSeconds);
        command.Parameters.AddWithValue("$cutoff", limit);

        return command.ExecuteNonQuery();
    }

    /// <summary>How far a level has already aggregated, or null if it has never run.</summary>
    /// <param name="bucketSeconds">Width of the level.</param>
    /// <returns>The instant, or null.</returns>
    public DateTimeOffset? ConsolidatedThrough(int bucketSeconds)
    {
        using SqliteConnection connection = Open();

        return ReadConsolidatedThrough(connection, transaction: null, bucketSeconds) is { } through
            ? DateTimeOffset.FromUnixTimeMilliseconds(through)
            : null;
    }

    /// <summary>Consolidation and deletion, in the right order, in one go.</summary>
    /// <param name="now">Now.</param>
    /// <param name="options">The history configuration.</param>
    /// <returns>What was written and deleted.</returns>
    public MaintenanceReport RunMaintenance(DateTimeOffset now, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The ORDER is not negotiable: aggregate first, delete afterwards. The other way
        // round, the first pass would delete the raw samples that the same pass's
        // consolidation had still to read — and nobody would notice, because the buckets
        // would be written all the same, just empty.
        int minuteBuckets = ConsolidateMinutes(now, options.ConsolidationGrace, options.MaxSpanPerPass);
        int fiveMinuteBuckets = ConsolidateFiveMinutes(now, options.ConsolidationGrace, options.MaxSpanPerPass);

        return new MaintenanceReport(
            minuteBuckets,
            fiveMinuteBuckets,
            PurgeRaw(now, options.RawRetention),
            PurgeRollup(BucketWidths.MinuteSeconds, now, options.MinuteRetention),
            PurgeRollup(BucketWidths.FiveMinuteSeconds, now, options.FiveMinuteRetention));
    }

    /// <summary>Lists the series present in the history.</summary>
    /// <returns>The series, ordered by collector, metric and instance.</returns>
    public IReadOnlyList<StoredSeries> ListSeries()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT collector_id, metric_id, instance, value_kind FROM series
            ORDER BY collector_id, metric_id, instance;
            """;

        List<StoredSeries> series = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            series.Add(new StoredSeries(
                new SeriesKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                (MetricValueKind)reader.GetInt32(3)));
        }

        return series;
    }

    /// <summary>Reads a window of a series' history.</summary>
    /// <param name="key">The series.</param>
    /// <param name="bucketSeconds">Resolution: 1 for raw, 60 or 300 for the aggregates.</param>
    /// <param name="from">Start of the window, included.</param>
    /// <param name="toExclusive">End of the window, excluded.</param>
    /// <param name="maxPoints">Maximum number of points to return.</param>
    /// <returns>The points, in increasing time order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If the resolution does not exist.</exception>
    public IReadOnlyList<HistoryPoint> ReadHistory(
        SeriesKey key,
        int bucketSeconds,
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        int maxPoints)
    {
        // A made-up resolution must not return an empty list: it would look like "no data"
        // instead of "you asked wrong", and whoever is looking at the graph would conclude
        // that the machine is not monitored.
        if (bucketSeconds is not (BucketWidths.RawSeconds
            or BucketWidths.MinuteSeconds
            or BucketWidths.FiveMinuteSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketSeconds),
                bucketSeconds,
                "Unknown resolution: only 1 (raw), 60 and 300 seconds are supported.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        bool raw = bucketSeconds == BucketWidths.RawSeconds;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = raw ? SelectRawHistorySql : SelectRollupHistorySql;
        command.Parameters.AddWithValue("$collector", key.CollectorId);
        command.Parameters.AddWithValue("$metric", key.MetricId);
        command.Parameters.AddWithValue("$instance", key.Instance);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", toExclusive.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", maxPoints);

        if (!raw)
        {
            command.Parameters.AddWithValue("$width", bucketSeconds);
        }

        List<HistoryPoint> points = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));

            // The raw samples come out with the SAME shape as the aggregates: count 1 and
            // the four values equal to each other. That is what lets the client change
            // resolution without having two drawing branches, one always less tested.
            points.Add(raw
                ? new HistoryPoint(timestamp, 1, reader.GetDouble(1), reader.GetDouble(1), reader.GetDouble(1), reader.GetDouble(1))
                : new HistoryPoint(
                    timestamp,
                    reader.GetInt32(1),
                    reader.GetDouble(2) / reader.GetInt32(1),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5)));
        }

        // The two queries order the OTHER way round on purpose: with an increasing order the
        // LIMIT would keep the oldest points, and asking for ninety days would give a graph
        // that ends seventeen days ago, plausible and with no error at all. On a dashboard
        // the present is the piece you cannot lose. Here the increasing order promised by the
        // contract is put back, so the client draws without reordering anything.
        points.Reverse();

        return points;
    }

    /// <summary>How much room the history takes and how far it is consolidated.</summary>
    /// <returns>The statistics.</returns>
    public StorageStats ReadStats()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = SelectStatsSql;

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException("The row count returned nothing.");
        }

        return new StorageStats(
            DatabasePath,
            FileSizeBytes(),
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            ToTimestamp(ReadConsolidatedThrough(connection, transaction: null, BucketWidths.MinuteSeconds)),
            ToTimestamp(ReadConsolidatedThrough(connection, transaction: null, BucketWidths.FiveMinuteSeconds)));
    }

    private static DateTimeOffset? ToTimestamp(long? unixMs) =>
        unixMs is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;

    private long FileSizeBytes()
    {
        // The WAL is part of the database in every respect: counting only the main file would
        // make a history of hundreds of megabytes look like one of a few kilobytes.
        long total = 0L;

        foreach (string suffix in DatabaseFileSuffixes)
        {
            FileInfo info = new(DatabasePath + suffix);

            if (info.Exists)
            {
                total += info.Length;
            }
        }

        return total;
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();

        using SqliteCommand pragma = connection.CreateCommand();

        // NORMAL with WAL: a power failure can cost the last transactions, never the
        // database. For machine telemetry it is the right compromise — FULL would mean one
        // fsync a second on data worth a few seconds of graph.
        pragma.CommandText = "PRAGMA synchronous = NORMAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private long ResolveSeriesId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeriesSample sample,
        Dictionary<SeriesKey, long> resolvedNow)
    {
        if (seriesIds.TryGetValue(sample.Key, out long cached))
        {
            return cached;
        }

        if (resolvedNow.TryGetValue(sample.Key, out long pending))
        {
            return pending;
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = InsertSeriesSql;
            insert.Parameters.AddWithValue("$collector", sample.Key.CollectorId);
            insert.Parameters.AddWithValue("$metric", sample.Key.MetricId);
            insert.Parameters.AddWithValue("$instance", sample.Key.Instance);
            insert.Parameters.AddWithValue("$kind", (int)sample.Kind);
            insert.ExecuteNonQuery();
        }

        using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = SelectSeriesIdSql;
        select.Parameters.AddWithValue("$collector", sample.Key.CollectorId);
        select.Parameters.AddWithValue("$metric", sample.Key.MetricId);
        select.Parameters.AddWithValue("$instance", sample.Key.Instance);

        object? scalar = select.ExecuteScalar();

        if (scalar is null)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Series {sample.Key.CollectorId}/{sample.Key.MetricId}/{sample.Key.Instance} was inserted but cannot be read back."));
        }

        long seriesId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        resolvedNow[sample.Key] = seriesId;

        return seriesId;
    }

    private int Consolidate(
        int sourceSeconds,
        int targetSeconds,
        DateTimeOffset now,
        TimeSpan grace,
        TimeSpan maxSpanPerPass)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSpanPerPass, TimeSpan.Zero);

        TimeSpan targetWidth = TimeSpan.FromSeconds(targetSeconds);
        long upperLimit = RetentionPolicy.ConsolidationHorizon(now.ToUnixTimeMilliseconds(), targetWidth, grace);

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        if (sourceSeconds != BucketWidths.RawSeconds)
        {
            long? sourceThrough = ReadConsolidatedThrough(connection, transaction, sourceSeconds);

            if (sourceThrough is not { } covered)
            {
                // The level below has never aggregated: any bucket built now would be made
                // of nothing, and the marker would make it permanent.
                return 0;
            }

            // It stops where the lower level reaches, rounded to the nearest full bucket.
            // Without this, a five-minute bucket would be built on three minutes out of
            // five: plausible average, false average, and never corrected again.
            upperLimit = Math.Min(upperLimit, RollupMath.AlignToBucketStart(covered, targetWidth));
        }

        long lower;

        if (ReadConsolidatedThrough(connection, transaction, targetSeconds) is { } resume)
        {
            lower = resume;
        }
        else
        {
            long? firstSource = ReadFirstSourceTimestamp(connection, transaction, sourceSeconds);

            if (firstSource is not { } first)
            {
                // There is simply nothing to aggregate. The marker advances all the same, so
                // as not to rescan the emptiness at every maintenance pass.
                WriteConsolidatedThrough(connection, transaction, targetSeconds, upperLimit);
                transaction.Commit();

                return 0;
            }

            lower = RollupMath.AlignToBucketStart(first, targetWidth);
        }

        long upper = RollupMath.AlignToBucketStart(
            Math.Min(upperLimit, lower + (long)maxSpanPerPass.TotalMilliseconds),
            targetWidth);

        if (upper <= lower)
        {
            return 0;
        }

        int written = 0;

        using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = UpsertRollupSql;

            SqliteParameter seriesParam = upsert.Parameters.Add("$series", SqliteType.Integer);
            SqliteParameter widthParam = upsert.Parameters.Add("$width", SqliteType.Integer);
            SqliteParameter startParam = upsert.Parameters.Add("$start", SqliteType.Integer);
            SqliteParameter countParam = upsert.Parameters.Add("$count", SqliteType.Integer);
            SqliteParameter sumParam = upsert.Parameters.Add("$sum", SqliteType.Real);
            SqliteParameter minParam = upsert.Parameters.Add("$min", SqliteType.Real);
            SqliteParameter maxParam = upsert.Parameters.Add("$max", SqliteType.Real);
            SqliteParameter lastParam = upsert.Parameters.Add("$last", SqliteType.Real);

            widthParam.Value = targetSeconds;

            foreach (KeyValuePair<long, List<RollupBucket>> group in
                ReadSourceWindow(connection, transaction, sourceSeconds, lower, upper))
            {
                seriesParam.Value = group.Key;

                foreach (RollupBucket bucket in RollupMath.Combine(group.Value, targetWidth))
                {
                    startParam.Value = bucket.BucketStartMs;
                    countParam.Value = bucket.Count;
                    sumParam.Value = bucket.Sum;
                    minParam.Value = bucket.Min;
                    maxParam.Value = bucket.Max;
                    lastParam.Value = bucket.Last;
                    upsert.ExecuteNonQuery();
                    written++;
                }
            }
        }

        WriteConsolidatedThrough(connection, transaction, targetSeconds, upper);
        transaction.Commit();

        return written;
    }

    private static Dictionary<long, List<RollupBucket>> ReadSourceWindow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sourceSeconds,
        long fromMs,
        long toMs)
    {
        bool raw = sourceSeconds == BucketWidths.RawSeconds;

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = raw ? SelectRawWindowSql : SelectRollupWindowSql;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);

        if (!raw)
        {
            command.Parameters.AddWithValue("$width", sourceSeconds);
        }

        Dictionary<long, List<RollupBucket>> bySeries = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            long seriesId = reader.GetInt64(0);

            RollupBucket bucket = raw
                ? RollupBucket.FromSample(reader.GetInt64(1), reader.GetDouble(2))
                : new RollupBucket(
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6));

            if (!bySeries.TryGetValue(seriesId, out List<RollupBucket>? buckets))
            {
                buckets = [];
                bySeries[seriesId] = buckets;
            }

            buckets.Add(bucket);
        }

        return bySeries;
    }

    private static long? ReadFirstSourceTimestamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sourceSeconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        if (sourceSeconds == BucketWidths.RawSeconds)
        {
            command.CommandText = "SELECT MIN(captured_at) FROM sample_raw;";
        }
        else
        {
            command.CommandText = "SELECT MIN(bucket_start) FROM sample_rollup WHERE bucket_seconds = $width;";
            command.Parameters.AddWithValue("$width", sourceSeconds);
        }

        object? scalar = command.ExecuteScalar();

        return scalar is null or DBNull ? null : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static long? ReadConsolidatedThrough(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int bucketSeconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT consolidated_through FROM rollup_state WHERE bucket_seconds = $width;";
        command.Parameters.AddWithValue("$width", bucketSeconds);

        object? scalar = command.ExecuteScalar();

        return scalar is null or DBNull ? null : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static void WriteConsolidatedThrough(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int bucketSeconds,
        long throughMs)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertRollupStateSql;
        command.Parameters.AddWithValue("$width", bucketSeconds);
        command.Parameters.AddWithValue("$through", throughMs);
        command.ExecuteNonQuery();
    }
}
