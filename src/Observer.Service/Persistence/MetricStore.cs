using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Lo strato SQLite. Fa SOLO da magazzino: la matematica del rollup e le decisioni di
/// ritenzione vivono in <see cref="RollupMath"/> e <see cref="RetentionPolicy"/>, dove si
/// possono provare senza toccare un file.
/// </summary>
/// <remarks>
/// <para>
/// Ogni metodo apre e chiude la sua connessione. Con il pool di Microsoft.Data.Sqlite aprire
/// costa quasi nulla, e in cambio non esiste uno stato condiviso da sincronizzare fra il
/// servizio che scrive e le richieste HTTP che leggono.
/// </para>
/// <para>
/// Il giornale e' in modalita' WAL: i lettori non aspettano lo scrittore, che e' proprio il
/// requisito "le risposte HTTP non devono rallentare quando si scrive".
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

        -- L'identita' di una serie. instance e' NOT NULL e vale stringa vuota quando la
        -- metrica e' unica per macchina: in un indice UNIQUE di SQLite due NULL NON sono
        -- uguali, quindi con NULL la stessa serie verrebbe reinserita a ogni secondo.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_series_identity
            ON series (collector_id, metric_id, instance);

        -- WITHOUT ROWID: la riga e' quasi tutta chiave, e cosi' non esistono ne' il rowid
        -- nascosto ne' l'indice separato sulla primary key. Su una tabella che cresce di
        -- una riga al secondo per serie e' la differenza fra un file e un problema.
        CREATE TABLE IF NOT EXISTS sample_raw (
            series_id   INTEGER NOT NULL REFERENCES series (series_id),
            captured_at INTEGER NOT NULL,
            value       REAL    NOT NULL,
            PRIMARY KEY (series_id, captured_at)
        ) WITHOUT ROWID;

        -- Serve alla cancellazione, che filtra per tempo su TUTTE le serie: senza, ogni
        -- giro di manutenzione scandirebbe l'intera tabella.
        CREATE INDEX IF NOT EXISTS ix_raw_time ON sample_raw (captured_at);

        -- Un'unica tabella per tutti i livelli, con l'ampiezza come colonna: aggiungere un
        -- terzo livello sara' una riga di configurazione, non una migrazione di schema.
        -- value_sum e sample_count invece della media: ricombinando bucket con un numero
        -- diverso di campioni, la media delle medie e' un numero credibile e falso.
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

        -- Il segnaposto di ogni livello: fin dove ha gia' aggregato. E' quello che permette
        -- a un giro di manutenzione di leggere solo il nuovo invece di riscandire tutto, ed
        -- e' anche il vincolo che impedisce di cancellare dati non ancora riassunti.
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
        ORDER BY r.captured_at
        LIMIT $limit;
        """;

    private const string SelectRollupHistorySql = """
        SELECT b.bucket_start, b.sample_count, b.value_sum, b.value_min, b.value_max, b.value_last
        FROM sample_rollup b
        JOIN series s ON s.series_id = b.series_id
        WHERE s.collector_id = $collector AND s.metric_id = $metric AND s.instance = $instance
          AND b.bucket_seconds = $width AND b.bucket_start >= $from AND b.bucket_start < $to
        ORDER BY b.bucket_start
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

    /// <summary>I tre file che SQLite usa in modalita' WAL.</summary>
    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm"];

    /// <summary>
    /// Le serie gia' viste. Evita due interrogazioni al secondo per ogni metrica: le serie
    /// sono poche decine e non spariscono mai, quindi la cache non puo' invecchiare male.
    /// </summary>
    private readonly ConcurrentDictionary<SeriesKey, long> seriesIds = new();

    private readonly string connectionString;

    /// <summary>Crea il magazzino sul file indicato. Non apre nulla finche' non serve.</summary>
    /// <param name="databasePath">Percorso del file SQLite.</param>
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

            // Microsoft.Data.Sqlite trasforma questo timeout in un'attesa su SQLITE_BUSY.
            // Serve perche' scrittore e lettori sono processi logici diversi sullo stesso
            // file: senza, una lettura durante un commit fallirebbe invece di aspettare.
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Percorso assoluto del file.</summary>
    public string DatabasePath { get; }

    /// <summary>Crea lo schema se manca. Idempotente.</summary>
    public void Initialize()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        // WAL resta scritto nel file e vale per sempre; synchronous e' invece per
        // connessione e va rimesso a ogni apertura (vedi Open).
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();

        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    /// <summary>Scrive un lotto di campioni grezzi in un'unica transazione.</summary>
    /// <param name="samples">I campioni da scrivere.</param>
    /// <returns>Quante righe grezze sono state scritte.</returns>
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

        // Gli identificatori risolti adesso entrano nella cache condivisa SOLO dopo il
        // commit: se la transazione fallisse, la cache resterebbe altrimenti piena di
        // identificatori di righe che non esistono.
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

    /// <summary>Consolida il grezzo in bucket da un minuto.</summary>
    /// <param name="now">Adesso.</param>
    /// <param name="grace">Attesa dopo la chiusura di un bucket.</param>
    /// <param name="maxSpanPerPass">Quanto tempo di storico al massimo in questo giro.</param>
    /// <returns>Quanti bucket sono stati scritti.</returns>
    public int ConsolidateMinutes(DateTimeOffset now, TimeSpan grace, TimeSpan maxSpanPerPass) =>
        Consolidate(BucketWidths.RawSeconds, BucketWidths.MinuteSeconds, now, grace, maxSpanPerPass);

    /// <summary>Consolida i bucket da un minuto in bucket da cinque minuti.</summary>
    /// <param name="now">Adesso.</param>
    /// <param name="grace">Attesa dopo la chiusura di un bucket.</param>
    /// <param name="maxSpanPerPass">Quanto tempo di storico al massimo in questo giro.</param>
    /// <returns>Quanti bucket sono stati scritti.</returns>
    public int ConsolidateFiveMinutes(DateTimeOffset now, TimeSpan grace, TimeSpan maxSpanPerPass) =>
        Consolidate(BucketWidths.MinuteSeconds, BucketWidths.FiveMinuteSeconds, now, grace, maxSpanPerPass);

    /// <summary>Cancella il grezzo gia' consolidato e piu' vecchio della ritenzione.</summary>
    /// <param name="now">Adesso.</param>
    /// <param name="retention">Per quanto si vuole tenere il grezzo.</param>
    /// <returns>Quante righe sono state cancellate.</returns>
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

    /// <summary>Cancella i bucket di un livello, senza mai superare il livello successivo.</summary>
    /// <param name="bucketSeconds">Ampiezza del livello da ripulire.</param>
    /// <param name="now">Adesso.</param>
    /// <param name="retention">Per quanto si vuole tenere quel livello.</param>
    /// <returns>Quante righe sono state cancellate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se il livello non esiste.</exception>
    public int PurgeRollup(int bucketSeconds, DateTimeOffset now, TimeSpan retention)
    {
        using SqliteConnection connection = Open();

        long? downstream = bucketSeconds switch
        {
            BucketWidths.MinuteSeconds =>
                ReadConsolidatedThrough(connection, transaction: null, BucketWidths.FiveMinuteSeconds),

            // L'ultimo livello non ha nessuno a valle: se aspettasse un consolidamento
            // successivo non cancellerebbe MAI nulla e il file crescerebbe per sempre.
            BucketWidths.FiveMinuteSeconds => long.MaxValue,

            _ => throw new ArgumentOutOfRangeException(
                nameof(bucketSeconds),
                bucketSeconds,
                "Livello di aggregazione sconosciuto: sono previsti solo 60 e 300 secondi."),
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

    /// <summary>Fin dove un livello ha gia' aggregato, oppure null se non ha mai girato.</summary>
    /// <param name="bucketSeconds">Ampiezza del livello.</param>
    /// <returns>L'istante, oppure null.</returns>
    public DateTimeOffset? ConsolidatedThrough(int bucketSeconds)
    {
        using SqliteConnection connection = Open();

        return ReadConsolidatedThrough(connection, transaction: null, bucketSeconds) is { } through
            ? DateTimeOffset.FromUnixTimeMilliseconds(through)
            : null;
    }

    /// <summary>Consolidamento e cancellazione, nell'ordine giusto, in un colpo solo.</summary>
    /// <param name="now">Adesso.</param>
    /// <param name="options">La configurazione dello storico.</param>
    /// <returns>Cosa e' stato scritto e cancellato.</returns>
    public MaintenanceReport RunMaintenance(DateTimeOffset now, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // L'ORDINE non e' negoziabile: prima si aggrega, poi si cancella. Al contrario, il
        // primo giro cancellerebbe il grezzo che il consolidamento dello stesso giro doveva
        // ancora leggere — e non se ne accorgerebbe nessuno, perche' i bucket verrebbero
        // comunque scritti, solo vuoti.
        int minuteBuckets = ConsolidateMinutes(now, options.ConsolidationGrace, options.MaxSpanPerPass);
        int fiveMinuteBuckets = ConsolidateFiveMinutes(now, options.ConsolidationGrace, options.MaxSpanPerPass);

        return new MaintenanceReport(
            minuteBuckets,
            fiveMinuteBuckets,
            PurgeRaw(now, options.RawRetention),
            PurgeRollup(BucketWidths.MinuteSeconds, now, options.MinuteRetention),
            PurgeRollup(BucketWidths.FiveMinuteSeconds, now, options.FiveMinuteRetention));
    }

    /// <summary>Elenca le serie presenti nello storico.</summary>
    /// <returns>Le serie, ordinate per collector, metrica e istanza.</returns>
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

    /// <summary>Legge una finestra di storico di una serie.</summary>
    /// <param name="key">La serie.</param>
    /// <param name="bucketSeconds">Risoluzione: 1 per il grezzo, 60 o 300 per gli aggregati.</param>
    /// <param name="from">Inizio della finestra, incluso.</param>
    /// <param name="toExclusive">Fine della finestra, esclusa.</param>
    /// <param name="maxPoints">Numero massimo di punti da restituire.</param>
    /// <returns>I punti, in ordine di tempo crescente.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se la risoluzione non esiste.</exception>
    public IReadOnlyList<HistoryPoint> ReadHistory(
        SeriesKey key,
        int bucketSeconds,
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        int maxPoints)
    {
        // Una risoluzione inventata non deve restituire una lista vuota: sembrerebbe
        // "nessun dato" invece di "hai sbagliato a chiedere", e chi guarda il grafico
        // concluderebbe che la macchina non e' monitorata.
        if (bucketSeconds is not (BucketWidths.RawSeconds
            or BucketWidths.MinuteSeconds
            or BucketWidths.FiveMinuteSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketSeconds),
                bucketSeconds,
                "Risoluzione sconosciuta: sono previsti solo 1 (grezzo), 60 e 300 secondi.");
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

            // Il grezzo esce con la STESSA forma degli aggregati: conteggio 1 e i quattro
            // valori uguali fra loro. E' cio' che permette al client di cambiare risoluzione
            // senza avere due rami di disegno, di cui uno sempre meno collaudato.
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

        return points;
    }

    /// <summary>Quanto occupa lo storico e fin dove e' consolidato.</summary>
    /// <returns>Le statistiche.</returns>
    public StorageStats ReadStats()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = SelectStatsSql;

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException("Il conteggio delle righe non ha restituito nulla.");
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
        // Il WAL fa parte del database a tutti gli effetti: contare solo il file principale
        // farebbe apparire uno storico da centinaia di megabyte come uno da pochi kilobyte.
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

        // NORMAL con WAL: una mancanza di corrente puo' costare le ultime transazioni, mai
        // il database. Per telemetria di macchina e' il compromesso giusto — FULL
        // significherebbe un fsync al secondo su un dato che vale pochi secondi di grafico.
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
                $"La serie {sample.Key.CollectorId}/{sample.Key.MetricId}/{sample.Key.Instance} e' stata inserita ma non si rilegge."));
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
                // Il livello sotto non ha mai aggregato: qualunque bucket costruito adesso
                // sarebbe fatto di niente, e il segnaposto lo renderebbe definitivo.
                return 0;
            }

            // Ci si ferma dove arriva il livello inferiore, arrotondato al bucket pieno piu'
            // vicino. Senza questo, un bucket da cinque minuti verrebbe costruito su tre
            // minuti su cinque: media plausibile, media falsa, e mai piu' corretta.
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
                // Non c'e' proprio niente da aggregare. Il segnaposto avanza lo stesso, per
                // non riscandire il vuoto a ogni giro di manutenzione.
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
