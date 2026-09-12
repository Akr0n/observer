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
public class RiepilogoTests
{
    /// <remarks>
    /// Mezzogiorno LOCALE, non UTC: la frase mostra l'ora della macchina di chi guarda (come
    /// <c>HistoryStrip.Descrivi</c>), quindi un istante UTC renderebbe il test dipendente dal
    /// fuso di chi lo esegue - verde qui e rosso sul runner, o viceversa.
    /// </remarks>
    private static readonly DateTimeOffset Mezzogiorno = new(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Local));

    private static Assenza Vuoto(int daMinuto, int aMinuto, bool dalBordo = false) =>
        new(Mezzogiorno.AddMinutes(daMinuto), Mezzogiorno.AddMinutes(aMinuto), dalBordo);

    [Fact]
    public void NienteDaDireNonEUnaRiga()
    {
        // Il silenzio e' un risultato: "ho chiesto e non c'era niente". Una riga "all good" per
        // ogni macchina sana riempirebbe il riquadro proprio nel caso in cui non serve, e lo si
        // imparerebbe a chiudere senza leggerlo.
        Assert.Equal(string.Empty, Riepilogo.Riga("lavoro", [], colGiorno: false));
    }

    [Fact]
    public void UnaSolaInterruzioneDiceQuantoEQuando()
    {
        string riga = Riepilogo.Riga("lavoro", [Vuoto(20, 200)], colGiorno: false);

        Assert.Equal("lavoro: not measured for 3 h (12:20 – 15:20)", riga);

        // Con una sola, "in 1 period" sarebbe rumore: il totale E' quella.
        Assert.DoesNotContain("period", riga, StringComparison.Ordinal);
    }

    [Fact]
    public void PiuInterruzioniDiconoQuanteSonoEQualEraLaPiuLunga()
    {
        // Il totale da solo mentirebbe per omissione: tre ore in un colpo e tre ore in dieci
        // singhiozzi sono due macchine diverse, e la piu' lunga e' quella che decide se alzarsi
        // dalla sedia.
        string riga = Riepilogo.Riga("lavoro", [Vuoto(10, 20), Vuoto(60, 240), Vuoto(300, 310)], colGiorno: false);

        Assert.Contains("in 3 periods", riga, StringComparison.Ordinal);
        Assert.Contains("longest 13:00 – 16:00", riga, StringComparison.Ordinal);
        Assert.Contains("3 h 20 min", riga, StringComparison.Ordinal);
    }

    [Fact]
    public void IlVuotoSulBordoNonSiConteggiaFraLeInterruzioni()
    {
        // La ritenzione cancella un PREFISSO, indistinguibile da una macchina accesa a meta'
        // finestra: chiamarlo interruzione sarebbe inventare, e una frase inventata insegna a
        // non fidarsi delle altre.
        string solo = Riepilogo.Riga("casa", [Vuoto(0, 45, dalBordo: true)], colGiorno: false);

        Assert.Equal("casa: nothing known before 12:45", solo);
        Assert.DoesNotContain("not measured", solo, StringComparison.Ordinal);

        // E quando c'e' anche un'interruzione vera, il bordo resta una nota in coda e NON entra
        // nel totale: venti minuti, non sessantacinque.
        string insieme = Riepilogo.Riga("casa", [Vuoto(0, 45, dalBordo: true), Vuoto(60, 80)], colGiorno: false);

        Assert.Contains("not measured for 20 min", insieme, StringComparison.Ordinal);
        Assert.Contains("nothing known before 12:45", insieme, StringComparison.Ordinal);
        Assert.DoesNotContain("periods", insieme, StringComparison.Ordinal);
    }

    [Fact]
    public void OltreLaGiornataGliIstantiPortanoIlGiorno()
    {
        // Stessa soglia e stessa ragione di HistoryStrip.Descrivi: a sette giorni "14:20" puo'
        // essere uno qualunque di sette pomeriggi.
        string senza = Riepilogo.Riga("lavoro", [Vuoto(20, 200)], colGiorno: false);
        string con = Riepilogo.Riga("lavoro", [Vuoto(20, 200)], colGiorno: true);

        Assert.Matches(@"\(\d{2}:\d{2} – \d{2}:\d{2}\)", senza);
        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", con);
    }

    [Fact]
    public void UnInterruzioneCheAttraversaLaMezzanotteNonSiLeggeAllIndietro()
    {
        // A ventiquattro ore un'assenza puo' durare quasi l'intera finestra, e i due estremi
        // cadono allora sullo stesso orario di due giorni diversi: senza il giorno la riga
        // direbbe "not measured for 23 h 45 min (09:25 – 09:10)", cioe' una durata di quasi un
        // giorno accanto a un intervallo che si legge come un quarto d'ora all'indietro.
        // Succede anche a un'ora, su una macchina spenta a cavallo di mezzanotte: per questo la
        // regola guarda la COPPIA e non la soglia della finestra.
        string riga = Riepilogo.Riga("lavoro", [Vuoto(-755, -710)], colGiorno: false);

        Assert.Matches(@"\([A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2}\)", riga);

        // E quando i due estremi stanno nella stessa giornata il giorno NON compare: aggiungerlo
        // sempre allungherebbe la frase dove non serve.
        Assert.DoesNotMatch(@"[A-Za-z]{3} \d{2}:\d{2}", Riepilogo.Riga("lavoro", [Vuoto(10, 20)], colGiorno: false));
    }

    [Fact]
    public async Task UnoStoricoCheNonSiPuoLeggereLoDiceInveceDiTacere()
    {
        // E' il punto in cui questa strada si distingue da un avviso che non compare: quando non
        // si puo' sapere, lo si scrive. Il silenzio resta riservato a "ho chiesto e va tutto
        // bene", e cosi' il silenzio significa qualcosa.
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ClientConStoricoGuasto cliente = new();

        MainViewModel viewModel = new(
            cliente,
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale], []));

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(25));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.RiepilogoAssenze.Length == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("history could not be read", viewModel.RiepilogoAssenze, StringComparison.Ordinal);
        Assert.Contains("persistenza spenta", viewModel.RiepilogoAssenze, StringComparison.Ordinal);
        Assert.True(viewModel.MostraRiepilogo);

        // Il riepilogo chiede PIU' indietro della finestra che esamina, ed e' la correzione che
        // tiene in piedi tutto il resto: senza quel margine la griglia, ancorata all'ultimo
        // punto, sfora a sinistra e ogni macchina sana apre con "nothing known before".
        Assert.True(
            cliente.Riepiloghi(TimeSpan.FromHours(1), DateTimeOffset.UtcNow) > 0,
            "il riepilogo non ha chiesto oltre la finestra: la griglia sforerebbe a sinistra");

        // Cambiando periodo cambia la domanda, quindi si ricomincia da capo.
        int aUnOra = cliente.Riepiloghi(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        viewModel.Periodo = "24h";

        Assert.Equal(string.Empty, viewModel.RiepilogoAssenze);
        Assert.False(viewModel.MostraRiepilogo);

        while (!arresto.IsCancellationRequested
            && cliente.Riepiloghi(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            cliente.Riepiloghi(TimeSpan.FromHours(24), DateTimeOffset.UtcNow) > 0,
            "cambiando periodo il riepilogo non e' stato rifatto sulla finestra nuova");
        Assert.True(aUnOra > 0, "la prima richiesta non era quella del riepilogo");

        await arresto.CancelAsync();

        try
        {
            await ciclo;
        }
        catch (OperationCanceledException)
        {
            // Fine del test.
        }
    }

    /// <summary>Campiona benissimo, e lo storico non c'e'.</summary>
    private sealed class ClientConStoricoGuasto : IMetricsClient
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<HistoryQuery> chieste = [];

        /// <summary>Le sole richieste del RIEPILOGO, riconosciute dal margine che solo lui chiede.</summary>
        /// <remarks>
        /// Contarle tutte non distinguerebbe niente: la striscia interroga la stessa serie a
        /// ogni passo, quindi un contatore unico sale comunque e il test resterebbe verde anche
        /// se il riepilogo non partisse mai. Il riepilogo e' l'unico che guarda PIU' indietro
        /// della finestra, ed e' proprio la correzione che questo test deve inchiodare.
        /// </remarks>
        public int Riepiloghi(TimeSpan finestra, DateTimeOffset adesso) =>
            chieste.Count(q => q.Da < adesso - finestra - TimeSpan.FromMinutes(1));

        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.CanaleLocale();

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

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken)
        {
            chieste.Add(richiesta);

            return Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "persistenza spenta", null));
        }
    }
}