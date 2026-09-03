using Observer.App.Services;
using Observer.App.ViewModels;
using Observer.Core.Metrics;

namespace Observer.App.Tests;

/// <summary>
/// Il pallino accanto a ogni macchina: cosa dice, e chi lo aggiorna.
/// </summary>
/// <remarks>
/// Due regole. La prima: il colore segue la stessa regola della barra di stato, con la sua
/// grazia di dieci secondi, cosi' un pallino rosso e una barra rossa vogliono dire la stessa
/// cosa. La seconda: le macchine che non si stanno guardando vengono sondate da sole, senza
/// che nessuno ci clicchi sopra e senza che il giro dei quadranti le aspetti.
/// </remarks>
public class StatoMacchineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ObserverEndpoint Remota(string nome) =>
        ObserverEndpoint.Remoto(new Uri($"https://{nome}:5058/"), "token", nome, new string('a', 64));

    [Fact]
    public void AllInizioNessunaMacchinaHaUnoStato()
    {
        MacchinaInElenco voce = new(Remota("altra"));

        Assert.True(voce.Ignoto);
        Assert.False(voce.Raggiungibile);
        Assert.Equal("Not checked yet", voce.Dettaglio);
    }

    [Fact]
    public void UnaRispostaBuonaSegnaRaggiungibile()
    {
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.Ok, string.Empty, T0);

        Assert.True(voce.Raggiungibile);
        Assert.Equal("Reachable", voce.Dettaglio);
        Assert.Contains("Reachable", voce.Descrizione, StringComparison.Ordinal);
    }

    [Fact]
    public void UnRifiutoRecenteEAttenzioneEDopoLaGraziaEGuasto()
    {
        // La stessa regola della barra di stato: un servizio che sta ancora partendo rifiuta,
        // e per dieci secondi e' normale. Dopo, no.
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0);
        Assert.True(voce.Attenzione);

        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + StatusEscalation.Tolleranza + TimeSpan.FromSeconds(1));
        Assert.True(voce.Guasto);

        // E una risposta buona azzera la serie: il guasto successivo ricomincia da capo.
        voce.Registra(ServiceOutcome.Ok, string.Empty, T0 + TimeSpan.FromMinutes(1));
        voce.Registra(ServiceOutcome.ConnessioneRifiutata, "refused", T0 + TimeSpan.FromMinutes(1));
        Assert.True(voce.Attenzione);
    }

    [Fact]
    public void UnTokenRifiutatoEGuastoDaSubito()
    {
        // Fra un minuto sara' identico: non c'e' grazia che tenga.
        MacchinaInElenco voce = new(Remota("altra"));

        voce.Registra(ServiceOutcome.TokenRifiutato, "rejected", T0);

        Assert.True(voce.Guasto);
    }

    [Fact]
    public async Task LeMacchineNonGuardateVengonoSondateDaSole()
    {
        ObserverEndpoint locale = ObserverEndpoint.CanaleLocale();
        ObserverEndpoint viva = Remota("viva");
        ObserverEndpoint spenta = Remota("spenta");

        List<ObserverEndpoint> aperte = [];

        MainViewModel viewModel = new(
            client: new ClientCheRisponde(locale),
            problemaDiConfigurazione: null,
            elenco: new MachineListResult([locale, viva, spenta], []),
            apriMacchina: punto =>
            {
                aperte.Add(punto);

                return punto == viva ? new ClientCheRisponde(punto) : new ClientMuto(punto);
            });

        using CancellationTokenSource arresto = new(TimeSpan.FromSeconds(15));
        Task ciclo = viewModel.EseguiAsync(arresto.Token);

        while (!arresto.IsCancellationRequested && viewModel.Macchine.Any(voce => voce.Ignoto))
        {
            await Task.Delay(50, CancellationToken.None);
        }

        // La macchina guardata segue il giro principale; le altre due la sonda.
        Assert.True(viewModel.Macchine[0].Raggiungibile);
        Assert.True(viewModel.Macchine[1].Raggiungibile);
        Assert.True(viewModel.Macchine[2].Attenzione, viewModel.Macchine[2].Dettaglio);

        // La sonda NON apre un client verso la macchina che si sta gia' guardando.
        Assert.DoesNotContain(locale, aperte);
        Assert.Contains(viva, aperte);
        Assert.Contains(spenta, aperte);

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

    private sealed class ClientCheRisponde(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(
                ServiceOutcome.Ok,
                string.Empty,
                new MachineSnapshot(
                    MachineSnapshot.CurrentSchemaVersion,
                    DateTimeOffset.UnixEpoch,
                    [new MetricSnapshot("cpu", CollectorStatus.Ok, null, [])])));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.Ok, string.Empty, MetricCatalog.Empty));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.Ok, string.Empty, []));
    }

    private sealed class ClientMuto(ObserverEndpoint endpoint) : IMetricsClient
    {
        public ObserverEndpoint Endpoint { get; } = endpoint;

        public Task<SnapshotFetch> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<CatalogFetch> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));

        public Task<HistoryFetch> GetHistoryAsync(HistoryQuery richiesta, CancellationToken cancellationToken) =>
            Task.FromResult(new HistoryFetch(ServiceOutcome.NonRaggiungibile, "spenta", null));
    }
}