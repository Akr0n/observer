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

        // La coda vince dove le due si sovrappongono: e' la lettura piu' fresca, e su un
        // intervallo che l'aggregato ha consolidato solo a meta' sarebbe l'aggregato a mentire.
        foreach (HistoryPoint punto in coda)
        {
            uniti[punto.Timestamp] = punto;
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
}