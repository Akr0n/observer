using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Observer.App.Services;
using Observer.Core.Metrics;
using Observer.Core.Metrics.Cpu;

namespace Observer.App.ViewModels;

/// <summary>
/// L'unica schermata: interroga il servizio una volta al secondo e mostra cio' che risponde.
/// </summary>
/// <remarks>
/// Regola non negoziabile di questa classe: non lascia MAI la finestra vuota e non lascia mai
/// uscire un'eccezione. Chi usa questa applicazione non legge i log, quindi ogni guasto deve
/// diventare una frase in italiano dentro la barra di state.
/// </remarks>
public sealed partial class MainViewModel : ViewModelBase
{
    /// <summary>Ogni quanto si interroga il servizio.</summary>
    /// <remarks>
    /// Pubblico perche' un test possa confrontarlo con <see cref="Controls.Gauge.NeedleTravelTime"/>: la
    /// corsa della lancetta deve restare piu' breve di questo, altrimenti non finirebbe mai e
    /// il quadrante non starebbe fermo su un valore misurato nemmeno by un istante.
    /// </remarks>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Ogni quanto si interroga il servizio quando la finestra e' ridotta a icona.</summary>
    /// <remarks>
    /// Non si ferma: riaprendo la finestra la barra di state deve dire subito com'e' andata,
    /// non "collegamento in corso". Ma un campione al secondo by una finestra che nessuno
    /// guarda e' lavoro fatto alla macchina che si sta misurando, e questo e' uno strumento
    /// che rientra nel numero che mostra.
    /// </remarks>
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(10);

    /// <summary>Ogni quanto si riprova una lettura di history fallita.</summary>
    /// <remarks>
    /// Non il passo del period: a sette giorni quello vale due ore, e un timeout lascerebbe
    /// accanto alla striscia un "No history" vecchio di due ore su dati che intanto sono
    /// tornati. Si riprova presto, e si rallenta solo quando e' andata bene.
    /// </remarks>
    private static readonly TimeSpan HistoryRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>In quante riletture si divide un passo, quando la lettura e' andata bene.</summary>
    /// <remarks>
    /// Rileggere OGNI passo sembrava la readInterval giusta - piu' spesso non aggiunge una barra,
    /// aggiunge solo traffico - e non lo era: l'ultima barra della striscia e' l'intervallo IN
    /// CORSO, e da 0.18.0 si disegna larga quanto la parte che ha coperto. Rileggendo ogni
    /// passo si guarda ogni volta una barra appena nata, sempre alla stessa frazione: a sette
    /// giorni l'estremo destro - quello che l'occhio legge come "clock" - resterebbe una row
    /// da un pixel by tutta la sessione, accanto a quadranti vivi. Un quarto del passo la fa
    /// crescere in quattro scatti, e resta un trentesimo del traffico della vista da un'timeText.
    /// </remarks>
    private const int RereadsPerStep = 4;

    /// <summary>Il minimo fra due riletture dello history, quale che sia il period.</summary>
    /// <remarks>
    /// Tocca solo la vista da un'timeText, il cui passo vale gia' un minuto: li' la barra in corso
    /// resta congelata alla frazione che aveva quando si e' process il period, e si accetta.
    /// Scenderebbe a quindici secondi, ma sono dodici richieste ogni quindici secondi - una
    /// volta e mezza il campionamento stesso - by animare una barretta da tredici pixel. Il
    /// prezzo lo paga la macchina che questa finestra sta misurando, e compare nel numero che
    /// la finestra mostra. Sui periodi lunghi il quarto di passo costa molto meno di cosi' e
    /// il difetto e' molto piu' grosso: e' li' che si spende.
    /// </remarks>
    private static readonly TimeSpan MinimumRereadInterval = TimeSpan.FromMinutes(1);

    /// <summary>Il minimo da cui rileggere il grezzo, quale che sia il period.</summary>
    /// <remarks>
    /// Il consolidamento degli aggregati ha una grazia di quattro minuti: il livello a un
    /// minuto e' indietro di cinque o sei rispetto ad clock. Senza questa seconda lettura le
    /// ultime barrette sarebbero SEMPRE vuote, e la striscia direbbe "non misurato" proprio
    /// sull'clock, mentre il quadrante sopra mostra un valore vivo. Con una sorgente a cinque
    /// minuti il ritardo cresce, e la tail si allarga con lei: vedi TailFor.
    /// </remarks>
    private static readonly TimeSpan MinimumTail = TimeSpan.FromMinutes(10);

    /// <summary>Quante rows chiedere al pannello dei processi.</summary>
    /// <remarks>
    /// Quindici, non tutti: la domanda a cui il pannello risponde e' "chi mi sta mangiando la
    /// macchina", e la tail dell'machineList - centinaia di processi fermi - non risponde a niente
    /// e costa banda a ogni secondo.
    /// </remarks>
    private const int ProcessRowCount = 15;

    private readonly Func<IMetricsClient?>? rereadConfiguration;
    private readonly Func<DateTimeOffset> clock;

    /// <summary>Come aprire un client verso una macchina scelta nell'machineList.</summary>
    private readonly Func<ObserverEndpoint, IMetricsClient>? openMachine;

    /// <summary>Come rileggere da disco la machine di una macchina, quando la sua credenziale non vale piu'.</summary>
    private readonly Func<ObserverEndpoint, ObserverEndpoint?>? rereadEndpoint;

    private readonly Func<string, Task>? copyToClipboard;

    /// <summary>L'ultima scrittura negli appunti, by metterci in fila la prossima.</summary>
    private Task clipboardQueue = Task.CompletedTask;

    /// <summary>La machine dell'machineList che il giro principale sta leggendo davvero.</summary>
    /// <remarks>
    /// NON la selezione della lista: quella puo' diventare null (un Ctrl+clic sulla machine
    /// evidenziata la deseleziona) mentre il giro continua a leggere la stessa macchina, e
    /// allora la sonda la interrogherebbe una seconda volta e il entryClient pallino smetterebbe di
    /// seguire la barra. E' questa machine che le sonde saltano e che la barra aggiorna.
    /// </remarks>
    private MachineRow? watchedEntry;


    private IMetricsClient? client;

    private MetricCatalog catalog = MetricCatalog.Empty;
    private bool catalogLoaded;

    /// <summary>
    /// From quando le readings falliscono di fila, oppure null se l'ultima e' andata bene.
    /// </summary>
    /// <remarks>
    /// E' cio' che distingue un servizio che sta partendo da un servizio che non c'e'. Va
    /// azzerato anche quando si cambia endpoint: a una macchina diversa spetta un'timer
    /// nuova, non quella gia' consumata dalla previousWrite.
    /// </remarks>
    private DateTimeOffset? faultSince;

    /// <summary>
    /// Costruisce la schermata.
    /// </summary>
    /// <param name="client">Il client verso il servizio, oppure null se manca la configurazione.</param>
    /// <param name="configurationProblem">
    /// La frase da mostrare quando <paramref name="client"/> e' null.
    /// </param>
    /// <param name="rereadConfiguration">
    /// Come riprovare a leggere la configurazione mentre l'applicazione e' aperta, oppure
    /// null by non riprovare affatto. Restituisce un client quando la configurazione
    /// diventa validScale.
    /// </param>
    /// <param name="clock">
    /// From dove si legge l'timeText, oppure null by l'clock di sistema. Serve alle prove:
    /// l'timer prima di dichiarare guasto un servizio dura dieci secondi, e un test che li
    /// aspettasse davvero sarebbe un test che nessuno esegue volentieri.
    /// </param>
    /// <param name="machineList">
    /// Le macchine da mettere nella barra laterale, oppure null by non mostrarla affatto.
    /// </param>
    /// <param name="openMachine">Come aprire un client verso una macchina dell'machineList.</param>
    /// <param name="rereadEndpoint">
    /// Come rileggere da disco la machine di una macchina non guardata quando una sonda torna
    /// con un token rifiutato o un'impronta che non corrisponde, oppure null by non rileggere.
    /// </param>
    /// <param name="copyToClipboard">
    /// Come scrivere negli appunti, oppure null: senza, i comandi di copy restano spenti.
    /// </param>
    public MainViewModel(
        IMetricsClient? client,
        string? configurationProblem,
        Func<IMetricsClient?>? rereadConfiguration = null,
        Func<DateTimeOffset>? clock = null,
        MachineListResult? machineList = null,
        Func<ObserverEndpoint, IMetricsClient>? openMachine = null,
        Func<ObserverEndpoint, ObserverEndpoint?>? rereadEndpoint = null,
        Func<string, Task>? copyToClipboard = null)
    {
        this.copyToClipboard = copyToClipboard;
        this.client = client;
        this.rereadConfiguration = rereadConfiguration;
        this.openMachine = openMachine;
        this.rereadEndpoint = rereadEndpoint;
        this.clock = clock ?? (static () => DateTimeOffset.UtcNow);

        foreach (ObserverEndpoint endpoint in machineList?.Machines ?? [])
        {
            Machines.Add(new MachineRow(endpoint));
        }

        foreach (string problem in machineList?.Problems ?? [])
        {
            MachineListProblems.Add(problem);
        }

        // La selezione iniziale segue il client con cui la finestra e' stata costruita. Non
        // serve alcun guardiano contro la propria stessa scrittura: il gestore qui sotto esce
        // da se' quando la macchina scelta e' gia' quella aperta.
        SelectedMachine = Machines.FirstOrDefault(
            machine => client is not null && machine.Endpoint == client.Endpoint) ?? Machines.FirstOrDefault();

        // Solo il nome dell'applicazione. QUALE macchina si sta guardando lo dicono gia' la
        // row sotto il title e la machine evidenziata nella barra laterale: ripeterlo nel
        // title grande e' rumore che si legge a ogni sguardo. La version sta nella barra
        // del title della finestra, non qui: la si cerca quando serve, non la si rilegge.
        Heading = "Observer";

        if (client is null)
        {
            ShowStatus(
                FAInfoBarSeverity.Error,
                "Configuration missing",
                configurationProblem ?? "The configuration could not be read.");
            Subheading = "Not connected.";
        }
        else
        {
            ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            Subheading = "Connecting…";
        }
    }

