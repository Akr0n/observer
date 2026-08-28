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

    private readonly Func<IMetricsClient?>? rileggiConfigurazione;
    private readonly Func<DateTimeOffset> adesso;

    /// <summary>Come aprire un client verso una macchina scelta nell'elenco.</summary>
    private readonly Func<ObserverEndpoint, IMetricsClient>? apriMacchina;


    private IMetricsClient? client;

    private MetricCatalog catalogo = MetricCatalog.Empty;
    private bool catalogoLetto;

    /// <summary>
    /// Da quando le letture falliscono di fila, oppure null se l'ultima e' andata bene.
    /// </summary>
    /// <remarks>
    /// E' cio' che distingue un servizio che sta partendo da un servizio che non c'e'. Va
    /// azzerato anche quando si cambia endpoint: a una macchina diversa spetta un'attesa
    /// nuova, non quella gia' consumata dalla precedente.
    /// </remarks>
    private DateTimeOffset? guastoDa;

    /// <summary>
    /// Costruisce la schermata.
    /// </summary>
    /// <param name="client">Il client verso il servizio, oppure null se manca la configurazione.</param>
    /// <param name="problemaDiConfigurazione">
    /// La frase da mostrare quando <paramref name="client"/> e' null.
    /// </param>
    /// <param name="rileggiConfigurazione">
    /// Come riprovare a leggere la configurazione mentre l'applicazione e' aperta, oppure
    /// null per non riprovare affatto. Restituisce un client quando la configurazione
    /// diventa valida.
    /// </param>
    /// <param name="orologio">
    /// Da dove si legge l'ora, oppure null per l'orologio di sistema. Serve alle prove:
    /// l'attesa prima di dichiarare guasto un servizio dura dieci secondi, e un test che li
    /// aspettasse davvero sarebbe un test che nessuno esegue volentieri.
    /// </param>
    /// <param name="elenco">
    /// Le macchine da mettere nella barra laterale, oppure null per non mostrarla affatto.
    /// </param>
    /// <param name="apriMacchina">Come aprire un client verso una macchina dell'elenco.</param>
    public MainViewModel(
        IMetricsClient? client,
        string? problemaDiConfigurazione,
        Func<IMetricsClient?>? rileggiConfigurazione = null,
        Func<DateTimeOffset>? orologio = null,
        MachineListResult? elenco = null,
        Func<ObserverEndpoint, IMetricsClient>? apriMacchina = null)
    {
        this.client = client;
        this.rileggiConfigurazione = rileggiConfigurazione;
        this.apriMacchina = apriMacchina;
        adesso = orologio ?? (static () => DateTimeOffset.UtcNow);

        foreach (ObserverEndpoint punto in elenco?.Machines ?? [])
        {
            Macchine.Add(punto);
        }

        foreach (string problema in elenco?.Problems ?? [])
        {
            ProblemiDellElenco.Add(problema);
        }

        // La selezione iniziale segue il client con cui la finestra e' stata costruita. Non
        // serve alcun guardiano contro la propria stessa scrittura: il gestore qui sotto esce
        // da se' quando la macchina scelta e' gia' quella aperta.
        MacchinaSelezionata = Macchine.FirstOrDefault(
            punto => client is not null && punto == client.Endpoint) ?? Macchine.FirstOrDefault();

        Intestazione = client is null
            ? "Observer"
            : $"Observer — {client.Endpoint.Descrizione}";

        if (client is null)
        {
            Mostra(
                FAInfoBarSeverity.Error,
                "Configuration missing",
                problemaDiConfigurazione ?? "The configuration could not be read.");
            SottoIntestazione = "Not connected.";
        }
        else
        {
            Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            SottoIntestazione = "Connecting…";
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

    /// <summary>Le macchine fra cui si puo' scegliere. La prima e' sempre questa.</summary>
    public ObservableCollection<ObserverEndpoint> Macchine { get; } = [];

    /// <summary>Le voci dell'elenco che sono state scartate, e perche'.</summary>
    /// <remarks>
    /// Mostrate accanto all'elenco invece che nascoste in un log: una macchina configurata male
    /// che semplicemente NON COMPARE e' indistinguibile da una macchina che non e' stata
    /// aggiunta, e chi la cerca non ha modo di sapere che cosa correggere.
    /// </remarks>
    public ObservableCollection<string> ProblemiDellElenco { get; } = [];

    /// <summary>
    /// Vero quando l'elenco contiene solo questa macchina e non c'e' niente da correggere.
    /// </summary>
    /// <remarks>
    /// La barra laterale si vede SEMPRE, e prima non era cosi': compariva solo quando c'era
    /// gia' una seconda macchina. Il risultato e' che nessuno poteva scoprire di poterne
    /// aggiungere una, perche' l'unico posto dove la funzione si annuncia e' la funzione
    /// stessa. Una funzione che si mostra solo a chi sa gia' che esiste non esiste.
    /// <para>
    /// Al suo posto, quando c'e' una macchina sola, si spiega come aggiungerne un'altra e si
    /// dice il percorso esatto del file da scrivere.
    /// </para>
    /// </remarks>
    public bool MostraSuggerimento => Macchine.Count <= 1 && ProblemiDellElenco.Count == 0;

    /// <summary>Come si aggiunge una macchina, col percorso del file da scrivere.</summary>
    public string SuggerimentoElenco { get; } =
        "Only this machine so far. To watch another one, run \"observer share\" on it and put " +
        "what it prints into " + MachineDirectory.FilePath;

    /// <summary>La macchina attualmente guardata.</summary>
    [ObservableProperty]
    public partial ObserverEndpoint? MacchinaSelezionata { get; set; }

    /// <summary>Cambia macchina senza riavviare la finestra.</summary>
    /// <param name="value">La macchina scelta nell'elenco.</param>
    partial void OnMacchinaSelezionataChanged(ObserverEndpoint? value)
    {
        if (value is null || apriMacchina is null)
        {
            return;
        }

        if (client is not null && value == client.Endpoint)
        {
            return;
        }

        client = apriMacchina(value);

        // Tutto cio' che descriveva la macchina PRECEDENTE va buttato: il catalogo, perche' le
        // etichette appartengono a quel servizio; i riquadri, perche' sono le sue misure; e
        // l'orologio dei guasti, perche' a una macchina nuova spetta un'attesa nuova.
        catalogoLetto = false;
        catalogo = MetricCatalog.Empty;
        guastoDa = null;
        Gruppi.Clear();

        Intestazione = "Observer - " + value.Descrizione;
        Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading...");
        SottoIntestazione = "Connecting...";
    }

    /// <summary>
    /// Il ciclo di aggiornamento. Non lancia mai: qualunque guasto diventa testo a schermo.
    /// </summary>
    /// <param name="cancellationToken">Annullato alla chiusura dell'applicazione.</param>
    /// <summary>
    /// Riprova a leggere la configurazione finche' non diventa valida.
    /// </summary>
    /// <returns>True se un client e' stato adottato, false se non c'e' modo di riprovare.</returns>
    private async Task<bool> AttendiConfigurazioneAsync(CancellationToken cancellationToken)
    {
        if (rileggiConfigurazione is null)
        {
            return false;
        }

        using PeriodicTimer attesa = new(Intervallo);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await attesa.WaitForNextTickAsync(cancellationToken))
            {
                return false;
            }

            if (rileggiConfigurazione() is not { } comparso)
            {
                continue;
            }

            client = comparso;
            guastoDa = null;
            Intestazione = $"Observer — {comparso.Endpoint.Descrizione}";
            Mostra(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            SottoIntestazione = "Connecting…";
            return true;
        }

        return false;
    }

    public async Task EseguiAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            // Senza token non c'e' niente da interrogare: martellare il servizio con richieste
            // destinate al 401 non aiuta. Ma il messaggio a schermo dice all'utente di creare
            // un file di configurazione, e se crearlo non producesse alcun effetto finche' non
            // riavvia — cosa che il messaggio non dice — l'utente seguirebbe le istruzioni alla
            // lettera e concluderebbe che l'applicazione e' rotta. Quindi si rilegge.
            if (!await AttendiConfigurazioneAsync(cancellationToken))
            {
                return;
            }
        }

        using PeriodicTimer timer = new(Intervallo);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServiceOutcome esito = await AggiornaAsync(cancellationToken);

                // Un 401 su una finestra GIA' collegata significa quasi sempre che il token e'
                // stato ruotato. Senza rileggere qui, la finestra resterebbe bloccata su
                // "Token rejected" fino al riavvio: e' lo stesso incidente di "Configuration
                // missing", su un altro percorso, e va chiuso allo stesso modo.
                // Anche ImprontaNonCorrisponde, e per la stessa ragione: il messaggio dice
                // all'utente di correggere machines.json, e correggerlo deve BASTARE. E' il
                // terzo percorso su cui questo incidente si presenta - dopo "Configuration
                // missing" e "Token rejected" - e chiuderne due su tre non serve a niente.
                if (esito is ServiceOutcome.TokenRifiutato or ServiceOutcome.ImprontaNonCorrisponde)
                {
                    AdottaConfigurazioneAggiornata();
                }

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
                "Updates stopped",
                $"Automatic refresh stopped after an unexpected error ({ex.GetType().Name}: " +
                $"{ex.Message}). The values on screen have stopped updating: close and reopen the application.");
            SottoIntestazione = "Refresh stopped.";
        }
    }

    /// <summary>
    /// Rilegge la configurazione e adotta il client risultante, se e' cambiato.
    /// </summary>
    /// <remarks>
    /// Non chiude il client precedente: chi lo ha costruito ne conserva il riferimento e lo
    /// chiude all'uscita. Chiuderlo qui lo strapperebbe da sotto una richiesta ancora in volo.
    /// </remarks>
    private void AdottaConfigurazioneAggiornata()
    {
        if (rileggiConfigurazione?.Invoke() is not { } ricomparso || ReferenceEquals(ricomparso, client))
        {
            return;
        }

        client = ricomparso;

        // Attesa nuova: l'endpoint e' cambiato, e i secondi gia' consumati contro il
        // precedente non dicono niente su questo.
        guastoDa = null;

        // Il catalogo appartiene al servizio precedente: va riletto, altrimenti le etichette
        // resterebbero quelle di una macchina diversa.
        catalogoLetto = false;
        catalogo = MetricCatalog.Empty;

        Intestazione = $"Observer — {ricomparso.Endpoint.Descrizione}";
    }

    private async Task<ServiceOutcome> AggiornaAsync(CancellationToken cancellationToken)
    {
        if (client is not { } corrente)
        {
            return ServiceOutcome.Unknown;
        }

        // Prima il campionamento e SOLO POI il catalogo. Verificato sperimentalmente: con il
        // servizio spento, chiedere prima il catalogo raddoppia l'attesa — due timeout invece
        // di uno — e la finestra resta a dire "collegamento in corso" per sei secondi prima di
        // ammettere che non si collega.
        SnapshotFetch fetch = await corrente.GetLatestAsync(cancellationToken);

        // Fra la partenza della richiesta e la sua risposta l'utente puo' aver cambiato
        // macchina nella barra laterale. Applicare qui i valori appena arrivati significherebbe
        // mostrare le misure della macchina PRECEDENTE sotto il nome di quella nuova, e
        // riempirne il catalogo con etichette che non sono le sue.
        if (!ReferenceEquals(client, corrente))
        {
            return ServiceOutcome.Unknown;
        }

        if (!fetch.IsOk)
        {
            SegnalaProblema(fetch.Outcome, fetch.Problem, corrente.Endpoint);
            return fetch.Outcome;
        }

        // Il catalogo cambia solo quando cambia il servizio: si legge una volta sola, e si
        // ritenta al giro dopo se non riesce. Va letto PRIMA di disegnare, altrimenti il primo
        // fotogramma mostrerebbe "cpu.usage.total" al posto di "CPU usage". Se non arriva
        // mai, le metriche restano visibili con il loro identificatore grezzo invece di sparire.
        if (!catalogoLetto)
        {
            CatalogFetch catalogFetch = await corrente.GetCatalogAsync(cancellationToken);

            if (catalogFetch.IsOk && ReferenceEquals(client, corrente))
            {
                catalogo = catalogFetch.Catalog!;
                catalogoLetto = true;
            }
        }

        MachineSnapshot snapshot = fetch.Snapshot!;
        Applica(SnapshotProjection.Project(snapshot, catalogo));

        StatoVisibile = false;

        // La serie di guasti e' finita: la prossima ricomincia da capo, e ha diritto alla
        // stessa attesa che ha avuto questa.
        guastoDa = null;

        string ora = snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // Sul canale locale non si nomina alcun token, perche' li' non ne esiste uno:
        // scriverlo manderebbe chi legge a cercare una credenziale che non serve.
        SottoIntestazione = corrente.Endpoint.Kind == EndpointKind.Locale
            ? $"Connected to this machine · last reading at {ora}"
            : $"Connected to {corrente.Endpoint.Descrizione} · last reading at {ora} · " +
              $"token {corrente.Endpoint.Origine}";

        return ServiceOutcome.Ok;
    }

    /// <summary>
    /// Traduce una lettura fallita in cio' che si vede a schermo.
    /// </summary>
    /// <remarks>
    /// La gravita' NON dipende dal singolo tentativo andato male ma da quanto dura la serie:
    /// e' <see cref="StatusEscalation"/> a deciderlo, ed e' li' che sta la tabella provata.
    /// Qui resta solo la misura del tempo e la traduzione in colore.
    /// </remarks>
    private void SegnalaProblema(ServiceOutcome esito, string testo, ObserverEndpoint punto)
    {
        DateTimeOffset ora = adesso();
        guastoDa ??= ora;

        StatusMessage messaggio = StatusEscalation.Per(
            esito,
            testo,
            ora - guastoDa.Value,
            punto,
            valoriGiaMostrati: Gruppi.Count > 0);

        Mostra(Gravita(messaggio.Tone), messaggio.Title, messaggio.Text);
        SottoIntestazione = messaggio.Subheading;
    }

    private static FAInfoBarSeverity Gravita(StatusTone tono) => tono switch
    {
        StatusTone.Informational => FAInfoBarSeverity.Informational,
        StatusTone.Warning => FAInfoBarSeverity.Warning,
        _ => FAInfoBarSeverity.Error,
    };

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