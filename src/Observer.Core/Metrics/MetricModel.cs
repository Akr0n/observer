using System.Text.Json.Serialization;

namespace Observer.Core.Metrics;

/// <summary>
/// Unita' di misura come tipo APERTO, non come enum chiuso. E' la scelta che sostiene il
/// requisito "misurare qualsiasi parametro": il primo sensore in rpm, in volt o in giri
/// non deve costringere a modificare Observer.Core.
/// </summary>
/// <param name="Symbol">Simbolo dell'unita', per esempio "%", "byte", "rpm".</param>
public readonly record struct MetricUnit(string Symbol)
{
    /// <summary>Punti percentuali, da 0 a 100.</summary>
    public static MetricUnit Percent => new("%");

    /// <summary>Byte.</summary>
    public static MetricUnit Bytes => new("byte");

    /// <summary>Grandezza adimensionale (conteggi, flag).</summary>
    public static MetricUnit None => new(string.Empty);
}

/// <summary>Quale dei rami di <see cref="MetricValue"/> e' valorizzato.</summary>
public enum MetricValueKind
{
    /// <summary>Nessun ramo. E' default(MetricValueKind) e non deve mai indicare un valore reale.</summary>
    Unknown = 0,

    /// <summary>Valore numerico.</summary>
    Number = 1,

    /// <summary>Valore testuale (per esempio il modello di un disco).</summary>
    Text = 2,

    /// <summary>Valore booleano (per esempio "SMART ha segnalato un guasto").</summary>
    Flag = 3,
}

/// <summary>
/// Il payload uniforme che viaggia in rete. Un solo formato per CPU, RAM, temperature,
/// SMART e GPU: e' cio' che permette di aggiungere una sorgente senza toccare il trasporto
/// ne' il client.
/// </summary>
public readonly record struct MetricValue
{
    // [JsonConstructor] su un costruttore PRIVATO: System.Text.Json lo onora, e senza di
    // questo il tipo si serializza ma NON si rideserializza. Le proprieta' sono get-only,
    // quindi il deserializzatore userebbe il costruttore implicito della struct e
    // restituirebbe default(MetricValue) — kind=Unknown, number=0 — senza lanciare nulla:
    // il client mostrerebbe una macchina piena di zeri marcati "Ok" mentre curl sullo stesso
    // endpoint restituisce i numeri giusti. Il costruttore resta privato di proposito:
    // renderlo pubblico permetterebbe stati incoerenti come Kind=Number con Text valorizzato.
    [JsonConstructor]
    private MetricValue(MetricValueKind kind, double number, string? text, bool flag)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Flag = flag;
    }

    /// <summary>Ramo valorizzato.</summary>
    public MetricValueKind Kind { get; }

    /// <summary>Valore numerico, significativo solo se <see cref="Kind"/> e' Number.</summary>
    public double Number { get; }

    /// <summary>Valore testuale, significativo solo se <see cref="Kind"/> e' Text.</summary>
    public string? Text { get; }

    /// <summary>Valore booleano, significativo solo se <see cref="Kind"/> e' Flag.</summary>
    public bool Flag { get; }

    /// <summary>
    /// Costruisce un valore numerico. Lancia sui valori non finiti: un NaN accettato qui
    /// non perde una metrica, fa lanciare il serializzatore piu' tardi e perde l'INTERA
    /// risposta HTTP, tutte le altre metriche comprese. Meglio un errore rumoroso subito,
    /// nel punto in cui si vede quale collector lo ha prodotto.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Se il valore e' NaN o infinito.</exception>
    public static MetricValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Un valore non finito non e' rappresentabile in JSON e farebbe fallire l'intera risposta.");
        }

        return new MetricValue(MetricValueKind.Number, value, null, false);
    }

    /// <summary>Costruisce un valore testuale.</summary>
    public static MetricValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new MetricValue(MetricValueKind.Text, 0.0, value, false);
    }

    /// <summary>Costruisce un valore booleano.</summary>
    public static MetricValue FromFlag(bool value) =>
        new(MetricValueKind.Flag, 0.0, null, value);
}

