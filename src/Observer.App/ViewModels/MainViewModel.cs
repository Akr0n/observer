using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.Core.Metrics;

namespace Observer.App.ViewModels;

/// <summary>
/// L'unica schermata: interroga il servizio una volta al secondo e mostra cio' che risponde.
/// </summary>
/// <remarks>
/// Regola non negoziabile di questa classe: non lascia MAI la finestra vuota e non lascia mai
/// uscire un'eccezione. Chi usa questa applicazione non legge i log, quindi ogni guasto deve
/// diventare una frase in italiano dentro la barra di stato.
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan Intervallo = TimeSpan.FromSeconds(1);

    private readonly IMetricsClient? client;

    private MetricCatalog catalogo = MetricCatalog.Empty;
    private bool catalogoLetto;

    /// <summary>
    /// Costruisce la schermata.
    /// </summary>
    /// <param name="client">Il client verso il servizio, oppure null se manca la configurazione.</param>
    /// <param name="problemaDiConfigurazione">
    /// La frase da mostrare quando <paramref name="client"/> e' null.
    /// </param>
    public MainViewModel(IMetricsClient? client, string? problemaDiConfigurazione)
    {
        this.client = client;

        Intestazione = client is null
            ? "Observer"
            : $"Observer — {client.BaseAddress}";

        if (client is null)
        {
            Mostra(
                FAInfoBarSeverity.Error,
                "Manca la configurazione",
                problemaDiConfigurazione ?? ClientConfiguration.TestoTokenMancante());
            SottoIntestazione = "Non collegato.";
        }
        else
        {
            Mostra(FAInfoBarSeverity.Informational, "Collegamento in corso", "Prima lettura in corso…");
            SottoIntestazione = "Collegamento in corso…";
        }
    }

    /// <summary>Titolo grande in cima alla finestra.</summary>
    [ObservableProperty]
    public partial string Intestazione { get; set; }

    /// <summary>Riga sotto il titolo: stato del collegamento e ora dell'ultima lettura.</summary>
    [ObservableProperty]
    public partial string SottoIntestazione { get; set; }

    /// <summary>Titolo della barra di stato.</summary>
    [ObservableProperty]
    public partial string StatoTitolo { get; set; } = string.Empty;

    /// <summary>Testo della barra di stato.</summary>
    [ObservableProperty]
    public partial string StatoMessaggio { get; set; } = string.Empty;

    /// <summary>Gravita' della barra di stato.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity StatoGravita { get; set; } = FAInfoBarSeverity.Informational;

    /// <summary>True quando c'e' qualcosa da segnalare. Quando tutto va, la barra sparisce.</summary>
    [ObservableProperty]
    public partial bool StatoVisibile { get; set; } = true;

    /// <summary>I riquadri, uno per sorgente di metriche.</summary>
    public ObservableCollection<MetricGroup> Gruppi { get; } = [];

    /// <summary>
    /// Il ciclo di aggiornamento. Non lancia mai: qualunque guasto diventa testo a schermo.
    /// </summary>
    /// <param name="cancellationToken">Annullato alla chiusura dell'applicazione.</param>
    public async Task EseguiAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            // Senza token non c'e' niente da interrogare: il messaggio e' gia' a schermo dal
            // costruttore, e martellare il servizio con richieste destinate al 401 non aiuta.
            return;
        }

        using PeriodicTimer timer = new(Intervallo);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await AggiornaAsync(cancellationToken);

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura dell'applicazione: uscita normale, non un errore da mostrare.
        }
#pragma warning disable CA1031 // Questo ciclo e' avviato senza nessuno che ne attenda l'esito:
        catch (Exception ex) // un'eccezione qui sparirebbe in silenzio e la finestra si
