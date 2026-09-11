using System.Globalization;

namespace Observer.App.Services;

/// <summary>Che cosa si sa di un intervallo della striscia.</summary>
public enum BarKind
{
    /// <summary>Nessun campione: in quell'intervallo la macchina non stava misurando.</summary>
    Assente = 0,

    /// <summary>Qualche campione, ma non tutti: coperto solo in parte.</summary>
    Parziale = 1,

    /// <summary>Intervallo coperto per intero.</summary>
    Misurata = 2,
}

/// <summary>Un intervallo della striscia dello storico.</summary>
/// <param name="Inizio">L'istante da cui parte l'intervallo.</param>
/// <param name="Genere">Quanto se ne sa.</param>
/// <param name="Media">La media dei campioni, da 0 a 1. Zero quando non ce ne sono.</param>
/// <param name="Massimo">Il massimo raggiunto, da 0 a 1.</param>
/// <param name="Minimo">Il minimo toccato, da 0 a 1.</param>
/// <param name="Campioni">Quanti campioni sono caduti nell'intervallo.</param>
/// <param name="Attesi">Quanti ne sarebbero caduti se fosse stato coperto per intero.</param>
public sealed record HistoryBar(
    DateTimeOffset Inizio,
    BarKind Genere,
    double Media,
    double Massimo,
    double Minimo,
    int Campioni,
    int Attesi);

/// <summary>
/// Da cio' che il servizio manda a cio' che si disegna: la griglia degli intervalli.
/// </summary>
/// <remarks>
/// <b>Il servizio non manda i buchi.</b> Un intervallo in cui non e' stato campionato niente
/// semplicemente non compare nell'array dei punti — non arriva con zero campioni, non arriva
/// affatto. Misurato uccidendo il servizio per 95 secondi: al livello di un minuto il bucket
/// di quel minuto non esiste, e l'array salta direttamente al successivo.
/// <para>
/// Da qui la regola che questa classe esiste per far rispettare: <b>la griglia si costruisce
/// dai tempi attesi, e i punti ci si cercano dentro</b>, mai il contrario. Scorrere l'array e
/// disegnare una barretta per punto darebbe una striscia continua e piena anche su una
/// macchina spenta meta' giornata: i buchi sparirebbero stringendosi, e chi guarda leggerebbe
/// una macchina sempre accesa. E' il modo piu' facile di raccontare una bugia con dei dati
/// veri.
/// </para>
/// <para>
/// Un intervallo coperto solo in parte esiste ed e' un terzo caso: arriva con un numero di
/// campioni ridotto (misurati 53 e 31 su 60) e con una media calcolata solo su quelli. E' un
/// numero plausibile su mezzo minuto, e va detto che e' mezzo minuto.
/// </para>
/// </remarks>
public static class HistoryStrip
{
    /// <summary>Quanti campioni ci si aspetta in un intervallo, a un campione al secondo.</summary>
    /// <param name="passo">La durata dell'intervallo.</param>
    /// <returns>Il numero di campioni attesi, almeno uno.</returns>
    /// <remarks>
    /// Il servizio campiona a 1 Hz, quindi i campioni attesi coincidono con i secondi. E'
    /// misurato: gli intervalli pieni arrivano con 60 campioni al minuto e 300 a cinque minuti.
    /// </remarks>
    public static int AttesiIn(TimeSpan passo) => Math.Max(1, (int)Math.Round(passo.TotalSeconds));