/// <summary>
/// Un valore misurato, oppure il motivo per cui quel singolo valore manca.
/// </summary>
/// <remarks>
/// La diagnostica vive QUI e non solo sullo snapshot perche' altrimenti un collector con
/// piu' istanze non potrebbe esprimere il caso normale: tre dischi di cui uno dietro un
/// bridge USB che non inoltra i comandi SMART. Con un solo stato per collector resterebbero
/// due sole scelte, entrambe sbagliate — dichiarare tutto Ok facendo sparire in silenzio il
/// disco problematico, oppure dichiarare tutto Unavailable perdendo anche i dischi sani.
/// Si costruisce solo dalle fabbriche: cosi' non esiste un punto "Ok" senza valore ne' un
/// punto degradato senza spiegazione.
/// </remarks>
public sealed record MetricPoint
{
    // Come per MetricValue: privato ma annotato, altrimenti il tipo si serializza e non si
    // rideserializza, e il client riceve punti vuoti senza alcun errore.
    [JsonConstructor]
    private MetricPoint(
        string metricId,
        string? instance,
        MetricValue? value,
        CollectorStatus status,
        string? message)
    {
        MetricId = metricId;
        Instance = instance;
        Value = value;
        Status = status;
        Message = message;
    }

    /// <summary>Identificatore della metrica, per esempio "cpu.usage.total".</summary>
    public string MetricId { get; }

    /// <summary>
    /// Dimensione per istanza: il core, il disco, l'interfaccia di rete. E' una stringa e
    /// non una gerarchia di tipi, ed e' proprio questo a permettere che il per-core, il
    /// per-disco e il per-processo passino dalla stessa interfaccia senza modificarla.
    /// Null quando la metrica e' unica per macchina.
    /// </summary>
    public string? Instance { get; }

    /// <summary>Il valore, oppure null quando <see cref="Status"/> non e' Ok.</summary>
    public MetricValue? Value { get; }

    /// <summary>Esito della misura di QUESTA istanza, indipendente dalle altre.</summary>
    public CollectorStatus Status { get; }

    /// <summary>
    /// Spiegazione leggibile quando il valore manca. E' cio' che la dashboard mostra al
    /// posto del numero, invece di lasciare un buco muto.
    /// </summary>
    public string? Message { get; }

    /// <summary>Un valore letto correttamente.</summary>
    public static MetricPoint Measured(string metricId, string? instance, MetricValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);

        return new MetricPoint(metricId, instance, value, CollectorStatus.Ok, message: null);
    }

    /// <summary>Questa istanza non e' misurabile qui, e il motivo va mostrato.</summary>
    public static MetricPoint Unsupported(string metricId, string? instance, string reason) =>
        Missing(metricId, instance, CollectorStatus.Unsupported, reason);

    /// <summary>Questa istanza esiste ma ora non e' leggibile, e il motivo va mostrato.</summary>
    public static MetricPoint Unavailable(string metricId, string? instance, string reason) =>
        Missing(metricId, instance, CollectorStatus.Unavailable, reason);

    private static MetricPoint Missing(string metricId, string? instance, CollectorStatus status, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);

        // Il motivo e' obbligatorio: non deve essere possibile dichiarare un valore mancante
        // senza scrivere la frase che finira' in dashboard al suo posto.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new MetricPoint(metricId, instance, value: null, status, reason);
    }
}

/// <summary>
/// Metadati di una metrica. Viaggiano nel catalogo una sola volta, non a ogni campione, e
/// sono cio' che permette al client di disegnare una metrica contro cui non e' stato
/// compilato: sa che unita' ha, come si chiama e se e' per istanza.
/// </summary>
/// <param name="MetricId">Identificatore, deve coincidere con quello dei punti emessi.</param>
/// <param name="DisplayName">Nome leggibile da mostrare in dashboard.</param>
/// <param name="Unit">Unita' di misura.</param>
/// <param name="IsPerInstance">True se la metrica produce un punto per istanza.</param>
public sealed record MetricDescriptor(
    string MetricId,
    string DisplayName,
    MetricUnit Unit,
    bool IsPerInstance);

