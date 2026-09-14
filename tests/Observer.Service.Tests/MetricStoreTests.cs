using System.Globalization;
using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// Lo strato SQLite su un database vero. Qui non si riverifica la matematica del rollup —
/// quella e' gia' provata a parte — ma tutto cio' che solo un database puo' sbagliare:
/// identita' delle serie, transazioni, idempotenza, ordine fra consolidamento e
/// cancellazione.
/// </summary>
public class MetricStoreTests
{
    private static readonly TimeSpan NoGrace = TimeSpan.Zero;
    private static readonly TimeSpan OneHourPerPass = TimeSpan.FromHours(1);

    private static DateTimeOffset T(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SeriesSample CpuSample(string isoInstant, double value, string instance = "") =>
        new(
            new SeriesKey("cpu", "cpu.usage.total", instance),
            MetricValueKind.Number,
            T(isoInstant).ToUnixTimeMilliseconds(),
            value);

    private static SeriesKey CpuSeries(string instance = "") =>
        new("cpu", "cpu.usage.total", instance);

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using TempMetricStore temp = new();

        // Il servizio riparte e richiama Initialize su un file che esiste gia': se questa
        // riga lanciasse, il servizio non ripartirebbe mai una seconda volta.
        temp.Store.Initialize();

        Assert.Empty(temp.Store.ListSeries());
        Assert.True(File.Exists(temp.DatabasePath));
    }

    [Fact]
    public void ReadHistory_RawBeyondTheLimit_KeepsTheNewestPointsNotTheOldest()
    {
        // Con ORDER BY crescente + LIMIT si tengono i punti PIU' VECCHI. Una richiesta a 90
        // giorni su bucket da 5 minuti vale 25920 punti contro un limite di 5000: il grafico
        // sembrerebbe finire diciassette giorni fa, plausibile e senza alcun errore.
        // Su una dashboard il presente e' il pezzo che non si puo' perdere.
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 1d),
            CpuSample("2026-08-26T12:00:01Z", 2d),
            CpuSample("2026-08-26T12:00:02Z", 3d),
            CpuSample("2026-08-26T12:00:03Z", 4d),
            CpuSample("2026-08-26T12:00:04Z", 5d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 3);

        Assert.Equal(3, points.Count);

