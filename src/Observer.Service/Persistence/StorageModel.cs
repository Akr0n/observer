using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Le ampiezze dei livelli di aggregazione, in secondi. Sono costanti e non configurabili
/// perche' finiscono DENTRO il database, in una colonna: cambiarle a caldo renderebbe
/// illeggibile cio' che e' gia' stato scritto.
/// </summary>
public static class BucketWidths
{
    /// <summary>Il grezzo. Non e' un bucket: e' il campione cosi' com'e' stato letto.</summary>
    public const int RawSeconds = 1;

    /// <summary>Primo livello di aggregazione: un minuto.</summary>
    public const int MinuteSeconds = 60;

    /// <summary>Secondo livello di aggregazione: cinque minuti.</summary>
    public const int FiveMinuteSeconds = 300;
}

/// <summary>Una serie presente nello storico.</summary>
/// <param name="Key">La terna che la identifica.</param>
/// <param name="Kind">Il tipo di valore con cui e' stata registrata la prima volta.</param>
public sealed record StoredSeries(SeriesKey Key, MetricValueKind Kind);

/// <summary>
/// Un punto dello storico. Ha la STESSA forma sia per il grezzo sia per gli aggregati: sul
/// grezzo conteggio vale 1 e media, minimo, massimo e ultimo coincidono con il valore letto.
/// </summary>
/// <remarks>
/// La forma unica non e' pigrizia: e' cio' che permette al client di cambiare risoluzione
/// senza cambiare codice di disegno. Se il grezzo avesse una forma diversa, ogni grafico
/// dovrebbe avere due rami e uno dei due sarebbe sempre quello meno collaudato.
/// </remarks>
/// <param name="Timestamp">Istante del campione, o inizio del bucket, in UTC.</param>
/// <param name="Count">Quanti campioni grezzi ci sono dentro.</param>
/// <param name="Average">Media dei campioni.</param>
/// <param name="Min">Valore minimo.</param>
/// <param name="Max">Valore massimo.</param>
/// <param name="Last">Ultimo valore in ordine di tempo.</param>
public sealed record HistoryPoint(
    DateTimeOffset Timestamp,
    int Count,
    double Average,
    double Min,
    double Max,
    double Last);

/// <summary>Quanto occupa lo storico e fin dove e' stato consolidato.</summary>
/// <param name="DatabasePath">Percorso del file.</param>
/// <param name="FileSizeBytes">Dimensione del file, WAL compreso.</param>
/// <param name="SeriesCount">Numero di serie distinte.</param>
/// <param name="RawSamples">Campioni grezzi ancora presenti.</param>
/// <param name="MinuteBuckets">Bucket da un minuto presenti.</param>
/// <param name="FiveMinuteBuckets">Bucket da cinque minuti presenti.</param>
/// <param name="MinuteConsolidatedThrough">Fin dove il livello a un minuto ha aggregato.</param>
/// <param name="FiveMinuteConsolidatedThrough">Fin dove il livello a cinque minuti ha aggregato.</param>
public sealed record StorageStats(
    string DatabasePath,
    long FileSizeBytes,
    long SeriesCount,
    long RawSamples,
    long MinuteBuckets,
    long FiveMinuteBuckets,
    DateTimeOffset? MinuteConsolidatedThrough,
    DateTimeOffset? FiveMinuteConsolidatedThrough);

/// <summary>Esito di un giro di manutenzione.</summary>
/// <param name="MinuteBucketsWritten">Bucket da un minuto scritti o riscritti.</param>
/// <param name="FiveMinuteBucketsWritten">Bucket da cinque minuti scritti o riscritti.</param>
/// <param name="RawRowsPurged">Campioni grezzi cancellati.</param>
/// <param name="MinuteRowsPurged">Bucket da un minuto cancellati.</param>
/// <param name="FiveMinuteRowsPurged">Bucket da cinque minuti cancellati.</param>
public sealed record MaintenanceReport(
    int MinuteBucketsWritten,
    int FiveMinuteBucketsWritten,
    int RawRowsPurged,
    int MinuteRowsPurged,
    int FiveMinuteRowsPurged);