/// <summary>
/// Esito di una raccolta. E' la spina dorsale della degradazione graziosa: distinguere
/// questi casi e' cio' che permette alla dashboard di dire perche' un dato manca, invece
/// di mostrare un buco muto o, peggio, uno zero inventato.
/// </summary>
public enum CollectorStatus
{
    /// <summary>Nessun esito. Non deve mai spacciarsi per successo.</summary>
    Unknown = 0,

    /// <summary>Raccolta riuscita.</summary>
    Ok = 1,

    /// <summary>
    /// In avvio: manca ancora il secondo campione necessario a calcolare una differenza.
    /// E' legittimo e temporaneo, e va distinto da un guasto.
    /// </summary>
    Warmup = 2,

    /// <summary>La sorgente esiste su questa piattaforma ma ora non e' leggibile.</summary>
    Unavailable = 3,

    /// <summary>
    /// La metrica non e' misurabile su questa piattaforma. Diverso da "dimenticata": resta
    /// nel catalogo con la sua spiegazione.
    /// </summary>
    Unsupported = 4,

    /// <summary>La raccolta ha lanciato un'eccezione. Degrada questa metrica, non il servizio.</summary>
    Faulted = 5,
}

/// <summary>
/// Il risultato di una raccolta, in forma unica per tutti i collector.
/// </summary>
/// <param name="CollectorId">Chi ha prodotto lo snapshot.</param>
/// <param name="Status">Esito.</param>
/// <param name="Message">
/// Spiegazione leggibile quando <paramref name="Status"/> non e' Ok. E' cio' che finisce in
/// dashboard al posto del valore mancante.
/// </param>
/// <param name="Points">
/// I valori misurati. Vuoto quando l'esito non e' Ok. Un punto ASSENTE significa "non
/// applicabile qui": emettere uno zero al suo posto sarebbe fuorviante.
/// </param>
public sealed record MetricSnapshot(
    string CollectorId,
    CollectorStatus Status,
    string? Message,
    IReadOnlyList<MetricPoint> Points);

/// <summary>
/// Cio' che il servizio pubblica in rete a ogni campionamento: l'esito di tutti i collector
/// piu' l'istante in cui sono stati letti.
/// </summary>
/// <param name="SchemaVersion">
/// Versione del formato. Viaggia sul filo perche' un client e un servizio compilati da
/// commit diversi divergerebbero altrimenti in silenzio, con campi a zero invece che con un
/// messaggio leggibile.
/// </param>
/// <param name="CapturedAt">Istante del campionamento, in UTC.</param>
/// <param name="Collectors">Esito di ogni collector, compresi quelli degradati.</param>
public sealed record MachineSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    IReadOnlyList<MetricSnapshot> Collectors)
{
    /// <summary>Versione corrente del formato pubblicato.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// L'UNICO punto di estensione per aggiungere una sorgente di metriche. Nessun tipo qui
/// nomina una metrica specifica, quindi questo file non deve cambiare quando se ne aggiunge
/// una nuova: e' la proprieta' che regge il requisito "qualsiasi parametro".
/// </summary>
public interface IMetricCollector
{
    /// <summary>Identificatore univoco del collector, per esempio "cpu".</summary>
    string Id { get; }

    /// <summary>
    /// Le metriche che questo collector puo' emettere. Va dichiarato anche cio' che oggi
    /// non e' misurabile: e' la differenza fra "non si puo' qui" e "me la sono dimenticata".
    /// </summary>
    IReadOnlyList<MetricDescriptor> Descriptors { get; }

    /// <summary>
    /// Esegue una raccolta. Non deve lanciare per una sorgente assente o illeggibile:
    /// va restituito uno snapshot degradato con il motivo.
    /// </summary>
    ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken);
}