    /// <summary>Costruisce la striscia, buchi compresi.</summary>
    /// <param name="punti">I punti arrivati dal servizio, in qualsiasi ordine.</param>
    /// <param name="fine">La fine della finestra: l'ultimo intervallo e' quello che la contiene.</param>
    /// <param name="quanti">Quanti intervalli mostrare.</param>
    /// <param name="passo">Quanto dura ciascun intervallo.</param>
    /// <returns>Gli intervalli dal piu' vecchio al piu' recente, uno per posizione.</returns>
    public static IReadOnlyList<HistoryBar> Costruisci(
        IReadOnlyList<HistoryPoint> punti,
        DateTimeOffset fine,
        int quanti,
        TimeSpan passo)
    {
        ArgumentNullException.ThrowIfNull(punti);
        ArgumentOutOfRangeException.ThrowIfLessThan(quanti, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(passo, TimeSpan.Zero);

        int attesi = AttesiIn(passo);

        // PRIMA si raggruppa, e non e' un di piu': quando il passo della barra e' piu' largo
        // di quello dei punti - un quarto d'ora di barra su punti da cinque minuti - nello
        // stesso intervallo ne cadono tre, e indicizzarli per istante ne terrebbe UNO,
        // l'ultimo iterato, buttando gli altri due. La barra mostrerebbe l'ultimo campione
        // spacciandolo per la media di tutti, e il conteggio direbbe 1 su 900. Raggruppa
        // ricalcola la media dalla SOMMA, che e' l'unico modo di non far pesare uguale
        // intervalli con un numero diverso di campioni.
        punti = Raggruppa(punti, passo);

        // I punti si indicizzano per l'inizio del proprio intervallo, arrotondato al passo:
        // cosi' un timestamp che arriva con qualche millisecondo di scarto cade lo stesso
        // nella casella giusta invece di sparire.
        Dictionary<DateTimeOffset, HistoryPoint> perIstante = [];

        foreach (HistoryPoint punto in punti)
        {
            perIstante[Allinea(punto.Timestamp, passo)] = punto;
        }

        DateTimeOffset ultimo = Allinea(fine, passo);
        List<HistoryBar> striscia = new(quanti);

        for (int i = quanti - 1; i >= 0; i--)
        {
            DateTimeOffset inizio = ultimo - (passo * i);

            striscia.Add(perIstante.TryGetValue(inizio, out HistoryPoint? punto)
                ? Da(punto, inizio, attesi)
                : new HistoryBar(inizio, BarKind.Assente, 0d, 0d, 0d, 0, attesi));
        }

        return striscia;
    }

    /// <summary>Quanto e' larga davvero una barra, in proporzione a quanto ha coperto.</summary>
    /// <param name="barra">La barra da disegnare.</param>
    /// <param name="larghezza">La larghezza piena della colonna.</param>
    /// <returns>La larghezza da disegnare, mai sotto un pixel.</returns>
    /// <remarks>
    /// Serve per l'ULTIMA barra della striscia, che e' sempre l'intervallo <b>in corso</b>: a
    /// passo di un minuto contiene fra zero e sessanta secondi di misure e la differenza non
    /// si nota, ma a passo di due ore puo' contenerne cinque minuti e disegnarsi identica a
    /// una barra piena — proprio dove l'occhio legge "adesso". Una barra che ha coperto un
    /// dodicesimo del suo intervallo si disegna larga un dodicesimo.
    /// <para>
    /// Quanto la si veda crescere dipende da chi rilegge, non da qui, ed e' il motivo per cui
    /// <c>MainViewModel.ProssimaLettura</c> non rilegge ogni passo: rileggendo al passo si
    /// guarderebbe ogni volta una barra appena nata, sempre alla stessa frazione, e l'estremo
    /// destro della striscia resterebbe congelato per tutta la sessione a quella larghezza li'.
    /// </para>
    /// <para>
    /// Vale per ogni barra parziale, non solo per l'ultima: anche a meta' striscia, un
    /// intervallo coperto a meta' sa meno di uno coperto per intero, e la larghezza lo dice
    /// senza bisogno di un terzo colore. Le barre intere e i buchi non si toccano: un buco ha
    /// gia' il suo segno, e stringere una barra piena sarebbe una bugia al contrario.
    /// </para>
    /// </remarks>
    public static double LarghezzaDi(HistoryBar barra, double larghezza)
    {
        ArgumentNullException.ThrowIfNull(barra);

        if (barra.Genere != BarKind.Parziale || barra.Attesi <= 0)
        {
            return larghezza;
        }

        double coperta = Math.Clamp((double)barra.Campioni / barra.Attesi, 0d, 1d);

        // Almeno un pixel: una barra che esiste non deve sparire del tutto, o si leggerebbe
        // come un buco, che vuol dire un'altra cosa.
        return Math.Max(1d, larghezza * coperta);
    }

    /// <summary>Raggruppa campioni fitti in intervalli piu' larghi.</summary>
    /// <param name="punti">I punti da raggruppare.</param>
    /// <param name="passo">La durata dell'intervallo di destinazione.</param>
    /// <returns>Un punto per intervallo che contiene almeno un campione.</returns>
    /// <remarks>
    /// Serve per la coda della striscia, che si legge dai campioni grezzi. <b>La media si
    /// ricalcola dalla somma, non come media delle medie</b>: intervalli con un numero diverso
    /// di campioni peserebbero uguale, e ne uscirebbe un numero credibile e falso.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Raggruppa(
        IReadOnlyList<HistoryPoint> punti,
        TimeSpan passo)
    {
        ArgumentNullException.ThrowIfNull(punti);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(passo, TimeSpan.Zero);

        Dictionary<DateTimeOffset, (double Somma, double Min, double Max, int Conta)> accumulo = [];

        foreach (HistoryPoint punto in punti)
        {
            DateTimeOffset dove = Allinea(punto.Timestamp, passo);
            int conta = Math.Max(1, punto.Count);

            if (accumulo.TryGetValue(dove, out (double Somma, double Min, double Max, int Conta) gia))
            {
                accumulo[dove] = (
                    gia.Somma + (punto.Avg * conta),
                    Math.Min(gia.Min, punto.Min),
                    Math.Max(gia.Max, punto.Max),
                    gia.Conta + conta);
            }
            else
            {
                accumulo[dove] = (punto.Avg * conta, punto.Min, punto.Max, conta);
            }
        }

        return [.. accumulo
            .OrderBy(voce => voce.Key)
            .Select(voce => new HistoryPoint(
                voce.Key,
                voce.Value.Conta,
                voce.Value.Somma / voce.Value.Conta,
                voce.Value.Min,
                voce.Value.Max,
                voce.Value.Somma / voce.Value.Conta))];
    }

