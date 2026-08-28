using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Cosa succede quando la configurazione manca all'avvio e compare dopo.
/// </summary>
/// <remarks>
/// E' il caso NORMALE, non un caso limite: il messaggio "Configuration missing" dice
/// all'utente di creare un file. Se creare quel file non produce alcun effetto finche' non
/// riavvia — e il messaggio non glielo dice — l'utente segue le istruzioni alla lettera e
/// conclude che l'applicazione e' rotta. E' successo davvero.
/// </remarks>
public class MainViewModelReconnectTests
{
    [Fact]
    public async Task ConfigurazioneCompareDopoLAvvio_LApplicazioneSiCollegaSenzaRiavvio()
    {
        // All'avvio non c'e' configurazione; alla prima rilettura ne compare una valida.
        FakeMetricsClient client = new();
        int letture = 0;

        MainViewModel viewModel = new(
            client: null,
            problemaDiConfigurazione: "manca il token",
            rileggiConfigurazione: () =>
            {
                letture++;
                return client;
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(10));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        // Attende che il client comparso venga davvero INTERROGATO, senza dipendere da un
        // ritardo fisso: e' la prova che il view model lo ha adottato.
        while (!arresto.IsCancellationRequested && client.Interrogazioni == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(client.Interrogazioni >= 1, "il client comparso deve essere interrogato");
        Assert.True(letture >= 1);

        await arresto.CancelAsync();
        await ciclo.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task TokenRifiutatoSuUnaFinestraGiaCollegata_RileggeLaConfigurazione()
    {
        // Il gemello del caso sopra, e finora scoperto: la rilettura avveniva SOLO all'avvio.
        // Una finestra gia' collegata che riceve 401 — perche' il token e' stato ruotato —
        // restava bloccata su "Token rejected" fino al riavvio, e nessun messaggio lo diceva.
        // E' lo stesso incidente di "Configuration missing", su un altro percorso.
        FakeMetricsClient vecchio = new(
            ObserverEndpoint.Remoto(new Uri("http://vecchia:5057/"), "t", "dalla prova"),
            ServiceOutcome.TokenRifiutato);
        FakeMetricsClient nuovo = new(
            ObserverEndpoint.Remoto(new Uri("http://nuova:9999/"), "t", "dalla prova"),
            ServiceOutcome.NonRaggiungibile);

        MainViewModel viewModel = new(
            vecchio,
            problemaDiConfigurazione: null,
            rileggiConfigurazione: () => nuovo);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && nuovo.Interrogazioni == 0)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(nuovo.Interrogazioni >= 1, "dopo un 401 il client riletto deve essere interrogato");

        await arresto.CancelAsync();
        await ciclo.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task SenzaConfigurazioneESenzaRilettura_IlCicloEsceSubito()
    {
        // Il comportamento precedente resta valido quando non c'e' modo di rileggere:
        // martellare il servizio con richieste destinate al 401 non aiuterebbe nessuno.
        MainViewModel viewModel = new(client: null, problemaDiConfigurazione: "manca il token");

        await viewModel.EseguiAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Observer", viewModel.Intestazione);
    }

    private sealed class FakeMetricsClient(ObserverEndpoint endpoint, ServiceOutcome esito) : IMetricsClient
    {
        public FakeMetricsClient()
            : this(ObserverEndpoint.CanaleLocale(), ServiceOutcome.NonRaggiungibile)
        {
        }

        public ObserverEndpoint Endpoint { get; } = endpoint;

        private int interrogazioni;

        /// <summary>Quante volte questo client e' stato interrogato.</summary>
        /// <remarks>
        /// E' il segnale con cui i test riconoscono che il view model ha ADOTTATO questo
        /// client. Prima guardavano l'intestazione, che conteneva il nome della macchina; ora
        /// l'intestazione e' sempre "Observer", e comunque era un indizio indiretto: diceva
        /// che una stringa era cambiata, non che il client nuovo venisse davvero usato.
        /// </remarks>
        public int Interrogazioni => Volatile.Read(ref interrogazioni);

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref interrogazioni);

            return Task.FromResult(new SnapshotFetch(esito, "no service in this test", null));
        }

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(esito, "no service in this test", null));

        /// <summary>Vero dopo Dispose: rende il metodo non statico e documenta l'esito.</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}