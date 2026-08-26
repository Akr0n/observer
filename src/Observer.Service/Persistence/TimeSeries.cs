using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Identita' di una serie temporale: la terna che distingue un numero da un altro.
/// </summary>
/// <param name="CollectorId">Chi ha prodotto il valore, per esempio "cpu".</param>
/// <param name="MetricId">Quale metrica, per esempio "cpu.usage.total".</param>
/// <param name="Instance">
/// Il core, il disco, l'interfaccia. STRINGA VUOTA, non null, quando la metrica e' unica per
/// macchina: in SQLite due NULL non sono considerati uguali in un indice UNIQUE, quindi con
/// null la stessa serie verrebbe reinserita a ogni campionamento e lo storico si
/// spezzerebbe in migliaia di serie da un punto ciascuna.
/// </param>
public readonly record struct SeriesKey(string CollectorId, string MetricId, string Instance);

/// <summary>Un valore numerico appiattito da uno snapshot, pronto per essere scritto.</summary>
/// <param name="Key">La serie a cui appartiene.</param>
/// <param name="Kind">Il ramo di <see cref="MetricValue"/> da cui proviene.</param>
/// <param name="TimestampMs">Istante del campionamento, in millisecondi da Unix epoch (UTC).</param>
/// <param name="Value">Il valore numerico.</param>
public readonly record struct SeriesSample(SeriesKey Key, MetricValueKind Kind, long TimestampMs, double Value);

/// <summary>Un campione grezzo di UNA serie, gia' privo dell'identita' della serie.</summary>
/// <param name="TimestampMs">Istante del campionamento, in millisecondi da Unix epoch (UTC).</param>
/// <param name="Value">Il valore misurato.</param>
public readonly record struct RawSample(long TimestampMs, double Value);

/// <summary>
/// Un intervallo di tempo aggregato di UNA serie.
/// </summary>
/// <remarks>
/// Conserva SOMMA e CONTEGGIO invece della media gia' calcolata, ed e' la decisione centrale
/// di tutto il rollup. Ricombinando cinque bucket da un minuto in uno da cinque, la media
/// delle medie e' sbagliata ogni volta che i bucket non hanno lo stesso numero di campioni —
/// e non hanno lo stesso numero ogni volta che il servizio riparte, che un collector va in
/// timeout o che una metrica compare a meta' minuto. Il risultato sarebbe un numero
/// plausibile e falso. Con somma e conteggio la media a cinque minuti coincide, cifra per
/// cifra, con la media dei campioni grezzi.
/// </remarks>
public sealed record RollupBucket
{
    /// <summary>Crea un bucket aggregato.</summary>
    /// <param name="bucketStartMs">Inizio dell'intervallo, allineato alla sua ampiezza.</param>
    /// <param name="count">Numero di campioni grezzi confluiti qui dentro.</param>
    /// <param name="sum">Somma dei valori.</param>
    /// <param name="min">Valore minimo.</param>
    /// <param name="max">Valore massimo.</param>
    /// <param name="last">Ultimo valore in ordine di tempo.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Se il conteggio non e' positivo, oppure se un valore non e' finito. Entrambi i casi
    /// producono JSON non serializzabile: un bucket vuoto ha media 0/0 = NaN, e un NaN nella
    /// risposta non perde una metrica, perde l'INTERA risposta HTTP.
    /// </exception>
    public RollupBucket(long bucketStartMs, int count, double sum, double min, double max, double last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        RequireFinite(sum, nameof(sum));
        RequireFinite(min, nameof(min));
        RequireFinite(max, nameof(max));
        RequireFinite(last, nameof(last));

        BucketStartMs = bucketStartMs;
        Count = count;
        Sum = sum;
        Min = min;
        Max = max;
        Last = last;
    }

    /// <summary>Inizio dell'intervallo, in millisecondi da Unix epoch (UTC).</summary>
    public long BucketStartMs { get; }

    /// <summary>Numero di campioni grezzi aggregati. Sempre almeno 1.</summary>
    public int Count { get; }

    /// <summary>Somma dei valori aggregati.</summary>
    public double Sum { get; }

    /// <summary>Valore minimo nell'intervallo.</summary>
    public double Min { get; }

    /// <summary>Valore massimo nell'intervallo.</summary>
    public double Max { get; }

    /// <summary>Ultimo valore dell'intervallo, in ordine di tempo.</summary>
    public double Last { get; }

    /// <summary>Media dei campioni aggregati.</summary>
    public double Average => Sum / Count;

    /// <summary>
    /// Il bucket degenere che rappresenta un singolo campione grezzo. Serve a far passare
    /// grezzi e aggregati per la STESSA ricombinazione, invece di scriverne due versioni che
    /// possono divergere.
    /// </summary>
    /// <param name="timestampMs">Istante del campione, in millisecondi da Unix epoch (UTC).</param>
    /// <param name="value">Il valore misurato.</param>
    /// <returns>Un bucket con conteggio 1.</returns>
    public static RollupBucket FromSample(long timestampMs, double value) =>
        new(timestampMs, 1, value, value, value, value);

    private static void RequireFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "Un valore non finito non e' rappresentabile in JSON e farebbe fallire l'intera risposta.");
        }
    }
}