    /// <summary>Unisce due letture della stessa serie, tenendo la piu' fresca dove si sovrappongono.</summary>
    /// <param name="aggregato">La lettura che copre tutta la finestra, ma e' indietro.</param>
    /// <param name="coda">La lettura fresca degli ultimi intervalli.</param>
    /// <returns>I punti uniti.</returns>
    /// <remarks>
    /// La striscia si costruisce con DUE letture, e il motivo e' misurato: il consolidamento
    /// degli aggregati ha una grazia di quattro minuti, quindi il livello a un minuto e'
    /// indietro di cinque o sei minuti rispetto ad adesso. Con la sola lettura aggregata le
    /// ultime barrette sarebbero <b>sempre</b> vuote, e la striscia direbbe "non misurato"
    /// proprio sull'adesso — mentre i quadranti sopra mostrano valori vivi. La coda arriva dal
    /// grezzo, che e' aggiornato al secondo.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Unisci(
        IReadOnlyList<HistoryPoint> aggregato,
        IReadOnlyList<HistoryPoint> coda)
    {
        ArgumentNullException.ThrowIfNull(aggregato);
        ArgumentNullException.ThrowIfNull(coda);

        Dictionary<DateTimeOffset, HistoryPoint> uniti = [];

        foreach (HistoryPoint punto in aggregato)
        {
            uniti[punto.Timestamp] = punto;
        }

        // Dove le due si sovrappongono vince quella con PIU' campioni, non la piu' fresca.
        // Quasi sempre e' la coda, ed e' il motivo per cui questa funzione esiste: su un
        // intervallo consolidato a meta' l'aggregato ha meno campioni e mentirebbe. Ma c'e'
        // un intervallo in cui perde, ed e' sempre lo stesso: il piu' VECCHIO della coda. Il
        // grezzo si chiede da un istante qualsiasi - "dieci minuti fa" - che non cade sul
        // confine di un intervallo, quindi quel primo intervallo arriva tagliato, con trenta
        // campioni su sessanta, mentre l'aggregato ce li ha tutti. Lasciandolo vincere, una
        // barra misurata per intero si disegnava larga la meta' (e' parziale), il suggerimento
        // diceva "30 of 60 samples", e media, minimo e massimo saltavano la prima meta' del
        // minuto: un picco li' dentro spariva. Il confine si sposta a ogni lettura, quindi la
        // barra sbagliata era sempre la stessa posizione della striscia.
        foreach (HistoryPoint punto in coda)
        {
            uniti[punto.Timestamp] =
                uniti.TryGetValue(punto.Timestamp, out HistoryPoint? gia) && gia.Count > punto.Count
                    ? gia
                    : punto;
        }

        return [.. uniti.Values.OrderBy(punto => punto.Timestamp)];
    }

    private static HistoryBar Da(HistoryPoint punto, DateTimeOffset inizio, int attesi) =>
        new(
            inizio,
            punto.Count >= attesi ? BarKind.Misurata : BarKind.Parziale,
            punto.Avg,
            punto.Max,
            punto.Min,
            punto.Count,
            attesi);

    private static DateTimeOffset Allinea(DateTimeOffset istante, TimeSpan passo) =>
        new(istante.UtcTicks - (istante.UtcTicks % passo.Ticks), TimeSpan.Zero);

    /// <summary>Quale barra sta sotto una certa ascissa.</summary>
    /// <param name="x">Ascissa del puntatore, in pixel dal bordo sinistro della striscia.</param>
    /// <param name="larghezza">Larghezza dell'intera striscia.</param>
    /// <param name="quante">Quante barre ci sono.</param>
    /// <returns>L'indice, oppure -1 se il puntatore e' fuori.</returns>
    /// <remarks>
    /// Sta qui e non nel controllo per la stessa ragione dell'aritmetica dell'arco: un errore
    /// di un indice non fa fallire niente, mostra soltanto l'ora della barra accanto. E il
    /// bordo destro sbaglia da solo — con x uguale alla larghezza la divisione da'
    /// esattamente <c>quante</c>, cioe' un indice che non esiste.
    /// </remarks>
    public static int IndiceSotto(double x, double larghezza, int quante)
    {
        if (quante <= 0 || larghezza <= 0d || double.IsNaN(x) || x < 0d || x >= larghezza)
        {
            return -1;
        }

        return Math.Clamp((int)(x / (larghezza / quante)), 0, quante - 1);
    }

    /// <summary>Che cosa dire di una barra a chi ci passa sopra il mouse.</summary>
    /// <param name="barre">Le barre della striscia.</param>
    /// <param name="indice">Quale barra.</param>
    /// <returns>La frase da mostrare, vuota se l'indice non esiste.</returns>
    /// <remarks>
    /// Dice l'INTERVALLO, non l'istante: una barra copre da un minuto a due ore secondo il
    /// periodo scelto, e mostrarne solo l'inizio lascerebbe indovinare quanto e' larga. Il
    /// passo si ricava dalle barre stesse invece di essere una costante, cosi' resta vero
    /// qualunque periodo la striscia stia mostrando.
    /// <para>
    /// Su una barra vuota lo dice: "non misurato" non e' "zero", ed e' la stessa distinzione
    /// che il disegno gia' fa con il tratteggio.
    /// </para>
    /// </remarks>
    public static string Descrivi(IReadOnlyList<HistoryBar> barre, int indice)
    {
        ArgumentNullException.ThrowIfNull(barre);

        if (indice < 0 || indice >= barre.Count)
        {
            return string.Empty;
        }

        HistoryBar barra = barre[indice];

        if (barre.Count < 2)
        {
            return Ora(barra.Inizio, colGiorno: false);
        }

        TimeSpan passo = barre[1].Inizio - barre[0].Inizio;

        // Oltre le ventiquattro ore l'ora da sola non colloca piu' niente: a sette giorni la
        // stessa frase - "04:00 – 06:00" - compare su SETTE barre, una per giorno, e chi vede
        // un picco (che e' il motivo per cui si guarda una settimana) non ha modo di sapere di
        // che giorno sia. Il nome del giorno basta, la data no: fra due barre della stessa
        // striscia passano al massimo 83 x 2 h = 166 ore, meno di una settimana, quindi la
        // coppia (giorno, ora) non puo' ripetersi. La soglia e' stretta di proposito: a
        // ventiquattro ore l'arco vale esattamente un giorno, gli estremi non si toccano, e la
        // frase resta corta dove non serve allungarla.
        bool colGiorno = (passo * barre.Count) > TimeSpan.FromHours(24);

        string intervallo = Ora(barra.Inizio, colGiorno) + " – " + Ora(barra.Inizio + passo, colGiorno);

        return barra.Genere switch
        {
            BarKind.Assente => intervallo + " · not measured",
            BarKind.Parziale => intervallo + $" · {barra.Campioni} of {barra.Attesi} samples",
            _ => intervallo,
        };
    }

    private static string Ora(DateTimeOffset istante, bool colGiorno) =>
        istante.ToLocalTime().ToString(colGiorno ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}