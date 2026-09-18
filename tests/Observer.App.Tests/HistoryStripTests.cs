using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// La griglia della striscia, e la bugia che esiste per impedire.
/// </summary>
/// <remarks>
/// Il servizio non manda i buchi: un intervallo senza campioni non arriva con zero campioni,
/// non arriva affatto. Chi disegnasse una barretta per ogni punto ricevuto otterrebbe una
/// striscia continua e piena anche da una macchina spenta meta' giornata — i buchi
/// sparirebbero stringendosi, e chi guarda leggerebbe una macchina sempre accesa. E' una
/// bugia raccontata con dati veri: non fallisce niente, e nessun altro test la vedrebbe.
/// </remarks>
public class HistoryStripTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static HistoryPoint PointAt(int minutesAgo, double average, int samples = 60) =>
        new(Now - TimeSpan.FromMinutes(minutesAgo), samples, average, average, average, average);

    [Fact]
    public void APartialBarIsDrawnInProportionToItsCoverage()
    {
        // L'ultima barra della striscia e' sempre l'intervallo IN CORSO. A un minuto di passo
        // la differenza non si nota; a due ore, dopo cinque minuti, una barra piena direbbe
        // "due ore cosi'" proprio dove l'occhio legge "adesso".
        HistoryBar oneTwelfth = new(Now, BarKind.Partial, 0.5d, 0.5d, 0.5d, 600, 7200);

        Assert.Equal(10d, HistoryStrip.WidthOf(oneTwelfth, 120d), 9);
    }

    [Fact]
    public void APartialBarNeverShrinksBelowOnePixel()
    {
        // Sotto il pixel si leggerebbe come un buco, che vuol dire un'altra cosa: la' non si
        // e' misurato, qui si e' misurato poco.
        HistoryBar newborn = new(Now, BarKind.Partial, 0.5d, 0.5d, 0.5d, 1, 7200);

        Assert.Equal(1d, HistoryStrip.WidthOf(newborn, 6d), 9);
    }

    [Fact]
    public void FullBarsAndGapsAreDrawnAtFullWidth()
    {
        // Stringere una barra piena sarebbe una bugia al contrario, e un buco ha gia' il suo
        // segno: la larghezza parla solo di quanto un intervallo e' stato coperto.
        HistoryBar full = new(Now, BarKind.Measured, 0.5d, 0.5d, 0.5d, 60, 60);
        HistoryBar gap = new(Now, BarKind.Missing, 0d, 0d, 0d, 0, 60);

        Assert.Equal(6d, HistoryStrip.WidthOf(full, 6d), 9);
        Assert.Equal(6d, HistoryStrip.WidthOf(gap, 6d), 9);
    }

    [Fact]
    public void SeveralPointsInOneBarAreAveragedInsteadOfLost()
    {
        // Il caso che nasce appena il passo della barra supera quello dei punti: un quarto
        // d'ora di barra su punti da cinque minuti. Senza il raggruppamento dentro Build
        // ne sopravviveva UNO — l'ultimo iterato — e la barra mostrava quel campione
        // spacciandolo per la media di tutti e tre. Con tre punti a 0,2, 0,5 e 0,8 la
        // differenza fra la media vera e l'ultimo valore e' l'intera scala.
        List<HistoryPoint> points =
        [
            PointAt(14, 0.2d, samples: 300),
            PointAt(9, 0.5d, samples: 300),
            PointAt(4, 0.8d, samples: 300),
        ];

        // Due barre: i tre punti cadono tutti nel quarto d'ora PRECEDENTE a quello in corso,
        // perche' Adesso e' allineato alle 12:00 in punto.
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build(points, Now, barCount: 2, TimeSpan.FromMinutes(15));

        HistoryBar full = strip[0];

        Assert.Equal(0.5d, full.Average, 9);
        Assert.Equal(0.2d, full.Min, 9);
        Assert.Equal(0.8d, full.Max, 9);

        // E i campioni si sommano: 900 su 900, cioe' un quarto d'ora coperto per intero.
        Assert.Equal(900, full.Samples);
        Assert.Equal(BarKind.Measured, full.Kind);
    }

    [Fact]
    public void AGapStaysAGapAndKeepsItsPlaceInTime()
    {
        // IL test. Tre punti su dieci intervalli devono dare DIECI barrette, non tre: sette
        // sono buchi e devono restare al proprio posto nel tempo. Se questa cade, la striscia
        // racconta una macchina sempre accesa a chi l'ha spenta.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [PointAt(9, 0.5d), PointAt(5, 0.6d), PointAt(0, 0.7d)],
            Now,
            barCount: 10,
            Minute);

        Assert.Equal(10, strip.Count);
        Assert.Equal(7, strip.Count(bar => bar.Kind == BarKind.Missing));

        // E stanno esattamente dove devono: il primo, il quinto e l'ultimo.
        Assert.Equal(BarKind.Measured, strip[0].Kind);
        Assert.Equal(BarKind.Measured, strip[4].Kind);
        Assert.Equal(BarKind.Measured, strip[9].Kind);
        Assert.Equal(BarKind.Missing, strip[1].Kind);
    }

    [Fact]
    public void AGapHasNoValueToShow()
    {
        // Un intervallo assente non porta uno zero: uno zero e' una misura, e disegnarlo
        // direbbe "qui la macchina era a riposo" invece di "qui non si sa niente".
        HistoryBar gap = Assert.Single(HistoryStrip.Build([], Now, barCount: 1, Minute));

        Assert.Equal(BarKind.Missing, gap.Kind);
        Assert.Equal(0, gap.Samples);
    }

    [Fact]
    public void APartlyCoveredIntervalIsNotPassedOffAsComplete()
    {
        // Misurato sul servizio vero: fermandolo a meta' minuto, quel minuto arriva lo stesso
        // ma con 53 campioni su 60, e con una media calcolata solo su quelli. E' un numero
        // plausibile su mezzo minuto, e va detto che e' mezzo minuto.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [PointAt(0, 0.42d, samples: 53)],
            Now,
            barCount: 1,
            Minute);

        Assert.Equal(BarKind.Partial, strip[0].Kind);
        Assert.Equal(53, strip[0].Samples);
        Assert.Equal(60, strip[0].Expected);
    }

    [Fact]
    public void AnOffGridInstantStillFallsInTheRightInterval()
    {
        // I timestamp arrivano gia' allineati, ma bastano pochi millisecondi di scarto perche'
        // un confronto per uguaglianza faccia sparire la barretta. E una barretta che sparisce
        // si legge come "non misurato", cioe' il caso peggiore.
        HistoryPoint offGrid = new(
            Now - TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(37),
            60,
            0.33d,
            0.3d,
            0.4d,
            0.35d);

        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build(
            [offGrid],
            Now,
            barCount: 2,
            Minute);

        Assert.Equal(BarKind.Measured, strip[0].Kind);
        Assert.Equal(0.33d, strip[0].Average);
    }

    [Fact]
    public void TheStripRunsFromOldestToMostRecent()
    {
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.True(strip[0].Start < strip[1].Start);
        Assert.True(strip[1].Start < strip[2].Start);
    }

    [Fact]
    public void BucketingDoesNotAverageTheAverages()
    {
        // Due intervalli con copertura diversa: 50 campioni a 0.20 e 10 campioni a 0.90.
        // La media vera e' (50*0.20 + 10*0.90) / 60 = 0.3166..., non (0.20+0.90)/2 = 0.55.
        // La media delle medie e' un numero credibile e falso, ed e' l'errore piu' facile.
        IReadOnlyList<HistoryPoint> bucketed = HistoryStrip.Bucket(
            [
                new(Now, 50, 0.20d, 0.10d, 0.30d, 0.20d),
                new(Now + TimeSpan.FromSeconds(50), 10, 0.90d, 0.80d, 0.95d, 0.90d),
            ],
            Minute);

        HistoryPoint merged = Assert.Single(bucketed);

        Assert.Equal(60, merged.Count);
        Assert.Equal(0.31666d, merged.Avg, 4);
        Assert.Equal(0.10d, merged.Min);
        Assert.Equal(0.95d, merged.Max);
    }

    [Fact]
    public void ATailWithMoreSamplesBeatsTheLaggingAggregate()
    {
        // Il consolidamento degli aggregati ha una grazia di quattro minuti, quindi sugli
        // ultimi intervalli l'aggregato e' incompleto. Dove le due letture si sovrappongono
        // deve valere la piu' fresca, altrimenti sarebbe l'aggregato a mentire.
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now, 12, 0.10d, 0.10d, 0.10d, 0.10d)],
            [new(Now, 60, 0.80d, 0.70d, 0.90d, 0.85d)]);

        HistoryPoint point = Assert.Single(merged);

        Assert.Equal(60, point.Count);
        Assert.Equal(0.80d, point.Avg);
    }

    [Fact]
    public void ATruncatedIntervalDoesNotReplaceACompleteOne()
    {
        // Il grezzo si chiede da un istante qualsiasi - "dieci minuti fa" - che non cade sul
        // confine di un intervallo, quindi il PRIMO intervallo della coda arriva sempre
        // tagliato. Se vincesse per il solo fatto di essere piu' fresco, una barra misurata
        // per intero si disegnerebbe larga la meta' (e' parziale), il suggerimento direbbe
        // "30 of 60 samples", e media, minimo e massimo salterebbero mezzo minuto di misure:
        // un picco li' dentro sparirebbe. Vince chi ha piu' campioni, non chi arriva dopo.
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now, 60, 0.30d, 0.05d, 0.95d, 0.30d)],
            [new(Now, 30, 0.30d, 0.28d, 0.32d, 0.30d)]);

        HistoryPoint point = Assert.Single(merged);

        Assert.Equal(60, point.Count);
        Assert.Equal(0.95d, point.Max);
    }

    [Fact]
    public void MergingKeepsTheIntervalsOnlyOneReadingHas()
    {
        IReadOnlyList<HistoryPoint> merged = HistoryStrip.Merge(
            [new(Now - TimeSpan.FromMinutes(30), 60, 0.10d, 0.1d, 0.1d, 0.1d)],
            [new(Now, 60, 0.80d, 0.8d, 0.8d, 0.8d)]);

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].Timestamp < merged[1].Timestamp);
    }

    [Fact]
    public void ExpectedSamplesFollowTheIntervalLength()
    {
        // Il servizio campiona una volta al secondo: e' cio' che rende "quanti campioni sono
        // arrivati" una misura della copertura, e non un dettaglio.
        Assert.Equal(60, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromMinutes(1)));
        Assert.Equal(300, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, HistoryStrip.ExpectedSamplesIn(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AStripWithNoBarsOrNoStepIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Build([], Now, barCount: 0, Minute));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Build([], Now, barCount: 5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(9.9d, 0)]
    [InlineData(10d, 1)]
    [InlineData(99.9d, 9)]
    public void ThePointerFallsInTheRightBar(double x, int expected) =>
        Assert.Equal(expected, HistoryStrip.IndexAt(x, width: 100d, barCount: 10));

    [Theory]
    [InlineData(100d)]
    [InlineData(101d)]
    [InlineData(-1d)]
    public void OutsideTheStripThereIsNoBar(double x)
    {
        // Il bordo destro sbaglia da solo: con x esattamente uguale alla larghezza la
        // divisione da' dieci, cioe' un indice che non esiste, e senza il controllo il
        // suggerimento leggerebbe fuori dall'elenco.
        Assert.Equal(-1, HistoryStrip.IndexAt(x, width: 100d, barCount: 10));
    }

    [Fact]
    public void TheTooltipSaysTheIntervalNotTheInstant()
    {
        // Una barra copre da un minuto a due ore secondo il periodo: mostrarne solo l'inizio
        // lascerebbe indovinare quanto e' larga. Il passo si ricava dalle barre stesse, non da
        // una costante.
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build([PointAt(1, 0.5d)], Now, barCount: 3, Minute);

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2}$", HistoryStrip.Describe(strip, 1));
    }

    [Fact]
    public void PastADayTheTooltipAlsoSaysTheDay()
    {
        // A sette giorni la striscia copre 168 ore in 84 barre e non ha assi ne' etichette:
        // il suggerimento e' l'unico modo di collocare una barra nel tempo, e "04:00 – 06:00"
        // da solo compare su SETTE barre, una per giorno. Chi vede un picco - che e' il motivo
        // per cui si guarda una settimana - non saprebbe di che giorno e'. Il nome del giorno
        // basta: fra due barre passano al massimo 166 ore, quindi la coppia non si ripete.
        IReadOnlyList<HistoryBar> week =
            HistoryStrip.Build([], Now, barCount: 84, TimeSpan.FromHours(2));

        Assert.Matches(@"^[A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2} · ", HistoryStrip.Describe(week, 40));

        // A ventiquattro ore l'arco vale esattamente un giorno: la soglia e' stretta, e la
        // frase resta corta dove non serve allungarla.
        IReadOnlyList<HistoryBar> day =
            HistoryStrip.Build([], Now, barCount: 96, TimeSpan.FromMinutes(15));

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2} · ", HistoryStrip.Describe(day, 40));
    }

    [Fact]
    public void OnAGapTheTooltipSaysNothingWasMeasured()
    {
        // "Non misurato" non e' "zero", ed e' la stessa distinzione che il disegno fa gia' col
        // tratteggio: qui la si dice a parole, per chi ci passa sopra a controllare.
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.EndsWith("not measured", HistoryStrip.Describe(strip, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void OnAHalfCoveredIntervalTheTooltipSaysHowManySamples()
    {
        IReadOnlyList<HistoryBar> strip =
            HistoryStrip.Build([PointAt(1, 0.5d, samples: 31)], Now, barCount: 3, Minute);

        Assert.EndsWith(
            "31 of 60 samples", HistoryStrip.Describe(strip, 1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnIndexThatDoesNotExistProducesNoTooltip(int index)
    {
        IReadOnlyList<HistoryBar> strip = HistoryStrip.Build([], Now, barCount: 3, Minute);

        Assert.Empty(HistoryStrip.Describe(strip, index));
    }
}