    /// <summary>Title della finestra: nome e version del programma.</summary>
    /// <remarks>
    /// Costante by tutta la vita della finestra, quindi non e' osservabile. La version e'
    /// quella dei metadati del binario, cioe' di <c>Directory.Build.props</c>, senza l'hash.
    /// </remarks>
    public string WindowTitle { get; } = Title(AppVersion.OfThisProgram());

    /// <summary>Title grande in cima alla finestra.</summary>
    [ObservableProperty]
    public partial string Heading { get; set; }

    /// <summary>Compone il title della finestra dalla version.</summary>
    /// <param name="version">La version corta, o vuota se non c'e'.</param>
    /// <returns><c>Observer 0.8.0</c>, oppure solo <c>Observer</c> quando la version manca.</returns>
    public static string Title(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version.Length == 0 ? "Observer" : "Observer " + version;
    }

    /// <summary>LineFor sotto il title: state del collegamento e timeText dell'ultima lettura.</summary>
    [ObservableProperty]
    public partial string Subheading { get; set; }

    /// <summary>Title della barra di state.</summary>
    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    /// <summary>Testo della barra di state.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>SeverityFor' della barra di state.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity StatusSeverity { get; set; } = FAInfoBarSeverity.Informational;

    /// <summary>True quando c'e' qualcosa da segnalare. Quando tutto va, la barra sparisce.</summary>
    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; } = true;

    /// <summary>I riquadri, uno by sorgente di metriche.</summary>
    public ObservableCollection<MetricGroup> Groups { get; } = [];

    /// <summary>I quadranti, raccolti in cima da tutte le sorgenti.</summary>
    /// <remarks>
    /// Contiene le STESSE istanze che stanno dentro i gruppi, non delle copie: le rows si
    /// aggiornano sul posto una volta al secondo, e due copie divergerebbero senza che niente
    /// lo segnali. Qui si raccolgono soltanto by mostrarle insieme.
    /// </remarks>
    public ObservableCollection<MetricRow> Gauges { get; } = [];

    private DateTimeOffset nextHistoryRead = DateTimeOffset.MinValue;

    /// <summary>True quando c'e' almeno un quadrante da mostrare.</summary>
    /// <remarks>
    /// Senza, un riquadro vuoto col entryClient title resterebbe a schermo quando nessuna metrica e'
    /// misurabile - che e' proprio il momento in cui non deve sembrare che vada tutto bene.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasGauges { get; set; }

    /// <summary>I processi mostrati nel pannello, quando e' aperto.</summary>
    public ObservableCollection<ProcessRowState> Processes { get; } = [];

    /// <summary>True quando il pannello dei processi e' aperto.</summary>
    [ObservableProperty]
    public partial bool IsProcessPanelOpen { get; set; }

    /// <summary>Title del pannello: dice di quale resource si stanno guardando i processi.</summary>
    [ObservableProperty]
    public partial string ProcessesTitle { get; set; } = string.Empty;

    /// <summary>Che cosa non va nel pannello, quando qualcosa non va. Vuoto altrimenti.</summary>
    [ObservableProperty]
    public partial string ProcessesProblem { get; set; } = string.Empty;

    /// <summary>La row selezionata, quella che il pulsante terminerebbe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyRow))]
    [NotifyCanExecuteChangedFor(nameof(CopyProcessRowCommand))]
    public partial ProcessRowState? SelectedProcess { get; set; }

    /// <summary>True quando c'e' una row selezionata da poter terminare.</summary>
    [ObservableProperty]
    public partial bool CanEndProcess { get; set; }

    /// <summary>Il nome della macchina da riaprire la prossima volta, o null by questo computer.</summary>
    /// <remarks>
    /// Il nome GREZZO del endpoint, non <c>MachineRow.Name</c>: quello e' il nome
    /// <i>visibile</i>, che ripiega sull'indirizzo quando una machine non ne ha uno — il caso
    /// della vecchia configurazione a macchina singola — e sulla parola "This machine" by il
    /// canale locale. Nessuna delle due e' una key: la prima e' un indirizzo che finirebbe
    /// in un file dove non deve stare, la seconda non corrisponde a niente in
    /// <c>machines.json</c>.
    /// <para>
    /// E' la macchina davvero LETTA, non quella selezionata: la selezione puo' essere nulla
    /// mentre il giro continua a leggere, ed e' la stessa distinzione by cui esiste
    /// <c>watchedEntry</c>. Non e' osservabile perche' la finestra la legge una volta sola,
    /// alla chiusura.
    /// </para>
    /// </remarks>
    public string? MachineToRemember => watchedEntry?.Endpoint.Name?.Trim();

    /// <summary>True quando gli appunti sono raggiungibili: senza, i comandi restano spenti.</summary>
    /// <remarks>
    /// La cucitura verso gli appunti arriva da chi costruisce il view model, ed e' opzionale
    /// perche' una prova senza finestra non ce l'ha. Se un giorno qualcuno la dimenticasse
    /// nella radice di composizione, un comando che esce da se' sul null lascerebbe un
    /// pulsante che non fa niente e non lo dice — e i test resterebbero verdi, perche' loro il
    /// finto ce l'hanno. Spento si vede al primo avvio.
    /// </remarks>
    public bool CanCopy => copyToClipboard is not null;

    /// <summary>True quando c'e' una row di processo da copiare.</summary>
    /// <remarks>
    /// Sulla SELEZIONE e non su <see cref="CanEndProcess"/>, anche se oggi coincidono: copiare
    /// una row e' di sola lettura, terminarla no, e far viaggiare la prima sul permesso della
    /// seconda vuol dire che il giorno in cui si stringe il cancello di chi puo' uccidere un
    /// processo — un utente senza diritti, una macchina di sola lettura — sparirebbe anche la
    /// possibilita' di copiarne il nome, senza che nessuno l'abbia deciso.
    /// </remarks>
    public bool CanCopyRow => CanCopy && SelectedProcess is not null;

    /// <summary>
    /// True quando il pulsante di terminazione e' gia' state premuto una volta e sta
    /// aspettando la conferma.
    /// </summary>
    /// <remarks>
    /// La conferma sta nel pulsante e non in una finestra di dialogo, e non e' pigrizia: una
    /// finestra modale qui richiederebbe di passare la finestra padre al view model, cioe' di
    /// legare la logica all'interfaccia proprio dove finora non lo e'. Due clic sullo stesso
    /// pulsante, col text che cambia, difendono dallo stesso error — un clic distratto su
    /// una row sbagliata — senza quella dipendenza.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndButtonText))]
    public partial bool IsAwaitingEndConfirmation { get; set; }

    /// <summary>Che cosa c'e' scritto sul pulsante di terminazione, clock.</summary>
    /// <remarks>
    /// UN pulsante che cambia scritta, e non due che si alternano: con due, al primo clic il
    /// pulsante premuto spariva e il fuoco della tastiera cadeva nel vuoto, e chi conferma con
    /// Invio si trovava a premere Invio su niente.
    /// </remarks>
    public string EndButtonText => IsAwaitingEndConfirmation ? "Click again to end it" : "End process";

    /// <summary>
    /// True quando la finestra e' ridotta a icona: la readInterval delle readings si allunga.
    /// </summary>
    /// <remarks>Lo imposta la finestra; il view model non sa cos'e' una finestra.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PollInterval))]
    public partial bool IsMinimized { get; set; }

    /// <summary>Ogni quanto si legge, clock.</summary>
    public TimeSpan PollInterval => IsMinimized ? BackgroundInterval : Interval;

    /// <summary>Quanto e' scalata la finestra: 1 e' la misura normale, sotto 1 e' piu' piccola.</summary>
    /// <remarks>
    /// Avalonia non legge la dimensione del text di sistema, quindi chi l'ha alzata in Windows
    /// qui non la ritrova. Questa e' l'impostazione interna che la sostituisce, e va anche sotto
    /// il 100 %, dove Windows non va: e' uno zoom, non solo una misura del text. La applica la
    /// finestra, che scala tutto - quadranti compresi - e la ricorda fra un avvio e l'altro.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScale))]
    public partial double Zoom { get; set; } = Preferences.NormalZoom;

    /// <summary>Le scale fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<ZoomOption> ScaleOptions { get; } =
        [.. Preferences.AllowedZoomLevels.Select(factor => new ZoomOption(factor))];

    /// <summary>La scala come machine del selettore: e' <see cref="Zoom"/> con un'etichetta.</summary>
    /// <remarks>
    /// Il selettore puo' assegnare null mentre cambia machineList: allora la scala resta com'e'.
    /// </remarks>
    public ZoomOption SelectedScale
    {
        get => new(Zoom);
        set => Zoom = value?.Factor ?? Zoom;
    }

    /// <summary>Una scala non ammessa non entra: la si riporta alla normale.</summary>
    /// <param name="value">La scala query.</param>
    partial void OnZoomChanged(double value)
    {
        double validScale = Preferences.NormalizeZoom(value);

        if (validScale != value)
        {
            Zoom = validScale;
        }
    }

    /// <summary>Il tema: <c>system</c>, <c>light</c> o <c>dark</c>.</summary>
    /// <remarks>
    /// Lo applica l'applicazione, non questa classe, che non sa cos'e' un tema: qui sta solo
    /// la scelta, perche' e' cio' che la tendina mostra e cio' che si ricorda.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTheme))]
    public partial string Theme { get; set; } = Preferences.AllowedThemes[0];

    /// <summary>I temi fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [.. Preferences.AllowedThemes.Select(key => new ThemeOption(key))];

    /// <summary>Quanto history mostra la striscia: <c>1h</c>, <c>24h</c> o <c>7d</c>.</summary>
    /// <remarks>
    /// La key e non la machine, by la stessa ragione del tema: e' cio' che finisce nel file.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedHistoryPeriod))]
    public partial string HistoryPeriod { get; set; } = Preferences.AllowedPeriods[0];

    /// <summary>I periodi fra cui si sceglie, come voci del selettore.</summary>
    public static IReadOnlyList<HistoryPeriodOption> HistoryPeriodOptions { get; } =
        [.. Preferences.AllowedPeriods.Select(key => new HistoryPeriodOption(key))];

    /// <summary>Il period come machine del selettore.</summary>
    /// <remarks>
    /// Il getter non lancia MAI, come quello del tema: una key che non e' nella tabella
    /// ricadrebbe sulla prima machine invece di far cadere la finestra mentre si disegna.
    /// </remarks>
    public HistoryPeriodOption SelectedHistoryPeriod
    {
        get => HistoryPeriodOptions.FirstOrDefault(machine => machine.Key == HistoryPeriod) ?? HistoryPeriodOptions[0];
        set => HistoryPeriod = value?.Key ?? HistoryPeriod;
    }

    /// <summary>Il title sopra la striscia: dice il period DISEGNATO, non quello process.</summary>
    /// <remarks>
    /// Non si calcola da <see cref="HistoryPeriod"/>, ed e' una scelta. Calcolato, cambiava con il
    /// selettore - cioe' all'istante - mentre sotto restavano le barre del period previousWrite
    /// finche' la lettura nuova non atterrava: fino a otto secondi su una macchina lenta, e by
    /// SEMPRE su una macchina che non risponde, perche' li' lo history non si rilegge affatto.
    /// La finestra chiamava "Last 7 days" sessanta barre da un minuto, e una macchina a riposo
    /// da un'timeText si leggeva come a riposo da una settimana. Adesso lo scrive chi disegna, dopo
    /// la guardia sulle risposte in ritardo: il selettore dice cosa e' state chiesto, il title
    /// cosa si sta guardando, e quando divergono e' perche' divergono davvero.
    /// </remarks>
    [ObservableProperty]
    public partial string HistoryTitle { get; set; } = new HistoryPeriodOption(Preferences.AllowedPeriods[0]).Title;

    /// <summary>Il tema come machine del selettore: e' <see cref="Theme"/> con un'etichetta.</summary>
    /// <remarks>Il selettore puo' assegnare null mentre cambia machineList: allora il tema resta com'e'.</remarks>
    public ThemeOption SelectedTheme
    {
        get => new(Theme);
        set => Theme = value?.Key ?? Theme;
    }

    /// <summary>Un tema non ammesso non entra: si torna a quello del sistema.</summary>
    /// <param name="value">Il tema richiesto.</param>
    partial void OnThemeChanged(string value)
    {
        string validTheme = Preferences.NormalizeTheme(value);

        if (!string.Equals(validTheme, value, StringComparison.Ordinal))
        {
            Theme = validTheme;
        }
    }

    /// <summary>Un period non ammesso non entra, e quello nuovo si legge subito.</summary>
    /// <param name="value">Il period richiesto.</param>
    /// <remarks>
    /// Subito e non al prossimo giro: chi sceglie "7 days" guarda la striscia, e aspettare fino
    /// a due ore by vederla cambiare sarebbe indistinguibile da un selettore che non funziona.
    /// La scadenza torna indietro invece di chiamare la lettura da qui: cosi' la query
    /// parte dal ciclo, dove c'e' gia' il token di annullamento e la guardia sull'fetch, e non
    /// da un setter che il selettore chiama sul thread dell'interfaccia.
    /// </remarks>
    partial void OnHistoryPeriodChanged(string value)
    {
        string validTheme = Preferences.NormalizePeriod(value);

        if (!string.Equals(validTheme, value, StringComparison.Ordinal))
        {
            HistoryPeriod = validTheme;

            return;
        }

        // Finche' non c'e' niente disegnato il title segue il selettore: non c'e' striscia da
        // contraddire, e all'avvio con "7d" nel file dire "Last hour" sopra il vuoto sarebbe
        // sbagliato e basta. Appena una lettura atterra, il title torna a dire cio' che si vede.
        if (!Gauges.Any(row => row.ShowHistory))
        {
            HistoryTitle = SelectedHistoryPeriod.Title;
        }

        nextHistoryRead = DateTimeOffset.MinValue;

        // Cambiando period cambia la DOMANDA del riepilogo, non solo la sua risposta: "cosa mi
        // sono perso nell'ultima timeText" e "negli ultimi sette giorni" sono due cose diverse. Si
        // ricomincia da capo, congedo compreso - chi chiude un riquadro chiude quello, non ogni
        // riquadro futuro.
        awaySummaryDismissed = false;
        AwaySummaryText = string.Empty;
        ShowAwaySummary = false;

        foreach (MachineRow machine in Machines)
        {
            machine.SummaryPeriodKey = null;
            machine.SummaryLine = string.Empty;
        }
    }

    /// <summary>Cosa e' successo mentre la finestra era chiusa, una row by macchina.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAwaySummaryCommand))]
    public partial string AwaySummaryText { get; set; } = string.Empty;

    /// <summary>True quando c'e' un riepilogo da mostrare e nessuno lo ha ancora congedato.</summary>
    /// <remarks>
    /// A due vie: la barra ha la sua X, e chiuderla scrive qui. Non e' un dettaglio - la barra
    /// di STATO non e' chiudibile di proposito, perche' dice una cosa in corso; questa dice una
    /// cosa del passato, e un fatto del passato si legge una volta e si archivia.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowAwaySummary { get; set; }

    /// <summary>True quando l'utente ha chiuso il riquadro by questo period.</summary>
    private bool awaySummaryDismissed;

    partial void OnShowAwaySummaryChanged(bool value)
    {
        if (!value && AwaySummaryText.Length > 0)
        {
            awaySummaryDismissed = true;
        }
    }

    /// <summary>Quale resource sta guardando il pannello: <c>cpu</c>, <c>memory</c>, o null.</summary>
    private string? shownResource;

    /// <summary>Le macchine fra cui si puo' scegliere, ognuna col entryClient state. La prima e' sempre questa.</summary>
    public ObservableCollection<MachineRow> Machines { get; } = [];

    /// <summary>Ogni quanto si sondano le macchine che NON si stanno guardando.</summary>
    /// <remarks>
    /// Quindici secondi e non uno: un pallino accanto al nome deve dire "e' viva", non seguire
    /// la CPU. E le sonde partono e non si aspettano: una macchina spenta costa otto secondi
    /// di timeout, e il giro dei quadranti non deve pagarli.
    /// </remarks>
    public static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(15);

    private DateTimeOffset nextProbe = DateTimeOffset.MinValue;

    /// <summary>Le voci dell'machineList che sono state scartate, e perche'.</summary>
    /// <remarks>
    /// Mostrate accanto all'machineList invece che nascoste in un log: una macchina configurata male
    /// che semplicemente NON COMPARE e' indistinguibile da una macchina che non e' stata
    /// aggiunta, e chi la cerca non ha modo di sapere che cosa correggere.
    /// </remarks>
    public ObservableCollection<string> MachineListProblems { get; } = [];

    /// <summary>
    /// Vero quando l'machineList contiene solo questa macchina e non c'e' niente da correggere.
    /// </summary>
    /// <remarks>
    /// La barra laterale si vede SEMPRE, e prima non era cosi': compariva solo quando c'era
    /// gia' una seconda macchina. Il risultato e' che nessuno poteva scoprire di poterne
    /// aggiungere una, perche' l'unico posto dove la funzione si annuncia e' la funzione
    /// stessa. Una funzione che si mostra solo a chi sa gia' che esiste non esiste.
    /// <para>
    /// Al entryClient posto, quando c'e' una macchina sola, si spiega come aggiungerne un'altra e si
    /// dice il percorso esatto del file da scrivere.
    /// </para>
    /// </remarks>
    public bool ShowMachineListHint => Machines.Count <= 1 && MachineListProblems.Count == 0;

    /// <summary>Come si aggiunge una macchina, col percorso del file da scrivere.</summary>
    public string MachineListHint { get; } =
        "Only this machine so far. To watch another one, run \"observer share\" on it and put " +
        "what it prints into " + MachineDirectory.FilePath;

    /// <summary>La macchina attualmente guardata.</summary>
    [ObservableProperty]
    public partial MachineRow? SelectedMachine { get; set; }

    /// <summary>Cambia macchina senza riavviare la finestra.</summary>
    /// <param name="value">La macchina scelta nell'machineList.</param>
    partial void OnSelectedMachineChanged(MachineRow? value)
    {
        if (value is null)
        {
            // La lista non dovrebbe arrivarci (AlwaysSelected); se ci arriva, la macchina
            // guardata resta quella di prima e watchedEntry non cambia.
            return;
        }

        watchedEntry = value;

        // Il carico e' un derivato della macchina guardata come il catalog e i quadranti, e va
        // buttato QUI, prima delle uscite anticipate, perche' l'invariante e' legata a
        // watchedEntry e non al client. Senza questa row la machine appena cliccata continua a
        // mostrare i numeri che la sonda le aveva scritto fino a quindici secondi prima: circa
        // un secondo se la macchina risponde, ma gli interi otto del budget di query se non
        // risponde - cioe' proprio quando la si e' cliccata by capire cosa le succede, sotto
        // il nome evidenziato resta scritto che sta lavorando mentre la barra dice "Connecting".
        value.MachineLoad = MachineLoad.None;

        if (openMachine is null)
        {
            return;
        }

        if (client is not null && value.Endpoint == client.Endpoint)
        {
            return;
        }

        client = openMachine(value.Endpoint);

        // Tutto cio' che descriveva la macchina PRECEDENTE va buttato: il catalog, perche' le
        // etichette appartengono a quel servizio, e i riquadri, perche' sono le sue misure.
        // L'clock dei guasti invece si EREDITA dalla machine: se la sonda sa gia' da venti
        // secondi che questa macchina e' spenta, la barra apre rossa subito invece di recitare
        // dieci secondi di "Connecting" - la grazia serve a un servizio che sta partendo, non a
        // uno gia' misurato spento. E cosi' barra e pallino hanno un clock solo.
        catalogLoaded = false;
        catalog = MetricCatalog.Empty;
        faultSince = value.FailingSince;
        Groups.Clear();

        // E i quadranti, che sono una SECONDA collezione sulle stesse rows. Svuotare solo i
        // riquadri lasciava a schermo le lancette e le strisce della macchina previousWrite,
        // sotto il nome di quella nuova: numeri veri, attribuiti alla macchina sbagliata. Si
        // vedeva a colpo d'occhio proprio perche' meta' della finestra si svuotava e meta' no.
        // Chi aggiunge una terza collezione derivata la aggiunga QUI.
        Gauges.Clear();
        HasGauges = false;

        // E anche la scadenza dello history e' un derivato della macchina previousWrite. Le rows
        // rinascono senza striscia e senza nota - ne' barre ne' il motivo by cui non ci sono -
        // e senza questa row restano cosi' fino alla scadenza EREDITATA: mezz'timeText a sette
        // giorni, quasi quattro minuti a ventiquattro ore, con i quadranti sopra gia' vivi.
        nextHistoryRead = DateTimeOffset.MinValue;

        ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading...");
        Subheading = "Connecting...";
    }

    /// <summary>
    /// Il ciclo di aggiornamento. Non lancia mai: qualunque guasto diventa text a schermo.
    /// </summary>
    /// <param name="cancellationToken">Annullato alla chiusura dell'applicazione.</param>
    /// <summary>
    /// Riprova a leggere la configurazione finche' non diventa validScale.
    /// </summary>
    /// <returns>True se un client e' state adottato, false se non c'e' modo di riprovare.</returns>
    private async Task<bool> WaitForConfigurationAsync(CancellationToken cancellationToken)
    {
        if (rereadConfiguration is null)
        {
            return false;
        }

        using PeriodicTimer timer = new(Interval);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                return false;
            }

            if (rereadConfiguration() is not { } newClient)
            {
                continue;
            }

            client = newClient;
            faultSince = null;
            ShowStatus(FAInfoBarSeverity.Informational, "Connecting", "Taking the first reading…");
            Subheading = "Connecting…";
            return true;
        }

        return false;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            // Senza token non c'e' niente da interrogare: martellare il servizio con richieste
            // destinate al 401 non aiuta. Ma il message a schermo dice all'utente di creare
            // un file di configurazione, e se crearlo non producesse alcun effetto finche' non
            // riavvia — cosa che il message non dice — l'utente seguirebbe le istruzioni alla
            // lettera e concluderebbe che l'applicazione e' rotta. Quindi si rilegge.
            if (!await WaitForConfigurationAsync(cancellationToken))
            {
                return;
            }
        }

        using PeriodicTimer timer = new(Interval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServiceOutcome fetch = await RefreshAsync(cancellationToken);

                // Lo history dopo il campionamento e solo se il campionamento e' andato: se
                // la macchina non risponde, insistere sullo history aggiungerebbe attese a una
                // finestra che sta gia' aspettando, senza poter dire niente di nuovo.
                if (fetch == ServiceOutcome.Ok && clock() >= nextHistoryRead)
                {
                    // La scadenza la sposta la lettura, non questa row: e' l'unica che sa se
                    // e' andata bene, male, o se la risposta e' arrivata quando non serviva
                    // piu'. Spostarla da qui significava threeSteps cose sbagliate insieme - un
                    // timeout rimandava di un passo intero, cioe' due ore di "No history" su
                    // dati gia' tornati; una risposta scartata cancellava l'azzeramento che il
                    // selettore aveva appena fatto, spegnendolo by quindici secondi; e la
                    // scadenza si leggeva dal period di ADESSO invece che da quello chiesto.
                    await RefreshHistoryAsync(cancellationToken);
                }

                // I processi seguono lo stesso giro dei quadranti, ma solo a pannello aperto:
                // chiedere un machineList che nessuno sta guardando costerebbe una query al
                // secondo by niente.
                if (fetch == ServiceOutcome.Ok && IsProcessPanelOpen)
                {
                    await RefreshProcessesAsync(cancellationToken);
                }

                // Le altre macchine, by il pallino accanto al nome. Partono e non si
                // aspettano: vedi ProbeOtherMachines.
                if (clock() >= nextProbe)
                {
                    nextProbe = clock() + StatusRefreshInterval;
                    ProbeOtherMachines(cancellationToken);

                    // E, allo stesso passo, cosa e' successo mentre la finestra era chiusa. Sta
                    // QUI dentro e non fuori by due ragioni: una query succeeded non si
                    // ripete mai (la guardia e' SummaryPeriodKey), ma una FALLITA si', e
                    // questa e' la sua readInterval - la stessa con cui la sonda riprova il pallino.
                    // Fuori dal cancello girerebbe una volta al secondo by non fare niente.
                    StartAwaySummaries(cancellationToken);
                }

                // Un 401 su una finestra GIA' collegata significa quasi sempre che il token e'
                // state ruotato. Senza rileggere qui, la finestra resterebbe bloccata su
                // "Token rejected" fino al riavvio: e' lo stesso incidente di "Configuration
                // missing", su un altro percorso, e va chiuso allo stesso modo.
                // Anche FingerprintMismatch, e by la stessa ragione: il message dice
                // all'utente di correggere machines.json, e correggerlo deve BASTARE. E' il
                // terzo percorso su cui questo incidente si presenta - dopo "Configuration
                // missing" e "Token rejected" - e chiuderne due su threeSteps non serve a niente.
                if (fetch is ServiceOutcome.TokenRejected or ServiceOutcome.FingerprintMismatch)
                {
                    AdoptUpdatedConfiguration();
                }

                // La readInterval segue la finestra: ridotta a icona si legge ogni dieci secondi.
                // Cambiare il period di un PeriodicTimer vale dal tick successivo, che e'
                // esattamente quello che serve: nessun timer da ricreare, nessun giro perso.
                timer.Period = PollInterval;

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura dell'applicazione: uscita normale, non un error da mostrare.
        }
