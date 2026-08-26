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

        Assert.Equal("Observer", viewModel.Intestazione);

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(10));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        // Attende che il view model adotti il client comparso, senza dipendere da un ritardo fisso.
        while (!arresto.IsCancellationRequested
            && !viewModel.Intestazione.Contains("localhost", StringComparison.Ordinal))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Contains("localhost", viewModel.Intestazione, StringComparison.Ordinal);
        Assert.True(letture >= 1);

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

    private sealed class FakeMetricsClient : IMetricsClient
    {
        public Uri BaseAddress { get; } = new("http://localhost:5057/");

        public string TokenOrigin => "from the test";

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "no service in this test", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "no service in this test", null));

        /// <summary>Vero dopo Dispose: rende il metodo non statico e documenta l'esito.</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
