using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Che l'attesa provata in <see cref="StatusEscalationTests"/> sia davvero cablata nel ciclo.
/// </summary>
/// <remarks>
/// Senza questa classe la tabella dell'escalation potrebbe essere perfetta e la finestra
/// continuare ad aprirsi rossa: basterebbe che il view model passasse sempre zero come durata,
/// e nessun test puro se ne accorgerebbe. Qui si guarda cio' che si vede a schermo.
/// </remarks>
public class MainViewModelStatusTests
{
    [Fact]
    public async Task LaFinestraCheSiApreMentreIlServizioParte_NonMostraUnaBarraRossa()
    {
        // Il difetto misurato, riprodotto: il servizio non risponde ancora, e la finestra
        // e' appena stata aperta.
        OrologioFinto orologio = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.NonRaggiungibile, orologio);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        await Attendi(viewModel, "Waiting for", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Informational, viewModel.StatoGravita);
        Assert.Equal("Connecting", viewModel.StatoTitolo);

        await Chiudi(arresto, ciclo);
    }

    [Fact]
    public async Task SeIlServizioNonRispondeAncoraDopoLaTolleranza_LaBarraDiventaRossa()
    {
        // Il gemello obbligatorio del test sopra: rimandare l'allarme non deve significare
        // sopprimerlo. Un servizio che non c'e' va detto, e va detto in rosso.
        OrologioFinto orologio = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.NonRaggiungibile, orologio);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        await Attendi(viewModel, "Waiting for", arresto.Token);

        orologio.Avanza(StatusEscalation.Tolleranza + TimeSpan.FromSeconds(1));

        await Attendi(viewModel, "isn't answering", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatoGravita);
        Assert.Equal("Service unreachable", viewModel.StatoTitolo);

        await Chiudi(arresto, ciclo);
    }

    [Fact]
    public async Task UnTokenRifiutatoNonAspetta_ERossoDalPrimoTentativo()
    {
        // L'attesa vale solo dove aspettare puo' cambiare l'esito. Un token sbagliato sara'
        // sbagliato anche fra dieci secondi: rimandare l'allarme rimanderebbe solo il momento
        // in cui l'utente puo' correggerlo.
        OrologioFinto orologio = new();
        MainViewModel viewModel = Costruisci(ServiceOutcome.TokenRifiutato, orologio);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        await Attendi(viewModel, "rejected the token", arresto.Token);

        Assert.Equal(FAInfoBarSeverity.Error, viewModel.StatoGravita);

        await Chiudi(arresto, ciclo);
    }

    private static MainViewModel Costruisci(ServiceOutcome esito, OrologioFinto orologio) =>
        new(
            new FakeMetricsClient(esito),
            problemaDiConfigurazione: null,
            rileggiConfigurazione: null,
            orologio: orologio.Adesso);

    private static async Task Attendi(MainViewModel viewModel, string atteso, CancellationToken arresto)
    {
        while (!arresto.IsCancellationRequested
            && !viewModel.StatoMessaggio.Contains(atteso, StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains(atteso, viewModel.StatoMessaggio, StringComparison.Ordinal);
    }

    private static async Task Chiudi(CancellationTokenSource arresto, Task ciclo)
    {
        await arresto.CancelAsync();
        await ciclo.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    /// <summary>
    /// Un orologio che si sposta a comando: l'attesa dura dieci secondi, e un test che li
    /// aspettasse davvero sarebbe un test che nessuno esegue volentieri.
    /// </summary>
    /// <remarks>
    /// I tick stanno in un <c>long</c> letto e scritto con <see cref="Volatile"/>: il ciclo di
    /// aggiornamento gira su un thread del pool, il test avanza l'orologio dal proprio, e una
    /// struttura da sedici byte si potrebbe leggere a meta' scrittura.
    /// </remarks>
    private sealed class OrologioFinto
    {
        private long istante = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public DateTimeOffset Adesso() => new(Volatile.Read(ref istante), TimeSpan.Zero);

        public void Avanza(TimeSpan quanto) =>
            Volatile.Write(ref istante, Volatile.Read(ref istante) + quanto.Ticks);
    }

    /// <summary>Un client che fallisce sempre allo stesso modo.</summary>
    private sealed class FakeMetricsClient(ServiceOutcome esito) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = ObserverEndpoint.CanaleLocale();

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(esito, Testo(esito), null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(esito, Testo(esito), null));
        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(esito, Testo(esito), null));

        // Ricalca le frasi vere di MetricsClient: i test aspettano cio' che si vede a schermo,
        // e una frase inventata qui renderebbe l'attesa una tautologia.
        private static string Testo(ServiceOutcome esito) => esito switch
        {
            ServiceOutcome.TokenRifiutato =>
                "The service on this machine rejected the token (401).",
            _ =>
                "The Observer service isn't answering on this machine. Check that it is running.",
        };
    }
}