        // Gli ultimi tre, e comunque restituiti in ordine crescente: il client disegna da
        // sinistra a destra e non deve riordinare nulla.
        Assert.Equal(T("2026-08-26T12:00:02Z"), points[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:03Z"), points[1].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:04Z"), points[2].Timestamp);
        Assert.Equal(5d, points[2].Last);
    }

    [Fact]
    public void Write_ReadsRawBackInTheSameShapeAsAggregates()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 10d),
            CpuSample("2026-08-26T12:00:01Z", 20d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        Assert.Equal(2, points.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), points[0].Timestamp);

        // Sul grezzo conteggio 1 e i quattro valori coincidono: e' cio' che permette al
        // client di cambiare risoluzione senza avere due rami di disegno diversi.
        Assert.Equal(1, points[0].Count);
        Assert.Equal(10d, points[0].Average);
        Assert.Equal(10d, points[0].Min);
        Assert.Equal(10d, points[0].Max);
        Assert.Equal(10d, points[0].Last);
        Assert.Equal(20d, points[1].Last);
    }

    [Fact]
    public void Write_DoesNotDuplicateTheSeriesOnEverySample()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:01Z", 2d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:02Z", 3d)]);

        StoredSeries stored = Assert.Single(temp.Store.ListSeries());

        Assert.Equal("cpu.usage.total", stored.Key.MetricId);
        Assert.Equal(string.Empty, stored.Key.Instance);
        Assert.Equal(MetricValueKind.Number, stored.Kind);
    }

    [Fact]
    public void Write_KeepsInstancesApart()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:00Z", 1d),
            CpuSample("2026-08-26T12:00:00Z", 2d, "core0"),
            CpuSample("2026-08-26T12:00:00Z", 3d, "core1"),
        ]);

        Assert.Equal(3, temp.Store.ListSeries().Count);

        HistoryPoint point = Assert.Single(temp.Store.ReadHistory(
            CpuSeries("core1"), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(3d, point.Last);
    }

    [Fact]
    public void Write_TheSameInstantTwiceDoesNotCreateTwoRows()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);
        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 2d)]);

        // Il servizio puo' riscrivere lo stesso snapshot dopo un errore transitorio. Senza
        // upsert la scrittura lancerebbe e la coda si bloccherebbe; con un INSERT semplice
        // ignorato resterebbe il valore vecchio.
        HistoryPoint point = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(2d, point.Last);
    }

    [Fact]
    public void Consolidate_ProducesTheMinuteBucketWithTheRightValues()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 10d),
            CpuSample("2026-08-26T12:00:20Z", 30d),
            CpuSample("2026-08-26T12:00:30Z", 20d),
        ]);

        int written = temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(1, written);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(20d, bucket.Average);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
    }

    [Fact]
    public void Consolidate_LeavesTheMinuteInProgressAlone()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:30Z", 1d),
            CpuSample("2026-08-26T12:01:30Z", 2d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:01:40Z"), NoGrace, OneHourPerPass);

        // Il minuto delle 12:01 e' ancora aperto: consolidarlo adesso lo congelerebbe a un
        // solo campione, e la cancellazione del grezzo renderebbe l'errore definitivo.
        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
    }

    [Fact]
    public void Consolidate_TwoPassesInARowDoNotDoubleTheCounts()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 10d),
            CpuSample("2026-08-26T12:00:20Z", 20d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);
        int secondPass = temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(0, secondPass);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // Se il consolidamento sommasse invece di riscrivere, qui ci sarebbero 4 campioni e
        // una media perfettamente credibile calcolata sul doppio dei dati.
        Assert.Equal(2, bucket.Count);
        Assert.Equal(15d, bucket.Average);
    }

    [Fact]
    public void Consolidate_ResumesWhereItLeftOff()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:30Z", 1d)]);
        Assert.Equal(1, temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass));

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:02:10Z", 2d)]);
        Assert.Equal(1, temp.Store.ConsolidateMinutes(T("2026-08-26T12:04:00Z"), NoGrace, OneHourPerPass));

        IReadOnlyList<HistoryPoint> bucket = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100);

        Assert.Equal(2, bucket.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:02:00Z"), bucket[1].Timestamp);
    }

    [Fact]
    public void ConsolidateFiveMinutes_DoesNotRunAheadOfTheMinuteLevel()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());

        // A un minuto siamo arrivati solo alle 12:03. Un bucket da cinque minuti costruito
        // adesso conterrebbe tre minuti su cinque: numero plausibile, media falsa, e
        // siccome il rollup avanza il segnaposto non verrebbe mai piu' corretto.
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:03:10Z"), NoGrace, OneHourPerPass);

        int written = temp.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(0, written);
        Assert.Null(temp.Store.ConsolidatedThrough(BucketWidths.FiveMinuteSeconds));
        Assert.Empty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));
    }

    [Fact]
    public void ConsolidateFiveMinutes_ProducesTheBucketOnceTheLevelBelowCoversIt()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);

        int written = temp.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        Assert.Equal(1, written);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(5, bucket.Count);
    }

    [Fact]
    public void ConsolidateFiveMinutes_AverageMatchesTheRawAverage()
    {
        using TempMetricStore temp = new();

        // Minuti con un numero DIVERSO di campioni: e' il caso normale, non un caso limite.
        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T12:00:10Z", 100d),
            CpuSample("2026-08-26T12:01:10Z", 0d),
            CpuSample("2026-08-26T12:01:20Z", 0d),
            CpuSample("2026-08-26T12:01:30Z", 0d),
            CpuSample("2026-08-26T12:02:10Z", 0d),
            CpuSample("2026-08-26T12:02:20Z", 0d),
            CpuSample("2026-08-26T12:03:10Z", 0d),
            CpuSample("2026-08-26T12:04:10Z", 0d),
        ]);

        temp.Store.ConsolidateMinutes(T("2026-08-26T12:06:00Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        HistoryPoint bucket = Assert.Single(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // 100 / 8 = 12,5. La media delle medie dei minuti darebbe 20.
        Assert.Equal(8, bucket.Count);
        Assert.Equal(100d / 8d, bucket.Average);
        Assert.Equal(100d, bucket.Max);
        Assert.Equal(0d, bucket.Min);
    }

    [Fact]
    public void PurgeRaw_LeavesWhatNothingHasAggregatedYet()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:00Z", 1d)]);

        // Il rollup non ha mai girato: quel campione esiste in un solo posto al mondo.
        int purged = temp.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, purged);
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));
    }

    [Fact]
    public void PurgeRaw_DeletesWhatIsAlreadyAggregatedAndOldEnough()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        int purged = temp.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(1, purged);
        Assert.Empty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        // Il grezzo sparisce, il riassunto resta: e' esattamente il punto del rollup.
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void PurgeRaw_LeavesWhatIsStillInsideTheRetentionWindow()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        int purged = temp.Store.PurgeRaw(T("2026-08-26T12:05:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, purged);
    }

    [Fact]
    public void PurgeRollup_MinutesDoNotRunAheadOfFiveMinutes()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples([CpuSample("2026-08-26T12:00:10Z", 1d)]);
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), NoGrace, OneHourPerPass);

        // Il livello a cinque minuti non ha mai girato: cancellare i minuti significherebbe
        // perdere quel tratto di storico per sempre.
        int purged = temp.Store.PurgeRollup(
            BucketWidths.MinuteSeconds, T("2026-09-30T00:00:00Z"), TimeSpan.FromDays(7));

        Assert.Equal(0, purged);
        Assert.NotEmpty(temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void PurgeRollup_FiveMinutesHasNoLevelBelowItAndGoesByRetentionAlone()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        // L'ultimo livello non ha nessuno a valle: se aspettasse un consolidamento
        // successivo non cancellerebbe MAI nulla e il file crescerebbe per sempre.
        int purged = temp.Store.PurgeRollup(
            BucketWidths.FiveMinuteSeconds, T("2027-01-01T00:00:00Z"), TimeSpan.FromDays(90));

        Assert.Equal(1, purged);
    }

    [Fact]
    public void ReadHistory_ReturnsOnlyTheRequestedWindow()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(
        [
            CpuSample("2026-08-26T11:59:59Z", 1d),
            CpuSample("2026-08-26T12:00:00Z", 2d),
            CpuSample("2026-08-26T12:00:30Z", 3d),
            CpuSample("2026-08-26T12:01:00Z", 4d),
        ]);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        // Estremo iniziale incluso, finale escluso: cosi' due finestre consecutive non
        // mostrano lo stesso punto due volte.
        Assert.Equal(2, points.Count);
        Assert.Equal(2d, points[0].Last);
        Assert.Equal(3d, points[1].Last);
    }

    [Fact]
    public void ReadHistory_RespectsThePointLimit()
    {
        using TempMetricStore temp = new();

        List<SeriesSample> samples = [];

        for (int second = 0; second < 50; second++)
        {
            samples.Add(new SeriesSample(
                CpuSeries(),
                MetricValueKind.Number,
                T("2026-08-26T12:00:00Z").AddSeconds(second).ToUnixTimeMilliseconds(),
                second));
        }

        temp.Store.WriteSamples(samples);

        IReadOnlyList<HistoryPoint> points = temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 10);

        // Il limite protegge il servizio: una finestra di un mese a risoluzione un secondo
        // non deve poter costruire in memoria una risposta da centinaia di megabyte.
        Assert.Equal(10, points.Count);
    }

    [Fact]
    public void ReadHistory_OfAnUnknownSeriesIsEmptyNotAnError()
    {
        using TempMetricStore temp = new();

        Assert.Empty(temp.Store.ReadHistory(
            new SeriesKey("gpu", "gpu.temp", "0"),
            BucketWidths.RawSeconds,
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T13:00:00Z"),
            100));
    }

    [Fact]
    public void ReadHistory_RejectsAnUnknownResolution()
    {
        using TempMetricStore temp = new();

        // Una risoluzione inventata non deve restituire una lista vuota: sembrerebbe
        // "nessun dato" invece di "hai sbagliato a chiedere".
        Assert.Throws<ArgumentOutOfRangeException>(() => temp.Store.ReadHistory(
            CpuSeries(), 30, T("2026-08-26T12:00:00Z"), T("2026-08-26T13:00:00Z"), 100));
    }

    [Fact]
    public void ReadStats_CountsSeriesRowsAndConsolidation()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());
        temp.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), NoGrace, OneHourPerPass);
        temp.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), NoGrace, OneHourPerPass);

        StorageStats stats = temp.Store.ReadStats();

        Assert.Equal(1L, stats.SeriesCount);
        Assert.Equal(7L, stats.RawSamples);
        Assert.Equal(7L, stats.MinuteBuckets);
        Assert.Equal(1L, stats.FiveMinuteBuckets);
        Assert.Equal(T("2026-08-26T12:07:00Z"), stats.MinuteConsolidatedThrough);
        Assert.Equal(T("2026-08-26T12:05:00Z"), stats.FiveMinuteConsolidatedThrough);
        Assert.True(stats.FileSizeBytes > 0L);
    }

    [Fact]
    public void RunMaintenance_ConsolidatesAndPurgesInOnePass()
    {
        using TempMetricStore temp = new();

        temp.Store.WriteSamples(SevenMinutesOfSamples());

        StorageOptions options = new()
        {
            ConsolidationGrace = TimeSpan.Zero,
            RawRetention = TimeSpan.FromMinutes(1),
            MinuteRetention = TimeSpan.FromDays(7),
            FiveMinuteRetention = TimeSpan.FromDays(90),
        };

        // Un solo giro deve fare tutto NELL'ORDINE giusto: prima aggregare, poi cancellare.
        // Invertendo l'ordine il primo giro cancellerebbe il grezzo che il consolidamento
        // dello stesso giro doveva ancora leggere.
        MaintenanceReport report = temp.Store.RunMaintenance(T("2026-08-26T12:10:00Z"), options);

        Assert.Equal(7, report.MinuteBucketsWritten);

        // Due bucket da cinque minuti, non uno: i minuti sono consolidati fino alle 12:10,
        // quindi anche l'intervallo 12:05-12:10 e' chiuso, per quanto contenga solo due
        // minuti di dati veri.
        Assert.Equal(2, report.FiveMinuteBucketsWritten);
        Assert.Equal(7, report.RawRowsPurged);
        Assert.Equal(0, report.MinuteRowsPurged);
        Assert.Equal(0, report.FiveMinuteRowsPurged);

        // I minuti restano leggibili anche se il grezzo e' sparito.
        Assert.Equal(7, temp.Store.ReadHistory(
            CpuSeries(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100).Count);
    }

    private static IReadOnlyList<SeriesSample> SevenMinutesOfSamples() =>
    [
        CpuSample("2026-08-26T12:00:10Z", 1d),
        CpuSample("2026-08-26T12:01:10Z", 2d),
        CpuSample("2026-08-26T12:02:10Z", 3d),
        CpuSample("2026-08-26T12:03:10Z", 4d),
        CpuSample("2026-08-26T12:04:10Z", 5d),
        CpuSample("2026-08-26T12:05:10Z", 6d),
        CpuSample("2026-08-26T12:06:10Z", 7d),
    ];
}