#pragma warning disable CA1031 // Questo ciclo e' avviato senza nessuno che ne attenda l'fetch:
        catch (Exception ex) // un'eccezione qui sparirebbe in silenzio e la finestra si
#pragma warning restore CA1031 // congelerebbe senza dire niente. Va mostrata, non propagata.
        {
            ShowStatus(
                FAInfoBarSeverity.Error,
                "Updates stopped",
                $"Automatic refresh stopped after an unexpected error ({ex.GetType().Name}: " +
                $"{ex.Message}). The values on screen have stopped updating: close and reopen the application.");
            Subheading = "Refresh stopped.";
        }
    }

    /// <summary>
    /// Rilegge la configurazione e adotta il client risultante, se e' cambiato.
    /// </summary>
    /// <remarks>
    /// Non chiude il client previousWrite: chi lo ha costruito ne conserva il riferimento e lo
    /// chiude all'uscita. Chiuderlo qui lo strapperebbe da sotto una query ancora in volo.
    /// </remarks>
    private void AdoptUpdatedConfiguration()
    {
        if (rereadConfiguration?.Invoke() is not { } updatedClient || ReferenceEquals(updatedClient, client))
        {
            return;
        }

        client = updatedClient;
        watchedEntry?.Update(updatedClient.Endpoint);

        // Attesa nuova: l'endpoint e' cambiato, e i secondi gia' consumati contro il
        // previousWrite non dicono niente su questo. Il pallino ha lo stesso clock, ma lo
        // azzera Update qui sopra, insieme al text che ne deriva: da fuori la machine non lo
        // tocca piu' nessuno.
        faultSince = null;

        // Il catalog appartiene al servizio previousWrite: va refreshedEndpoint, altrimenti le etichette
        // resterebbero quelle di una macchina diversa.
        catalogLoaded = false;
        catalog = MetricCatalog.Empty;
    }

    private async Task<ServiceOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        if (client is not { } activeClient)
        {
            return ServiceOutcome.Unknown;
        }

        // Prima il campionamento e SOLO POI il catalog. Verificato sperimentalmente: con il
        // servizio spento, chiedere prima il catalog raddoppia l'timer — due timeout invece
        // di uno — e la finestra resta a dire "collegamento in corso" by sei secondi prima di
        // ammettere che non si collega.
        SnapshotFetch fetch = await activeClient.GetLatestAsync(cancellationToken);

        // Fra la partenza della query e la sua risposta l'utente puo' aver cambiato
        // macchina nella barra laterale. Applicare qui i valori appena arrivati significherebbe
        // mostrare le misure della macchina PRECEDENTE sotto il nome di quella nuova, e
        // riempirne il catalog con etichette che non sono le sue.
        if (!ReferenceEquals(client, activeClient))
        {
            return ServiceOutcome.Unknown;
        }

        if (!fetch.IsOk)
        {
            ReportProblem(fetch.Outcome, fetch.Problem, activeClient.Endpoint);
            return fetch.Outcome;
        }

        // Il catalog cambia solo quando cambia il servizio: si legge una volta sola, e si
        // ritenta al giro dopo se non riesce. Va letto PRIMA di disegnare, altrimenti il primo
        // fotogramma mostrerebbe "cpu.usage.total" al posto di "CPU usage". Se non arriva
        // mai, le metriche restano visibili con il loro identificatore grezzo invece di sparire.
        if (!catalogLoaded)
        {
            CatalogFetch catalogFetch = await activeClient.GetCatalogAsync(cancellationToken);

            if (catalogFetch.IsOk && ReferenceEquals(client, activeClient))
            {
                catalog = catalogFetch.Catalog!;
                catalogLoaded = true;
            }
        }

        // Stessa guardia anche dopo il catalog: e' un secondo await, e l'utente puo' aver
        // cambiato macchina proprio li'. Senza, il pallino di una macchina mai contattata
        // diventava "Reachable" con la lettura di quella previousWrite.
        if (!ReferenceEquals(client, activeClient))
        {
            return ServiceOutcome.Unknown;
        }

        MachineSnapshot snapshot = fetch.Snapshot!;
        Apply(SnapshotProjection.Project(snapshot, catalog));

        IsStatusVisible = false;
        watchedEntry?.Record(ServiceOutcome.Ok, string.Empty, clock());

        // La serie di guasti e' finita: la prossima ricomincia da capo, e ha diritto alla
        // stessa timer che ha avuto questa.
        faultSince = null;

        // Solo QUANDO. Il dove non e' sparito, si e' spostato dove non va refreshedEndpoint a ogni
        // sguardo: la macchina che si sta guardando e' quella selezionata nell'machineList a
        // sinistra, e questa row cambia una volta al secondo mentre quella non cambia mai.
        string timeText = snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Subheading = $"Last Reading: {timeText}";

        return ServiceOutcome.Ok;
    }

    /// <summary>
    /// Traduce una lettura fallita in cio' che si vede a schermo.
    /// </summary>
    /// <remarks>
    /// La severity' NON dipende dal singolo tentativo andato male ma da quanto dura la serie:
    /// e' <see cref="StatusEscalation"/> a deciderlo, ed e' li' che sta la tabella provata.
    /// Qui resta solo la misura del tempo e la traduzione in colore.
    /// </remarks>
    private void ReportProblem(ServiceOutcome fetch, string text, ObserverEndpoint endpoint)
    {
        DateTimeOffset timeText = clock();
        faultSince ??= timeText;

        StatusMessage message = StatusEscalation.MessageFor(
            fetch,
            text,
            timeText - faultSince.Value,
            endpoint,
            hasValuesOnScreen: Groups.Count > 0);

        ShowStatus(SeverityFor(message.Tone), message.Title, message.Text);
        Subheading = message.Subheading;

        // Il pallino della macchina guardata segue la barra di state, con lo stesso clock,
        // cosi' i due non dicono mai cose diverse.
        watchedEntry?.Record(fetch, text, timeText);
    }

    /// <summary>Interroga le macchine che non si stanno guardando, tutte insieme e senza aspettarle.</summary>
    /// <param name="cancellationToken">Annullato alla chiusura.</param>
    /// <remarks>
    /// Il giro principale NON attende le sonde: una macchina spenta risponde dopo otto secondi
    /// di timeout, e i quadranti della macchina guardata non devono fermarsi by questo. Ogni
    /// sonda aggiorna la propria machine quando torna, e finche' e' in volo non ne parte un'altra.
    /// </remarks>
    /// <summary>Cosa e' successo mentre nessuno guardava, macchina by macchina.</summary>
    /// <remarks>
    /// <para>
    /// Questa e' la risposta che questo progetto puo' dare ONESTAMENTE alla domanda "avvisami
    /// se una macchina cade mentre la finestra e' chiusa". L'avviso vero - icona nell'area di
    /// notifica, o notifica di sistema - non e' consegnabile su questo stack senza poter
    /// fallire in SILENZIO, che e' la cosa che questo programma non fa: l'icona di Avalonia non
    /// si puo' interrogare (<c>TrayIcon._impl</c> e' internal e ogni chiamata e' <c>?.</c>), su
    /// Linux senza un host StatusNotifierItem non compare e non logga niente, e su Windows il
    /// valore di ritorno di <c>Shell_NotifyIcon</c> e' ignorato. Un avviso che puo' non
    /// comparire senza dirlo e' peggio di nessun avviso - la stessa ragione by cui in 0.16.0
    /// e' state tolto Ctrl+C.
    /// </para>
    /// <para>
    /// Il dato pero' c'e' gia', e non su questa macchina: il servizio remoto conserva sette
    /// giorni di campioni al minuto. Quindi non serve nessun processo acceso, nessuna
    /// dipendenza e nessun avvio automatico - si chiede al rientro. Una query by macchina
    /// by period process, non periodica: la guardia e' <c>SummaryPeriodKey</c>, ed e' anche
    /// cio' che fa ripartire il conto quando si cambia period, perche' li' cambia la domanda.
    /// </para>
    /// <para>
    /// Cio' che NON copre, e va detto: non sveglia nessuno, e non dice niente della macchina
    /// ancora giu' clock - quel dato ce l'ha lei, e lei non risponde. MessageFor quella restano il
    /// rombo rosso e "for 3 min", con il limite gia' dichiarato su <c>FailingSince</c>.
    /// </para>
    /// </remarks>
    private void StartAwaySummaries(CancellationToken cancellationToken)
    {
        HistoryPeriodOption period = SelectedHistoryPeriod;

        foreach (MachineRow machine in Machines)
        {
            // Solo le macchine che rispondono: a una che non risponde lo history non si puo'
            // chiedere, ed e' proprio quella dove servirebbe di piu'. Quella non produce alcuna
            // row, di proposito - a dirlo ci sono gia' il rombo rosso e "for 3 min" accanto al
            // nome, e ripeterlo qui sarebbe la stessa cosa scritta due volte. La row
            // "history could not be read" e' by il caso diverso: la macchina risponde e lo
            // history no, che senza una frase resterebbe indistinguibile dal "tutto bene".
            if (machine.IsSummarizing
                || machine.Status != MachineStatus.Reachable
                || string.Equals(machine.SummaryPeriodKey, period.Key, StringComparison.Ordinal))
            {
                continue;
            }

            IMetricsClient? entryClient = ReferenceEquals(machine, watchedEntry) ? client : openMachine?.Invoke(machine.Endpoint);

            if (entryClient is null)
            {
                continue;
            }

            machine.IsSummarizing = true;
            _ = ReadAwaySummaryAsync(machine, entryClient, period, cancellationToken);
        }
    }

    /// <summary>Legge lo history di una macchina e ne ricava la row del riepilogo.</summary>
    private async Task ReadAwaySummaryAsync(
        MachineRow machine,
        IMetricsClient entryClient,
        HistoryPeriodOption period,
        CancellationToken cancellationToken)
    {
        try
        {
            // UNA serie sola, e fissa: la domanda non e' "cosa misurava" ma "stava misurando",
            // e a quella risponde qualunque metrica che il servizio campiona sempre. Stesso
            // argomento di MachineLoad, stessa costante condivisa da Observer.Core.
            // Si chiede PIU' indietro di quanto si esamina, e non e' un di piu'. La griglia si
            // ancora all'ULTIMO endpoint che la macchina manda, e quel endpoint e' indietro rispetto
            // ad clock quanto dura il consolidamento: chiedendo esattamente la finestra, le
            // prime caselle cadrebbero prima del "da" della query e sarebbero vuote PER
            // COSTRUZIONE, non perche' la macchina fosse spenta. Essendo contigue all'inizio
            // verrebbero lette come bordo, cioe' "nothing known before" su OGNI macchina sana a
            // OGNI apertura - una barra che si apre sempre dicendo sempre la stessa cosa non
            // vera si impara a chiudere senza leggerla. Il margine e' TailFor, il numero che
            // questo progetto ha gia' misurato by lo stesso ritardo nella striscia; i points in
            // piu' cadono fuori dalla griglia e Build li ignora.
            HistoryFetch history = await entryClient.GetHistoryAsync(
                new HistoryQuery(
                    "cpu",
                    CpuCollector.TotalUsageMetricId,
                    null,
                    clock() - period.Duration - TailFor(period),
                    period.Resolution),
                cancellationToken).ConfigureAwait(true);

            // Il period puo' essere cambiato durante l'timer: quella risposta risponde a una
            // domanda che non e' piu' quella sullo schermo.
            if (SelectedHistoryPeriod != period)
            {
                return;
            }

            machine.SummaryLine = LineFor(machine, history, period);

            // La key si marca SOLO quando si e' letto davvero. Marcarla anche sul guasto
            // vorrebbe dire che un singolo timeout - otto secondi by l'intera risposta, e a
            // sette giorni sono duemila points - lascia in cima alla finestra "history could not
            // be read" by tutta la sessione, mentre accanto al nome la macchina e' verde e i
            // quadranti si aggiornano ogni secondo. Non marcandola si riprova al giro delle
            // sonde, e la row stantia si sostituisce da sola.
            if (history.Outcome == ServiceOutcome.Ok)
            {
                machine.SummaryPeriodKey = period.Key;
            }

            ComposeAwaySummary();
        }
        catch (OperationCanceledException)
        {
            // Chiusura: niente da dire.
        }
#pragma warning disable CA1031 // Come la sonda: un riepilogo che lancia non deve far cadere niente.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // Come sopra: non si marca la key, cosi' si riprova.
            machine.SummaryLine = $"{machine.Name}: history could not be read ({error.Message})";
            ComposeAwaySummary();
        }
        finally
        {
            machine.IsSummarizing = false;
        }
    }

    private static string LineFor(MachineRow machine, HistoryFetch history, HistoryPeriodOption period)
    {
        if (history.Outcome != ServiceOutcome.Ok || history.Points is null)
        {
            return $"{machine.Name}: history could not be read ({history.Problem})";
        }

        if (history.Points.Count == 0)
        {
            // Zero points non e' "sempre giu'": puo' essere una macchina installata ieri, o la
            // persistenza spenta. Dirlo cosi' com'e' costa una parola e non inventa niente.
            return $"{machine.Name}: no history for this period";
        }

        return AwaySummary.LineFor(
            machine.Name,
            HistoryStrip.FindGaps(history.Points, period.Duration, period.SourceStep),

            // Stessa soglia di HistoryStrip.Descrivi: oltre la giornata l'timeText da sola non
            // colloca piu' niente.
            period.Duration > TimeSpan.FromHours(24));
    }

    /// <summary>Mette insieme le rows delle macchine in un text solo.</summary>
    private void ComposeAwaySummary()
    {
        string text = string.Join(
            Environment.NewLine,
            Machines.Select(machine => machine.SummaryLine).Where(row => row.Length > 0));

        AwaySummaryText = text;

        // Chiuso dall'utente resta chiuso, finche' non cambia il period: una row in piu' che
        // arriva dieci secondi dopo non deve far ricomparire un riquadro appena congedato.
        ShowAwaySummary = text.Length > 0 && !awaySummaryDismissed;
    }

    private void ProbeOtherMachines(CancellationToken cancellationToken)
    {
        if (openMachine is null)
        {
            return;
        }

        foreach (MachineRow machine in Machines)
        {
            // Si salta la machine che il giro principale legge DAVVERO, non la selezione della
            // lista: vedi watchedEntry.
            if (machine.IsProbing || ReferenceEquals(machine, watchedEntry))
            {
                continue;
            }

            machine.IsProbing = true;
            _ = ProbeAsync(machine, cancellationToken);
        }
    }

    private async Task ProbeAsync(MachineRow machine, CancellationToken cancellationToken)
    {
        try
        {
            SnapshotFetch fetch = await openMachine!(machine.Endpoint).GetLatestAsync(cancellationToken);

            // La machine e' diventata la GUARDATA mentre la sonda era in volo: non si scrive, e
            // non si rilegge nemmeno il endpoint. La sonda parte saltando la guardata ma torna
            // fino a otto secondi dopo, e un clic basta. Scrivere vorrebbe dire due readings
            // della stessa macchina a cadenze diverse, che con i numeri accanto al nome si
            // contraddicono a vista. E rileggere sarebbe peggio che inutile: Update
            // sostituisce Endpoint senza toccare client, e AdoptUpdatedConfiguration
            // confronta proprio quel Endpoint con il disco - trovandolo gia' aggiornato non
            // riparerebbe piu', e la finestra resterebbe su "Token rejected" dopo un
            // "observer token set" andato a buon fine. Sulla guardata ci pensa il giro
            // principale, che ha in mano sia il endpoint sia il client.
            if (ReferenceEquals(machine, watchedEntry))
            {
                return;
            }

            machine.Record(fetch.Outcome, fetch.Problem, clock(), fetch.Snapshot);

            // Token rifiutato o impronta che non corrisponde: la machine va riletta da disco,
            // come fa gia' il giro principale by la macchina guardata. Altrimenti la sonda
            // successiva riparte con la credenziale vecchia e il pallino resta rosso fino al
            // riavvio, anche dopo "observer token set".
            if (fetch.Outcome is ServiceOutcome.TokenRejected or ServiceOutcome.FingerprintMismatch
                && rereadEndpoint?.Invoke(machine.Endpoint) is { } refreshedEndpoint
                && refreshedEndpoint != machine.Endpoint)
            {
                machine.Update(refreshedEndpoint);
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura: niente da registrare.
        }
#pragma warning disable CA1031 // Una sonda che lancia non deve far cadere niente: il entryClient fetch e' un pallino.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // Stessa guardia del ramo riuscito, e by la stessa ragione.
            if (!ReferenceEquals(machine, watchedEntry))
            {
                machine.Record(ServiceOutcome.Unknown, error.Message, clock());
            }
        }
        finally
        {
            machine.IsProbing = false;
        }
    }

    private static FAInfoBarSeverity SeverityFor(StatusTone tone) => tone switch
    {
        StatusTone.Informational => FAInfoBarSeverity.Informational,
        StatusTone.Warning => FAInfoBarSeverity.Warning,
        _ => FAInfoBarSeverity.Error,
    };

    private void ShowStatus(FAInfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusText = message;
        IsStatusVisible = true;
    }

    private void Apply(IReadOnlyList<MetricGroupState> states)
    {
        if (!HasSameCollectors(states))
        {
            Groups.Clear();

            foreach (MetricGroupState state in states)
            {
                Groups.Add(new MetricGroup(state));
            }
        }

        else
        {
            for (int i = 0; i < states.Count; i++)
            {
                Groups[i].Update(states[i]);
            }
        }

        SyncGauges();
    }

    /// <summary>Rilegge lo history di ogni metrica che ha un quadrante.</summary>
    /// <param name="cancellationToken">Annullato alla chiusura.</param>
    /// <remarks>
    /// <para>
    /// Non lancia e non tocca <c>faultSince</c> ne' la barra di state, di proposito: <b>un
    /// guasto dello history non e' un guasto della macchina</b>. Il servizio puo' rispondere
    /// benissimo al campionamento e avere la persistenza spenta, e colorare di rosso la
    /// finestra by questo insegnerebbe a ignorare anche gli allarmi veri. Il motivo finisce
    /// accanto alla striscia, dove riguarda.
    /// </para>
    /// <para>
    /// Questa funzione possiede <c>nextHistoryRead</c>, e ci sono TRE esiti, non due: andata
    /// bene, andata male, e arrivata quando non serviva piu'. Il terzo non tocca la scadenza -
    /// chi ha cambiato period o macchina l'ha appena riportata indietro di proposito, e
    /// spostarla qui vorrebbe dire lasciare a schermo la striscia vecchia sotto il title nuovo
    /// by quindici secondi, che da fuori e' indistinguibile da un selettore rotto.
    /// </para>
    /// </remarks>
    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        if (client is not { } activeClient)
        {
            return;
        }

        DateTimeOffset timeText = clock();
        HistoryPeriodOption period = SelectedHistoryPeriod;

        // Tutte le strisce insieme, non una dopo l'altra: sei quadranti facevano dodici
        // richieste in fila, e il tempo del giro era la SOMMA delle latenze. Le richieste
        // partono qui, in parallelo; le rows si toccano solo dopo, quando sono tornate tutte,
        // e sul thread dell'interfaccia.
        List<MetricRow> rows = [.. Gauges];

        (HistoryFetch Aggregate, HistoryFetch? Tail)[] readings = await Task.WhenAll(
            rows.Select(row => ReadHistoryAsync(activeClient, row.Key, period, timeText, cancellationToken)))
            .ConfigureAwait(true);

        // A sette giorni una lettura puo' durare l'intero budget di otto secondi, e in quel
        // tempo possono essere cambiate DUE cose: il period process e la macchina guardata.
        // Scrivere queste barre clock vorrebbe dire disegnare una settimana dentro una
        // striscia da un'timeText, o lo history della macchina sbagliata.
        // Il confronto sulle rows non e' un di piu' rispetto a quello sul client: App.Apri
        // tiene UN client by endpoint, quindi due cambi di macchina in fila (A->B->A) riportano
        // lo stesso identico oggetto, mentre Gauges e' stata svuotata due volte e queste
        // rows non sono piu' a schermo. Scriverci dentro perderebbe la lettura in silenzio.
        if (!ReferenceEquals(client, activeClient)
            || SelectedHistoryPeriod != period
            || !rows.SequenceEqual(Gauges))
        {
            return;
        }

        // Il title lo scrive chi disegna, qui e non nel selettore: da questa row in poi la
        // striscia e la frase sopra parlano dello stesso period.
        HistoryTitle = period.Title;

        // "Andata bene" vuol dire TUTTE, non almeno una. Con "almeno una" cinque strisce su sei
        // possono restare due ore a dire "No history" mentre la sesta si aggiorna, che e' lo
        // stesso difetto di prima ridotto di un sesto. Solo l'aggregate conta: la tail grezza
        // puo' mancare senza che la striscia ne soffra - la disegna comunque l'aggregate - e
        // guardarla qui trasformerebbe un guasto innocuo in una query ogni quindici secondi
        // by sempre. Zero quadranti conta come non andata: non e' partita nessuna query,
        // quindi riprovare presto e' gratis e copre i quadranti che compaiono piu' tardi.
        bool succeeded = rows.Count > 0;

        for (int i = 0; i < rows.Count; i++)
        {
            ApplyHistory(rows[i], readings[i].Aggregate, readings[i].Tail, period, timeText);
            succeeded &= readings[i].Aggregate.Outcome == ServiceOutcome.Ok;
        }

        nextHistoryRead = clock() + HistoryReadDelay(period, succeeded);
    }

    /// <summary>Fra quanto si rilegge lo history, dato il period e com'e' andata.</summary>
    /// <param name="period">Il period mostrato.</param>
    /// <param name="succeeded">True se ogni striscia ha ricevuto i suoi dati.</param>
    /// <returns>Quanto aspettare prima della lettura successiva.</returns>
    /// <remarks>
    /// Pura e pubblica perche' i due errori che ha gia' fatto non si vedono da nessuna parte
    /// se non qui: <b>rimandare un guasto di un passo intero</b> - a sette giorni due ore di
    /// "No history" su dati tornati da un secondo - e <b>rileggere esattamente ogni passo</b>,
    /// che sembra la readInterval giusta e non lo e', perche' guarderebbe ogni volta una barra
    /// appena nata e l'estremo destro della striscia resterebbe un pixel by sempre.
    /// </remarks>
    public static TimeSpan HistoryReadDelay(HistoryPeriodOption period, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (!succeeded)
        {
            return HistoryRetryInterval;
        }

        TimeSpan readInterval = period.Step / RereadsPerStep;

        return readInterval > MinimumRereadInterval ? readInterval : MinimumRereadInterval;
    }

    /// <summary>From quanto indietro leggere il grezzo by la tail, dato il passo della sorgente.</summary>
    /// <remarks>
    /// Tre points di sorgente, mai meno del minimo. Con la sorgente a un minuto restano i dieci
    /// minuti di sempre; a cinque minuti servono quindici, perche' il consolidamento di quel
    /// livello aspetta anche il livello sotto e resta indietro piu' a lungo.
    /// </remarks>
    private static TimeSpan TailFor(HistoryPeriodOption period)
    {
        TimeSpan threeSteps = period.SourceStep * 3;

        return threeSteps > MinimumTail ? threeSteps : MinimumTail;
    }

    /// <summary>Le due readings dello history di UNA metrica: l'aggregate al minuto e la tail grezza.</summary>
    /// <returns>La tail e' null quando l'aggregate non c'e': senza quello non serve.</returns>
    private static async Task<(HistoryFetch Aggregate, HistoryFetch? Tail)> ReadHistoryAsync(
        IMetricsClient activeClient,
        string key,
        HistoryPeriodOption period,
        DateTimeOffset timeText,
        CancellationToken cancellationToken)
    {
        string[] parts = key.Split('|');

        if (parts.Length < 2)
        {
            return (new HistoryFetch(ServiceOutcome.Unknown, "malformed metric key", null), null);
        }

        string? instance = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

        HistoryFetch aggregate = await activeClient.GetHistoryAsync(
            new HistoryQuery(parts[0], parts[1], instance, timeText - period.Duration, period.Resolution),
            cancellationToken).ConfigureAwait(false);

        if (aggregate.Outcome != ServiceOutcome.Ok || aggregate.Points is null)
        {
            return (aggregate, null);
        }

        HistoryFetch tail = await activeClient.GetHistoryAsync(
            new HistoryQuery(parts[0], parts[1], instance, timeText - TailFor(period), "raw"),
            cancellationToken).ConfigureAwait(false);

        return (aggregate, tail);
    }

    private static void ApplyHistory(
        MetricRow row,
        HistoryFetch aggregate,
        HistoryFetch? tail,
        HistoryPeriodOption period,
        DateTimeOffset timeText)
    {
        if (aggregate.Outcome != ServiceOutcome.Ok || aggregate.Points is null)
        {
            row.History = null;
            row.HistoryNote = "No history: " + aggregate.Problem;

            return;
        }

        // La tail grezza si raggruppa al passo della SORGENTE, non a quello della barra: e'
        // cio' che la rende confrontabile con i points aggregati prima di unirli. Il passo
        // della barra lo applica Build, una volta sola e su tutto.
        IReadOnlyList<HistoryPoint> points = tail is { Outcome: ServiceOutcome.Ok, Points: not null }
            ? HistoryStrip.Merge(aggregate.Points, HistoryStrip.Bucket(tail.Points, period.SourceStep))
            : aggregate.Points;

        row.HistoryNote = points.Count > 0
            ? string.Empty
            : "No history recorded for this metric yet.";

        row.History = HistoryStrip.Build(ToFractions(points), timeText, period.BarCount, period.Step);
    }

    /// <summary>Porta i valori dello history nella scala 0..1 dei quadranti.</summary>
    /// <remarks>
    /// Lo history conserva i valori come sono states misurati, quindi una percentuale arriva
    /// da 0 a 100. E' la stessa divisione che <c>MetricFormatting.Fraction</c> fa by la row
    /// a schermo: se le due divergessero, quadrante e striscia racconterebbero due storie
    /// diverse della stessa metrica.
    /// </remarks>
    private static IReadOnlyList<HistoryPoint> ToFractions(IReadOnlyList<HistoryPoint> points) =>
        [.. points.Select(endpoint => endpoint with
        {
            Avg = Math.Clamp(endpoint.Avg / 100d, 0d, 1d),
            Min = Math.Clamp(endpoint.Min / 100d, 0d, 1d),
            Max = Math.Clamp(endpoint.Max / 100d, 0d, 1d),
            Last = Math.Clamp(endpoint.Last / 100d, 0d, 1d),
        })];

    /// <summary>Rifa' l'machineList dei quadranti solo quando cambia davvero.</summary>
    /// <remarks>
    /// Il confronto e' by RIFERIMENTO, e deve restarlo: le rows sono le stesse istanze che
    /// stanno nei gruppi e si aggiornano da sole, quindi svuotare e riempire la collezione a
    /// ogni giro ricostruirebbe ogni quadrante una volta al secondo, facendo lampeggiare la
    /// finestra. Si ricostruisce quando un collector va o viene, oppure quando una metrica
    /// smette di essere misurabile e il entryClient quadrante non ha piu' senso.
    /// </remarks>
    private void SyncGauges()
    {
        List<MetricRow> expectedGauges =
            [.. Groups.SelectMany(group => group.Rows).Where(row => row.HasGauge)];

        HasGauges = expectedGauges.Count > 0;

        if (expectedGauges.Count == Gauges.Count
            && !expectedGauges.Where((row, i) => !ReferenceEquals(row, Gauges[i])).Any())
        {
            return;
        }

        Gauges.Clear();

        foreach (MetricRow row in expectedGauges)
        {
            Gauges.Add(row);
        }
    }

    /// <summary>Apre il pannello dei processi by la resource del quadrante process.</summary>
    /// <param name="row">Il quadrante su cui si e' cliccato.</param>
    /// <returns>L'timer della prima lettura.</returns>
    /// <remarks>
    /// Le esecuzioni concorrenti vanno PERMESSE: il comando e' uno solo by tutti i quadranti,
    /// e un comando asincrono, finche' e' in esecuzione, rifiuta ogni altra esecuzione. Senza
    /// questo, mentre la prima lettura e' in volo su una macchina remota lenta, ogni altro clic
    /// — su un altro quadrante, o sullo stesso by chiudere — verrebbe scartato in silenzio, e
    /// la finestra sembrerebbe non rispondere. La risposta di una lettura ormai superata la
    /// scarta <see cref="RefreshProcessesAsync"/>.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenProcessesAsync(MetricRow? row)
    {
        if (row is null || ProcessResource.From(row.Key) is not { } resource)
        {
            return;
        }

        // Lo stesso quadrante una seconda volta CHIUDE: e' il gesto che chiunque prova by
        // primo by far sparire una cosa che ha appena fatto comparire. Un altro quadrante
        // invece cambia machineList senza chiudere.
        if (IsProcessPanelOpen && string.Equals(shownResource, resource, StringComparison.Ordinal))
        {
            CloseProcessPanel();

            return;
        }

        shownResource = resource;
        // "Whole machine" sta nel title perche' il quadrante da cui si arriva e' quello di UN
        // disco, e l'machineList non lo e': i contatori di I/O sono by processo, non by dispositivo.
        ProcessesTitle = resource switch
        {
            "memory" => "Top processes by memory",
            "io" => "Top processes by I/O (whole machine)",
            _ => "Top processes by CPU",
        };

        IsProcessPanelOpen = true;
        IsAwaitingEndConfirmation = false;
        ProcessesProblem = string.Empty;

        await RefreshProcessesAsync(CancellationToken.None);
    }

    /// <summary>Copia negli appunti cio' che dice la barra di state.</summary>
    /// <returns>L'timer della scrittura negli appunti.</returns>
    /// <remarks>
    /// E' il caso che pesa: un message d'error lungo — un'impronta che non corrisponde, con
    /// le due impronte by intero — altrimenti va ricopiato a mano by incollarlo in una
    /// ricerca. Il title e il message su due rows, perche' sono due frasi.
    /// <para>
    /// <c>AllowConcurrentExecutions</c> non e' decorazione: un <c>AsyncRelayCommand</c> in
    /// esecuzione si disabilita e rifiuta ogni altra chiamata, quindi un secondo clic mentre
    /// gli appunti stanno scrivendo cadrebbe nel vuoto con il pulsante che lampeggia spento.
    /// E' il difetto gia' pagato dai sei pulsanti dei quadranti.
    /// </para>
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopy))]
    private Task CopyStatusAsync() =>
        WriteToClipboardAsync(StatusTitle + Environment.NewLine + StatusText);

    /// <summary>Copia negli appunti il riepilogo di cio' che e' successo mentre nessuno guardava.</summary>
    /// <returns>L'timer della scrittura negli appunti.</returns>
    /// <remarks>
    /// Il caso che pesa e' una row by macchina con date e durate: e' esattamente il text che
    /// si incolla in un message a chi tiene quella macchina, e ricopiarlo a mano da un
    /// riquadro e' come ricopiare un'impronta. Stessa fila degli altri due Copy, stesso
    /// <c>AllowConcurrentExecutions</c>, stessa ragione.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopyAwaySummary))]
    private Task CopyAwaySummaryAsync() => WriteToClipboardAsync(AwaySummaryText);

    /// <summary>True quando c'e' un riepilogo da copiare.</summary>
    private bool CanCopyAwaySummary() => copyToClipboard is not null && AwaySummaryText.Length > 0;

    /// <summary>Copia negli appunti la row di processo selezionata, col entryClient PID.</summary>
    /// <returns>L'timer della scrittura negli appunti.</returns>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanCopyRow))]
    private Task CopyProcessRowAsync() =>
        SelectedProcess is { } process ? WriteToClipboardAsync(process.ForClipboard) : Task.CompletedTask;

    /// <summary>Scrive negli appunti, e non lascia che un loro guasto si veda altrove.</summary>
    /// <param name="text">Cio' che va negli appunti.</param>
    /// <returns>L'timer della scrittura.</returns>
    /// <remarks>
    /// Un guasto degli appunti non ha dove dirsi: l'unico posto sarebbe la barra di state, che
    /// e' proprio cio' che si sta copiando, e sovrascriverla cancellerebbe il message.
    /// Meglio non fare niente che perdere il text by raccontare che non si e' riusciti a
    /// copiarlo.
    /// </remarks>
    private Task WriteToClipboardAsync(string text)
    {
        if (copyToClipboard is not { } copy)
        {
            return Task.CompletedTask;
        }

        // IN FILA, una dopo l'altra. Gli appunti di Windows possono essere tenuti da un altro
        // programma, e Avalonia in quel caso riprova dieci volte a cento millisecondi l'una:
        // due scritture partite a poca distanza hanno due cicli di ritentativo indipendenti, e
        // vince quella che RIESCE by ultima, non quella che si e' chiesta by ultima. Senza
        // fila, un secondo clic puo' lasciare negli appunti il text del primo — misurato, e in
        // silenzio. Tutto gira sul thread dell'interfaccia, quindi la fila non ha bisogno di
        // serrature: basta incatenare i Task.
        clipboardQueue = WriteQueuedAsync(clipboardQueue, copy, text);

        return clipboardQueue;
    }

    /// <summary>Aspetta la scrittura previousWrite, poi scrive. Non lancia mai.</summary>
    /// <param name="previousWrite">La scrittura da aspettare.</param>
    /// <param name="copy">Come scrivere.</param>
    /// <param name="text">Cosa scrivere.</param>
    /// <returns>L'timer della propria scrittura.</returns>
    /// <remarks>
    /// Che non lanci mai e' cio' che rende sicuro aspettarla dalla chiamata successiva: una
    /// scrittura fallita non deve trascinarsi dietro quelle dopo.
    /// </remarks>
    private static async Task WriteQueuedAsync(Task previousWrite, Func<string, Task> copy, string text)
    {
        await previousWrite;

        try
        {
            await copy(text);
        }
#pragma warning disable CA1031 // Gli appunti possono essere tenuti da un altro programma: e'
        catch (Exception) // un fallimento del sistema, non un guasto della dashboard.
#pragma warning restore CA1031
        {
            // Niente. Un guasto degli appunti non ha dove dirsi: l'unico posto sarebbe la
            // barra di state, che e' proprio cio' che si sta copiando.
        }
    }

    /// <summary>Chiude il pannello e dimentica cosa c'era dentro.</summary>
    [RelayCommand]
    private void CloseProcessPanel()
    {
        IsProcessPanelOpen = false;
        shownResource = null;
        SelectedProcess = null;
        IsAwaitingEndConfirmation = false;
        ProcessesProblem = string.Empty;
        Processes.Clear();
    }

    /// <summary>Termina il processo selectedPid, chiedendo conferma al primo clic.</summary>
    /// <returns>L'timer della query e della rilettura.</returns>
    [RelayCommand]
    private async Task EndSelectedProcessAsync()
    {
        if (client is null || SelectedProcess is not { } process)
        {
            return;
        }

        // Primo clic: arma soltanto. Il pulsante cambia text, e chi ha cliccato by sbaglio
        // se ne accorge prima che succeda qualcosa.
        if (!IsAwaitingEndConfirmation)
        {
            IsAwaitingEndConfirmation = true;

            return;
        }

        IsAwaitingEndConfirmation = false;

        KillFetch fetch = await client.KillProcessAsync(process.Pid, CancellationToken.None);

        ProcessesProblem = fetch.Outcome == ServiceOutcome.Ok ? string.Empty : fetch.Problem;

        await RefreshProcessesAsync(CancellationToken.None);
    }

    /// <summary>Cambiare row disarma la conferma.</summary>
    /// <param name="value">La row appena selezionata.</param>
    /// <remarks>
    /// Senza, una conferma armata su un processo resterebbe armata dopo aver selectedPid un
    /// altro processo, e il secondo clic terminerebbe quello sbagliato.
    /// </remarks>
    partial void OnSelectedProcessChanged(ProcessRowState? value)
    {
        IsAwaitingEndConfirmation = false;
        CanEndProcess = value is not null;
    }

    private async Task RefreshProcessesAsync(CancellationToken cancellationToken)
    {
        if (client is null || shownResource is not { } resource)
        {
            return;
        }

        ProcessFetch fetch = await client.GetProcessesAsync(resource, ProcessRowCount, cancellationToken);

        // Mentre la risposta era in volo il pannello puo' essere state chiuso, o portato su
        // un'altra resource: questa risposta allora non e' piu' di nessuno. Applicarla
        // riempirebbe un pannello chiuso, o metterebbe le rows della CPU sotto il title
        // della memoria.
        if (!IsProcessPanelOpen || !string.Equals(shownResource, resource, StringComparison.Ordinal))
        {
            return;
        }

        if (fetch.Outcome != ServiceOutcome.Ok)
        {
            ProcessesProblem = fetch.Problem;

            return;
        }

        ProcessesProblem = string.Empty;

        // La selezione si tiene sul PID e non sull'oggetto: le rows arrivano nuove a ogni
        // giro, e senza questo la selezione si perderebbe una volta al secondo — cioe' proprio
        // mentre si sta puntando il processo da terminare.
        int? selectedPid = SelectedProcess?.Pid;

        Processes.Clear();

        foreach (ProcessRowState row in fetch.Processes)
        {
            Processes.Add(row);
        }

        SelectedProcess = selectedPid is { } pid
            ? Processes.FirstOrDefault(row => row.Pid == pid)
            : null;
    }

    private bool HasSameCollectors(IReadOnlyList<MetricGroupState> states)
    {
        if (Groups.Count != states.Count)
        {
            return false;
        }

        for (int i = 0; i < states.Count; i++)
        {
            if (!string.Equals(Groups[i].CollectorId, states[i].CollectorId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}