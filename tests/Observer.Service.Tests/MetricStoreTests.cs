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
    private static readonly TimeSpan SenzaGrazia = TimeSpan.Zero;
    private static readonly TimeSpan UnGiroIntero = TimeSpan.FromHours(1);

    private static DateTimeOffset T(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SeriesSample Campione(string istanteIso, double valore, string istanza = "") =>
        new(
            new SeriesKey("cpu", "cpu.usage.total", istanza),
            MetricValueKind.Number,
            T(istanteIso).ToUnixTimeMilliseconds(),
            valore);

    private static SeriesKey Serie(string istanza = "") =>
        new("cpu", "cpu.usage.total", istanza);

    [Fact]
    public void Inizializza_EIdempotente()
    {
        using TempMetricStore temporaneo = new();

        // Il servizio riparte e richiama Initialize su un file che esiste gia': se questa
        // riga lanciasse, il servizio non ripartirebbe mai una seconda volta.
        temporaneo.Store.Initialize();

        Assert.Empty(temporaneo.Store.ListSeries());
        Assert.True(File.Exists(temporaneo.DatabasePath));
    }

    [Fact]
    public void ReadHistory_GrezzoOltreIlLimite_TieneIPuntiPiuRecentiNonIPiuVecchi()
    {
        // Con ORDER BY crescente + LIMIT si tengono i punti PIU' VECCHI. Una richiesta a 90
        // giorni su bucket da 5 minuti vale 25920 punti contro un limite di 5000: il grafico
        // sembrerebbe finire diciassette giorni fa, plausibile e senza alcun errore.
        // Su una dashboard il presente e' il pezzo che non si puo' perdere.
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:00Z", 1d),
            Campione("2026-08-26T12:00:01Z", 2d),
            Campione("2026-08-26T12:00:02Z", 3d),
            Campione("2026-08-26T12:00:03Z", 4d),
            Campione("2026-08-26T12:00:04Z", 5d),
        ]);

        IReadOnlyList<HistoryPoint> punti = temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 3);

        Assert.Equal(3, punti.Count);

        // Gli ultimi tre, e comunque restituiti in ordine crescente: il client disegna da
        // sinistra a destra e non deve riordinare nulla.
        Assert.Equal(T("2026-08-26T12:00:02Z"), punti[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:03Z"), punti[1].Timestamp);
        Assert.Equal(T("2026-08-26T12:00:04Z"), punti[2].Timestamp);
        Assert.Equal(5d, punti[2].Last);
    }

    [Fact]
    public void Scrive_ERileggeIlGrezzoConLaStessaFormaDegliAggregati()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:00Z", 10d),
            Campione("2026-08-26T12:00:01Z", 20d),
        ]);

        IReadOnlyList<HistoryPoint> punti = temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        Assert.Equal(2, punti.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), punti[0].Timestamp);

        // Sul grezzo conteggio 1 e i quattro valori coincidono: e' cio' che permette al
        // client di cambiare risoluzione senza avere due rami di disegno diversi.
        Assert.Equal(1, punti[0].Count);
        Assert.Equal(10d, punti[0].Average);
        Assert.Equal(10d, punti[0].Min);
        Assert.Equal(10d, punti[0].Max);
        Assert.Equal(10d, punti[0].Last);
        Assert.Equal(20d, punti[1].Last);
    }

    [Fact]
    public void Scrive_NonDuplicaLaSerieAOgniCampione()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:00Z", 1d)]);
        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:01Z", 2d)]);
        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:02Z", 3d)]);

        StoredSeries serie = Assert.Single(temporaneo.Store.ListSeries());

        Assert.Equal("cpu.usage.total", serie.Key.MetricId);
        Assert.Equal(string.Empty, serie.Key.Instance);
        Assert.Equal(MetricValueKind.Number, serie.Kind);
    }

    [Fact]
    public void Scrive_TieneSeparateLeIstanze()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:00Z", 1d),
            Campione("2026-08-26T12:00:00Z", 2d, "core0"),
            Campione("2026-08-26T12:00:00Z", 3d, "core1"),
        ]);

        Assert.Equal(3, temporaneo.Store.ListSeries().Count);

        HistoryPoint punto = Assert.Single(temporaneo.Store.ReadHistory(
            Serie("core1"), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(3d, punto.Last);
    }

    [Fact]
    public void Scrive_LoStessoIstanteDueVolteNonCreaDueRighe()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:00Z", 1d)]);
        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:00Z", 2d)]);

        // Il servizio puo' riscrivere lo stesso snapshot dopo un errore transitorio. Senza
        // upsert la scrittura lancerebbe e la coda si bloccherebbe; con un INSERT semplice
        // ignorato resterebbe il valore vecchio.
        HistoryPoint punto = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        Assert.Equal(2d, punto.Last);
    }

    [Fact]
    public void Consolida_ProduceIlBucketDaUnMinutoConIValoriGiusti()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:10Z", 10d),
            Campione("2026-08-26T12:00:20Z", 30d),
            Campione("2026-08-26T12:00:30Z", 20d),
        ]);

        int scritti = temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);

        Assert.Equal(1, scritti);

        HistoryPoint bucket = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(3, bucket.Count);
        Assert.Equal(20d, bucket.Average);
        Assert.Equal(10d, bucket.Min);
        Assert.Equal(30d, bucket.Max);
        Assert.Equal(20d, bucket.Last);
    }

    [Fact]
    public void Consolida_NonToccaIlMinutoInCorso()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:30Z", 1d),
            Campione("2026-08-26T12:01:30Z", 2d),
        ]);

        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:01:40Z"), SenzaGrazia, UnGiroIntero);

        // Il minuto delle 12:01 e' ancora aperto: consolidarlo adesso lo congelerebbe a un
        // solo campione, e la cancellazione del grezzo renderebbe l'errore definitivo.
        HistoryPoint bucket = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
    }

    [Fact]
    public void Consolida_DueGiriDiSeguitoNonRaddoppianoIConteggi()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:10Z", 10d),
            Campione("2026-08-26T12:00:20Z", 20d),
        ]);

        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);
        int secondoGiro = temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);

        Assert.Equal(0, secondoGiro);

        HistoryPoint bucket = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // Se il consolidamento sommasse invece di riscrivere, qui ci sarebbero 4 campioni e
        // una media perfettamente credibile calcolata sul doppio dei dati.
        Assert.Equal(2, bucket.Count);
        Assert.Equal(15d, bucket.Average);
    }

    [Fact]
    public void Consolida_RiprendeDaDoveEraRimasto()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:30Z", 1d)]);
        Assert.Equal(1, temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero));

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:02:10Z", 2d)]);
        Assert.Equal(1, temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:04:00Z"), SenzaGrazia, UnGiroIntero));

        IReadOnlyList<HistoryPoint> bucket = temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100);

        Assert.Equal(2, bucket.Count);
        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket[0].Timestamp);
        Assert.Equal(T("2026-08-26T12:02:00Z"), bucket[1].Timestamp);
    }

    [Fact]
    public void ConsolidaCinqueMinuti_NonSuperaIlConsolidamentoAUnMinuto()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(SetteMinutiDiCampioni());

        // A un minuto siamo arrivati solo alle 12:03. Un bucket da cinque minuti costruito
        // adesso conterrebbe tre minuti su cinque: numero plausibile, media falsa, e
        // siccome il rollup avanza il segnaposto non verrebbe mai piu' corretto.
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:03:10Z"), SenzaGrazia, UnGiroIntero);

        int scritti = temporaneo.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), SenzaGrazia, UnGiroIntero);

        Assert.Equal(0, scritti);
        Assert.Null(temporaneo.Store.ConsolidatedThrough(BucketWidths.FiveMinuteSeconds));
        Assert.Empty(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));
    }

    [Fact]
    public void ConsolidaCinqueMinuti_ProduceIlBucketQuandoIlLivelloSottoLoCopre()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(SetteMinutiDiCampioni());
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), SenzaGrazia, UnGiroIntero);

        int scritti = temporaneo.Store.ConsolidateFiveMinutes(
            T("2026-08-26T12:10:00Z"), SenzaGrazia, UnGiroIntero);

        Assert.Equal(1, scritti);

        HistoryPoint bucket = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100));

        Assert.Equal(T("2026-08-26T12:00:00Z"), bucket.Timestamp);
        Assert.Equal(5, bucket.Count);
    }

    [Fact]
    public void ConsolidaCinqueMinuti_LaMediaCoincideConQuellaDeiGrezzi()
    {
        using TempMetricStore temporaneo = new();

        // Minuti con un numero DIVERSO di campioni: e' il caso normale, non un caso limite.
        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T12:00:10Z", 100d),
            Campione("2026-08-26T12:01:10Z", 0d),
            Campione("2026-08-26T12:01:20Z", 0d),
            Campione("2026-08-26T12:01:30Z", 0d),
            Campione("2026-08-26T12:02:10Z", 0d),
            Campione("2026-08-26T12:02:20Z", 0d),
            Campione("2026-08-26T12:03:10Z", 0d),
            Campione("2026-08-26T12:04:10Z", 0d),
        ]);

        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:06:00Z"), SenzaGrazia, UnGiroIntero);
        temporaneo.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), SenzaGrazia, UnGiroIntero);

        HistoryPoint bucket = Assert.Single(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.FiveMinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));

        // 100 / 8 = 12,5. La media delle medie dei minuti darebbe 20.
        Assert.Equal(8, bucket.Count);
        Assert.Equal(100d / 8d, bucket.Average);
        Assert.Equal(100d, bucket.Max);
        Assert.Equal(0d, bucket.Min);
    }

    [Fact]
    public void CancellaGrezzo_NonToccaCioCheNessunoHaAncoraAggregato()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:00Z", 1d)]);

        // Il rollup non ha mai girato: quel campione esiste in un solo posto al mondo.
        int cancellati = temporaneo.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, cancellati);
        Assert.NotEmpty(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));
    }

    [Fact]
    public void CancellaGrezzo_EliminaCioCheEGiaAggregatoEVecchio()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:10Z", 1d)]);
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);

        int cancellati = temporaneo.Store.PurgeRaw(T("2026-08-26T20:00:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(1, cancellati);
        Assert.Empty(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100));

        // Il grezzo sparisce, il riassunto resta: e' esattamente il punto del rollup.
        Assert.NotEmpty(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void CancellaGrezzo_NonToccaCioCheEAncoraDentroLaFinestra()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:10Z", 1d)]);
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);

        int cancellati = temporaneo.Store.PurgeRaw(T("2026-08-26T12:05:00Z"), TimeSpan.FromHours(6));

        Assert.Equal(0, cancellati);
    }

    [Fact]
    public void CancellaBucket_UnMinutoNonSuperaICinqueMinuti()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples([Campione("2026-08-26T12:00:10Z", 1d)]);
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:02:00Z"), SenzaGrazia, UnGiroIntero);

        // Il livello a cinque minuti non ha mai girato: cancellare i minuti significherebbe
        // perdere quel tratto di storico per sempre.
        int cancellati = temporaneo.Store.PurgeRollup(
            BucketWidths.MinuteSeconds, T("2026-09-30T00:00:00Z"), TimeSpan.FromDays(7));

        Assert.Equal(0, cancellati);
        Assert.NotEmpty(temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:05:00Z"), 100));
    }

    [Fact]
    public void CancellaBucket_CinqueMinutiNonHaLivelloSuccessivoEUsaSoloLaRitenzione()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(SetteMinutiDiCampioni());
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), SenzaGrazia, UnGiroIntero);
        temporaneo.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), SenzaGrazia, UnGiroIntero);

        // L'ultimo livello non ha nessuno a valle: se aspettasse un consolidamento
        // successivo non cancellerebbe MAI nulla e il file crescerebbe per sempre.
        int cancellati = temporaneo.Store.PurgeRollup(
            BucketWidths.FiveMinuteSeconds, T("2027-01-01T00:00:00Z"), TimeSpan.FromDays(90));

        Assert.Equal(1, cancellati);
    }

    [Fact]
    public void Storico_RestituisceSoloLaFinestraRichiesta()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(
        [
            Campione("2026-08-26T11:59:59Z", 1d),
            Campione("2026-08-26T12:00:00Z", 2d),
            Campione("2026-08-26T12:00:30Z", 3d),
            Campione("2026-08-26T12:01:00Z", 4d),
        ]);

        IReadOnlyList<HistoryPoint> punti = temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 100);

        // Estremo iniziale incluso, finale escluso: cosi' due finestre consecutive non
        // mostrano lo stesso punto due volte.
        Assert.Equal(2, punti.Count);
        Assert.Equal(2d, punti[0].Last);
        Assert.Equal(3d, punti[1].Last);
    }

    [Fact]
    public void Storico_RispettaIlLimiteDiPunti()
    {
        using TempMetricStore temporaneo = new();

        List<SeriesSample> molti = [];

        for (int secondo = 0; secondo < 50; secondo++)
        {
            molti.Add(new SeriesSample(
                Serie(),
                MetricValueKind.Number,
                T("2026-08-26T12:00:00Z").AddSeconds(secondo).ToUnixTimeMilliseconds(),
                secondo));
        }

        temporaneo.Store.WriteSamples(molti);

        IReadOnlyList<HistoryPoint> punti = temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.RawSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:01:00Z"), 10);

        // Il limite protegge il servizio: una finestra di un mese a risoluzione un secondo
        // non deve poter costruire in memoria una risposta da centinaia di megabyte.
        Assert.Equal(10, punti.Count);
    }

    [Fact]
    public void Storico_DiUnaSerieInesistenteEVuotoNonUnErrore()
    {
        using TempMetricStore temporaneo = new();

        Assert.Empty(temporaneo.Store.ReadHistory(
            new SeriesKey("gpu", "gpu.temp", "0"),
            BucketWidths.RawSeconds,
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T13:00:00Z"),
            100));
    }

    [Fact]
    public void Storico_RifiutaUnaRisoluzioneSconosciuta()
    {
        using TempMetricStore temporaneo = new();

        // Una risoluzione inventata non deve restituire una lista vuota: sembrerebbe
        // "nessun dato" invece di "hai sbagliato a chiedere".
        Assert.Throws<ArgumentOutOfRangeException>(() => temporaneo.Store.ReadHistory(
            Serie(), 30, T("2026-08-26T12:00:00Z"), T("2026-08-26T13:00:00Z"), 100));
    }

    [Fact]
    public void Statistiche_ContanoSerieRigheEconsolidamento()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(SetteMinutiDiCampioni());
        temporaneo.Store.ConsolidateMinutes(T("2026-08-26T12:07:10Z"), SenzaGrazia, UnGiroIntero);
        temporaneo.Store.ConsolidateFiveMinutes(T("2026-08-26T12:10:00Z"), SenzaGrazia, UnGiroIntero);

        StorageStats statistiche = temporaneo.Store.ReadStats();

        Assert.Equal(1L, statistiche.SeriesCount);
        Assert.Equal(7L, statistiche.RawSamples);
        Assert.Equal(7L, statistiche.MinuteBuckets);
        Assert.Equal(1L, statistiche.FiveMinuteBuckets);
        Assert.Equal(T("2026-08-26T12:07:00Z"), statistiche.MinuteConsolidatedThrough);
        Assert.Equal(T("2026-08-26T12:05:00Z"), statistiche.FiveMinuteConsolidatedThrough);
        Assert.True(statistiche.FileSizeBytes > 0L);
    }

    [Fact]
    public void Manutenzione_ConsolidaEcancellaInUnColpoSolo()
    {
        using TempMetricStore temporaneo = new();

        temporaneo.Store.WriteSamples(SetteMinutiDiCampioni());

        StorageOptions opzioni = new()
        {
            ConsolidationGrace = TimeSpan.Zero,
            RawRetention = TimeSpan.FromMinutes(1),
            MinuteRetention = TimeSpan.FromDays(7),
            FiveMinuteRetention = TimeSpan.FromDays(90),
        };

        // Un solo giro deve fare tutto NELL'ORDINE giusto: prima aggregare, poi cancellare.
        // Invertendo l'ordine il primo giro cancellerebbe il grezzo che il consolidamento
        // dello stesso giro doveva ancora leggere.
        MaintenanceReport esito = temporaneo.Store.RunMaintenance(T("2026-08-26T12:10:00Z"), opzioni);

        Assert.Equal(7, esito.MinuteBucketsWritten);

        // Due bucket da cinque minuti, non uno: i minuti sono consolidati fino alle 12:10,
        // quindi anche l'intervallo 12:05-12:10 e' chiuso, per quanto contenga solo due
        // minuti di dati veri.
        Assert.Equal(2, esito.FiveMinuteBucketsWritten);
        Assert.Equal(7, esito.RawRowsPurged);
        Assert.Equal(0, esito.MinuteRowsPurged);
        Assert.Equal(0, esito.FiveMinuteRowsPurged);

        // I minuti restano leggibili anche se il grezzo e' sparito.
        Assert.Equal(7, temporaneo.Store.ReadHistory(
            Serie(), BucketWidths.MinuteSeconds, T("2026-08-26T12:00:00Z"), T("2026-08-26T12:30:00Z"), 100).Count);
    }

    private static IReadOnlyList<SeriesSample> SetteMinutiDiCampioni() =>
    [
        Campione("2026-08-26T12:00:10Z", 1d),
        Campione("2026-08-26T12:01:10Z", 2d),
        Campione("2026-08-26T12:02:10Z", 3d),
        Campione("2026-08-26T12:03:10Z", 4d),
        Campione("2026-08-26T12:04:10Z", 5d),
        Campione("2026-08-26T12:05:10Z", 6d),
        Campione("2026-08-26T12:06:10Z", 7d),
    ];
}