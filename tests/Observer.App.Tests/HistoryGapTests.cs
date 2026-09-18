using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Quando una macchina NON stava misurando, letto dal suo storico al rientro.
/// </summary>
/// <remarks>
/// E' la risposta alla domanda "cosa mi sono perso mentre la finestra era chiusa", e il dato per
/// darla esiste gia' sul disco della macchina remota. Le regole qui servono tutte a una cosa
/// sola: non dichiarare un'interruzione che nessuno ha osservato. Un riepilogo che grida al lupo
/// a ogni apertura si impara a chiudere senza leggerlo, che e' peggio di non averlo.
/// </remarks>
public class HistoryGapTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Punti a un passo l'uno dall'altro, da <paramref name="start"/>.</summary>
    private static IEnumerable<HistoryPoint> Series(DateTimeOffset start, int count, TimeSpan? step = null) =>
        Enumerable.Range(0, count)
            .Select(i => new HistoryPoint(start + ((step ?? Minute) * i), 60, 0.5d, 0.5d, 0.5d, 0.5d));

    [Fact]
    public void AMachineThatAlwaysMeasuredHasNothingToReport()
    {
        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(
            [.. Series(Noon, 30)],
            TimeSpan.FromMinutes(30),
            Minute);

        Assert.Empty(gaps);
    }

    [Fact]
    public void AnOutageInTheMiddleIsReportedWithItsStartAndEnd()
    {
        // Dieci minuti di misure, venti di niente, dieci di misure: e' il caso per cui il
        // riepilogo esiste.
        List<HistoryPoint> points = [.. Series(Noon, 10), .. Series(Noon + TimeSpan.FromMinutes(30), 10)];

        HistoryGap candidate = Assert.Single(HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(40), Minute));

        Assert.Equal(Noon + TimeSpan.FromMinutes(10), candidate.Start);
        Assert.Equal(Noon + TimeSpan.FromMinutes(30), candidate.End);
        Assert.Equal(TimeSpan.FromMinutes(20), candidate.Duration);
        Assert.False(candidate.AtEdge);
    }

    [Fact]
    public void TwoOutagesStayTwoOldestFirst()
    {
        List<HistoryPoint> points =
        [
            .. Series(Noon, 5),
            .. Series(Noon + TimeSpan.FromMinutes(10), 5),
            .. Series(Noon + TimeSpan.FromMinutes(25), 5),
        ];

        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(30), Minute);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), gaps[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), gaps[1].Duration);

        // In ordine, dalla piu' vecchia: e' l'ordine in cui si racconta una giornata.
        Assert.True(gaps[0].Start < gaps[1].Start);
    }

    [Fact]
    public void AGapTouchingTheOldestEdgeIsFlaggedAtEdge()
    {
        // A sinistra non si sa se e' un'interruzione o la fine di cio' che il servizio
        // conserva: la ritenzione cancella un PREFISSO, ed e' indistinguibile da una macchina
        // accesa a meta' finestra. Chiamarla "interruzione di 40 minuti" sarebbe inventare.
        IReadOnlyList<HistoryGap> gaps = HistoryStrip.FindGaps(
            [.. Series(Noon + TimeSpan.FromMinutes(40), 20)],
            TimeSpan.FromHours(1),
            Minute);

        Assert.True(Assert.Single(gaps).AtEdge);
    }

    [Fact]
    public void TheREALShapeOfAResponseProducesNoGap()
    {
        // La forma vera, che e' l'unica che conta e che le altre prove qui NON hanno: il
        // livello aggregato e' indietro di qualche minuto per il consolidamento, quindi
        // l'ultimo punto NON e' adesso; e il chiamante chiede piu' indietro della finestra che
        // esamina, proprio perche' la griglia si ancora a quell'ultimo punto e sfora a
        // sinistra. Con le due cose insieme non deve uscire nessun vuoto.
        //
        // Costruita come la manderebbe il servizio: 60 minuti di finestra, margine di dieci
        // minuti davanti (TailFor a un'ora), e la serie che finisce cinque minuti prima di
        // adesso. Senza il margine nella richiesta, qui esce un'assenza AtEdge e la riga
        // diventa "nothing known before" su una macchina che ha misurato tutto il tempo.
        TimeSpan window = TimeSpan.FromHours(1);
        TimeSpan margin = TimeSpan.FromMinutes(10);
        DateTimeOffset now = Noon;

        IReadOnlyList<HistoryPoint> response =
            [.. Series(now - window - margin, (int)((window + margin - TimeSpan.FromMinutes(5)) / Minute))];

        Assert.Empty(HistoryStrip.FindGaps(response, window, Minute));

        // E la controprova, che e' cio' che rende questa una prova e non un rito: la stessa
        // serie SENZA il margine - cioe' cio' che il codice faceva prima - un vuoto lo produce.
        IReadOnlyList<HistoryPoint> withoutMargin =
            [.. Series(now - window, (int)((window - TimeSpan.FromMinutes(5)) / Minute))];

        Assert.True(Assert.Single(HistoryStrip.FindGaps(withoutMargin, window, Minute)).AtEdge);
    }

    [Fact]
    public void AnOffsetBetweenTwoClocksDoesNotInventOutages()
    {
        // Tutto sta nell'orologio della MACCHINA. Due serie identiche, una traslata di venti
        // minuti - una macchina virtuale, un Windows fuori dominio - devono dire la stessa
        // cosa: uno scarto trasla la serie, non apre buchi in mezzo. Se la finestra si
        // ancorasse all'orologio del CLIENT, la seconda direbbe cose diverse dalla prima.
        List<HistoryPoint> here = [.. Series(Noon, 10), .. Series(Noon + TimeSpan.FromMinutes(25), 10)];
        List<HistoryPoint> ahead =
        [
            .. Series(Noon + TimeSpan.FromMinutes(20), 10),
            .. Series(Noon + TimeSpan.FromMinutes(45), 10),
        ];

        IReadOnlyList<HistoryGap> a = HistoryStrip.FindGaps(here, TimeSpan.FromMinutes(35), Minute);
        IReadOnlyList<HistoryGap> b = HistoryStrip.FindGaps(ahead, TimeSpan.FromMinutes(35), Minute);

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(a).Duration);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a[0].Duration, b[0].Duration);
        Assert.Equal(a[0].AtEdge, b[0].AtEdge);

        // E gli estremi sono traslati esattamente dello scarto, non di piu' e non di meno.
        Assert.Equal(a[0].Start + TimeSpan.FromMinutes(20), b[0].Start);
    }

    [Fact]
    public void AnEmptySeriesDoesNotInventAWindowLongOutage()
    {
        // "Non si sa niente" non e' "e' stata giu' tutto il tempo". La differenza la dice il
        // chiamante con un'altra frase; qui restituire una finestra intera di vuoto sarebbe
        // dichiarare un'interruzione che nessuno ha osservato.
        Assert.Empty(HistoryStrip.FindGaps([], TimeSpan.FromHours(1), Minute));
    }

    [Fact]
    public void GapsAreSearchedAtTheSourceStepNotTheBarStep()
    {
        // A sette giorni la barra della striscia copre DUE ORE: un'interruzione di quaranta
        // minuti ci finisce dentro come intervallo parziale e non verrebbe vista affatto.
        // Cercandola al passo della sorgente, cinque minuti, si vede.
        TimeSpan fiveMinutes = TimeSpan.FromMinutes(5);
        List<HistoryPoint> points =
        [
            .. Series(Noon, 2, fiveMinutes),
            .. Series(Noon + TimeSpan.FromMinutes(50), 1, fiveMinutes),
        ];

        HistoryGap gap = Assert.Single(HistoryStrip.FindGaps(points, TimeSpan.FromMinutes(55), fiveMinutes));

        Assert.Equal(TimeSpan.FromMinutes(40), gap.Duration);
        Assert.False(gap.AtEdge);

        // Lo stesso dato, al passo della barra da due ore: quei quaranta minuti non compaiono
        // piu' da nessuna parte.
        Assert.DoesNotContain(
            HistoryStrip.FindGaps(points, TimeSpan.FromDays(7), TimeSpan.FromHours(2)),
            candidate => candidate.Duration == TimeSpan.FromMinutes(40));
    }
}