#pragma warning restore CA1031 // congelerebbe senza dire niente. Va mostrata, non propagata.
        {
            Mostra(
                FAInfoBarSeverity.Error,
                "Aggiornamento interrotto",
                $"L'aggiornamento automatico si e' fermato per un errore imprevisto ({ex.GetType().Name}: " +
                $"{ex.Message}). I valori a schermo non sono piu' aggiornati: chiudi e riapri l'applicazione.");
            SottoIntestazione = "Aggiornamento fermo.";
        }
    }

    private async Task AggiornaAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            return;
        }

        // Prima il campionamento e SOLO POI il catalogo. Verificato sperimentalmente: con il
        // servizio spento, chiedere prima il catalogo raddoppia l'attesa — due timeout invece
        // di uno — e la finestra resta a dire "collegamento in corso" per sei secondi prima di
        // ammettere che non si collega.
        SnapshotFetch fetch = await client.GetLatestAsync(cancellationToken);

        if (!fetch.IsOk)
        {
            SegnalaProblema(fetch.Outcome, fetch.Problem);
            return;
        }

        // Il catalogo cambia solo quando cambia il servizio: si legge una volta sola, e si
        // ritenta al giro dopo se non riesce. Va letto PRIMA di disegnare, altrimenti il primo
        // fotogramma mostrerebbe "cpu.usage.total" al posto di "Utilizzo CPU". Se non arriva
        // mai, le metriche restano visibili con il loro identificatore grezzo invece di sparire.
        if (!catalogoLetto)
        {
            CatalogFetch catalogFetch = await client.GetCatalogAsync(cancellationToken);

            if (catalogFetch.IsOk)
            {
                catalogo = catalogFetch.Catalog!;
                catalogoLetto = true;
            }
        }

        MachineSnapshot snapshot = fetch.Snapshot!;
        Applica(SnapshotProjection.Project(snapshot, catalogo));

        StatoVisibile = false;

        string ora = snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        SottoIntestazione = $"Collegato · ultima lettura alle {ora} · token {client.TokenOrigin}";
    }

    private void SegnalaProblema(ServiceOutcome esito, string testo)
    {
        (FAInfoBarSeverity gravita, string titolo) = esito switch
        {
            ServiceOutcome.NonAncoraPronto => (FAInfoBarSeverity.Informational, "Il servizio si sta avviando"),
            ServiceOutcome.TokenRifiutato => (FAInfoBarSeverity.Error, "Token rifiutato"),
            ServiceOutcome.NonRaggiungibile => (FAInfoBarSeverity.Error, "Servizio non raggiungibile"),
            ServiceOutcome.VersioneIncompatibile => (FAInfoBarSeverity.Error, "Versioni incompatibili"),
            ServiceOutcome.RispostaIncomprensibile => (FAInfoBarSeverity.Error, "Risposta non riconosciuta"),
            _ => (FAInfoBarSeverity.Error, "Lettura non riuscita"),
        };

        Mostra(gravita, titolo, testo);

        // I valori restano a schermo apposta: cancellarli farebbe credere che la macchina
        // abbia smesso di avere una CPU. La riga qui sotto dice che sono fermi.
        SottoIntestazione = Gruppi.Count == 0
            ? "Non collegato."
            : "Non collegato: i valori mostrati sono l'ultima lettura riuscita.";
    }

    private void Mostra(FAInfoBarSeverity gravita, string titolo, string messaggio)
    {
        StatoGravita = gravita;
        StatoTitolo = titolo;
        StatoMessaggio = messaggio;
        StatoVisibile = true;
    }

    private void Applica(IReadOnlyList<MetricGroupState> stati)
    {
        if (!StessiCollector(stati))
        {
            Gruppi.Clear();

            foreach (MetricGroupState stato in stati)
            {
                Gruppi.Add(new MetricGroup(stato));
            }

            return;
        }

        for (int i = 0; i < stati.Count; i++)
        {
            Gruppi[i].Aggiorna(stati[i]);
        }
    }

    private bool StessiCollector(IReadOnlyList<MetricGroupState> stati)
    {
        if (Gruppi.Count != stati.Count)
        {
            return false;
        }

        for (int i = 0; i < stati.Count; i++)
        {
            if (!string.Equals(Gruppi[i].CollectorId, stati[i].CollectorId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
