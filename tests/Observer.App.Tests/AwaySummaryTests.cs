using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;

namespace Observer.App.Tests;

/// <summary>
/// La frase che dice cosa e' successo mentre la finestra era chiusa.
/// </summary>
/// <remarks>
/// E' la risposta onesta che questo stack puo' dare alla richiesta "avvisami se una macchina cade
/// mentre non guardo": l'avviso vero non e' consegnabile senza poter fallire in silenzio, questo
/// non puo' fallire in silenzio perche' non promette niente mentre nessuno guarda.
/// </remarks>
public class AwaySummaryTests
{
    /// <remarks>
    /// Mezzogiorno LOCALE, non UTC: la frase mostra l'ora della macchina di chi guarda (come
    /// <c>HistoryStrip.Describe</c>), quindi un istante UTC renderebbe il test dipendente dal
    /// fuso di chi lo esegue - verde qui e rosso sul runner, o viceversa.
    /// </remarks>
    private static readonly DateTimeOffset Noon = new(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Local));

    private static HistoryGap Gap(int startMinute, int endMinute, bool atEdge = false) =>
        new(Noon.AddMinutes(startMinute), Noon.AddMinutes(endMinute), atEdge);

    [Fact]
    public void NoGapsMeansNoLine()
    {
        // Il silenzio e' un risultato: "ho chiesto e non c'era niente". Una riga "all good" per
        // ogni macchina sana riempirebbe il riquadro proprio nel caso in cui non serve, e lo si
        // imparerebbe a chiudere senza leggerlo.
        Assert.Equal(string.Empty, AwaySummary.LineFor("lavoro", [], withDay: false));
    }

    [Fact]
    public void ASingleOutageSaysHowLongAndWhen()
    {
        string line = AwaySummary.LineFor("lavoro", [Gap(20, 200)], withDay: false);

        Assert.Equal("lavoro: not measured for 3 h (12:20 – 15:20)", line);

        // Con una sola, "in 1 period" sarebbe rumore: il totale E' quella.
        Assert.DoesNotContain("period", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralOutagesSayHowManyAndWhichWasLongest()
    {
        // Il totale da solo mentirebbe per omissione: tre ore in un colpo e tre ore in dieci
        // singhiozzi sono due macchine diverse, e la piu' lunga e' quella che decide se alzarsi
        // dalla sedia.
        string line = AwaySummary.LineFor("lavoro", [Gap(10, 20), Gap(60, 240), Gap(300, 310)], withDay: false);

        Assert.Contains("in 3 periods", line, StringComparison.Ordinal);
        Assert.Contains("longest 13:00 – 16:00", line, StringComparison.Ordinal);
        Assert.Contains("3 h 20 min", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGapAtTheEdgeIsNotCountedAsAnOutage()
    {
        // La ritenzione cancella un PREFISSO, indistinguibile da una macchina accesa a meta'
        // finestra: chiamarlo interruzione sarebbe inventare, e una frase inventata insegna a
        // non fidarsi delle altre.
        string edgeOnly = AwaySummary.LineFor("casa", [Gap(0, 45, atEdge: true)], withDay: false);

        Assert.Equal("casa: nothing known before 12:45", edgeOnly);
        Assert.DoesNotContain("not measured", edgeOnly, StringComparison.Ordinal);

        // E quando c'e' anche un'interruzione vera, il bordo resta una nota in coda e NON entra
        // nel totale: venti minuti, non sessantacinque.
        string edgeAndOutage = AwaySummary.LineFor("casa", [Gap(0, 45, atEdge: true), Gap(60, 80)], withDay: false);

        Assert.Contains("not measured for 20 min", edgeAndOutage, StringComparison.Ordinal);
        Assert.Contains("nothing known before 12:45", edgeAndOutage, StringComparison.Ordinal);
        Assert.DoesNotContain("periods", edgeAndOutage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDayFlagAddsTheWeekdayToBothTimes()
    {
        // Stessa soglia e stessa ragione di HistoryStrip.Describe: a sette giorni "14:20" puo'
        // essere uno qualunque di sette pomeriggi.
        string plain = AwaySummary.LineFor("lavoro", [Gap(20, 200)], withDay: false);
        string dated = AwaySummary.LineFor("lavoro", [Gap(20, 200)], withDay: true);

        Assert.Matches(@"\(\d{2}:\d{2} – \d{2}:\d{2}\)", plain);
        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", dated);
    }

    [Fact]
    public void AnOutageAcrossMidnightDoesNotReadBackwards()
    {
        // A ventiquattro ore un'assenza puo' durare quasi l'intera finestra, e i due estremi
        // cadono allora sullo stesso orario di due giorni diversi: senza il giorno la riga
        // direbbe "not measured for 23 h 45 min (09:25 – 09:10)", cioe' una durata di quasi un
        // giorno accanto a un intervallo che si legge come un quarto d'ora all'indietro.
        // Succede anche a un'ora, su una macchina spenta a cavallo di mezzanotte: per questo la
        // regola guarda la COPPIA e non la soglia della finestra.
        string line = AwaySummary.LineFor("lavoro", [Gap(-755, -710)], withDay: false);

        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", line);

        // E quando i due estremi stanno nella stessa giornata il giorno NON compare: aggiungerlo
        // sempre allungherebbe la frase dove non serve.
        Assert.DoesNotMatch(@"[A-Za-z]{3} \d{2}:\d{2}", AwaySummary.LineFor("lavoro", [Gap(10, 20)], withDay: false));
    }

    [Fact]
    public async Task AHistoryThatCannotBeReadSaysSoInsteadOfStayingSilent()
    {
        // E' il punto in cui questa strada si distingue da un avviso che non compare: quando non
        // si puo' sapere, lo si scrive. Il silenzio resta riservato a "ho chiesto e va tutto
        // bene", e cosi' il silenzio significa qualcosa.
        ObserverEndpoint local = ObserverEndpoint.LocalChannel();
        FailingHistoryClient client = new();

        MainViewModel viewModel = new(
            client,
            configurationProblem: null,
            machineList: new MachineListResult([local], []));

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(25));
        Task loop = viewModel.RunAsync(stop.Token);

        while (!stop.IsCancellationRequested && viewModel.AwaySummaryText.Length == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("history could not be read", viewModel.AwaySummaryText, StringComparison.Ordinal);
        Assert.Contains("persistenza spenta", viewModel.AwaySummaryText, StringComparison.Ordinal);
        Assert.True(viewModel.ShowAwaySummary);

        // Il riepilogo chiede PIU' indietro della finestra che esamina, ed e' la correzione che
        // tiene in piedi tutto il resto: senza quel margine la griglia, ancorata all'ultimo
        // punto, sfora a sinistra e ogni macchina sana apre con "nothing known before".
        Assert.True(
            client.SummaryQueryCount(TimeSpan.FromHours(1), DateTimeOffset.UtcNow) > 0,
            "il riepilogo non ha chiesto oltre la finestra: la griglia sforerebbe a sinistra");

        // Cambiando periodo cambia la domanda, quindi si ricomincia da capo.
        int queriesAtOneHour = client.SummaryQueryCount(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        viewModel.HistoryPeriod = "24h";

        Assert.Equal(string.Empty, viewModel.AwaySummaryText);
        Assert.False(viewModel.ShowAwaySummary);

        while (!stop.IsCancellationRequested
            && client.SummaryQueryCount(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            client.SummaryQueryCount(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) > 0,
            "cambiando periodo il riepilogo non e' stato rifatto sulla finestra nuova");
        Assert.True(queriesAtOneHour > 0, "la prima query non era quella del riepilogo");

        await stop.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    /// <summary>Campiona benissimo, e lo storico non c'e'.</summary>
    private sealed class FailingHistoryClient : IMetricsClient
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<HistoryQuery> queries = [];

        /// <summary>Le sole richieste del RIEPILOGO, riconosciute dal margine che solo lui chiede.</summary>
        /// <remarks>
        /// Contarle tutte non distinguerebbe niente: la striscia interroga la stessa serie a
        /// ogni passo, quindi un contatore unico sale comunque e il test resterebbe verde anche
        /// se il riepilogo non partisse mai. Il riepilogo e' l'unico che guarda PIU' indietro
        /// della finestra, ed e' proprio la correzione che questo test deve inchiodare.
        /// </remarks>
        public int SummaryQueryCount(TimeSpan window, DateTimeOffset now) =>
            queries.Count(q => q.From < now - window - TimeSpan.FromMinutes(1));

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.LocalChannel();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [
                        new MetricSnapshot("cpu", CollectorStatus.Ok, null,
                        [
                            MetricPoint.Measured(CpuCollector.TotalUsageMetricId, null, MetricValue.FromNumber(42d)),
                        ]),
                    ])));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MetricCatalog(
                [
                    new CollectorCatalogEntry("cpu",
                    [
                        new MetricDescriptor(CpuCollector.TotalUsageMetricId, "CPU usage", MetricUnit.Percent, IsPerInstance: false),
                    ]),
                ])));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            queries.Add(query);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.Unreachable, "persistenza spenta", null));
        }
    }
}