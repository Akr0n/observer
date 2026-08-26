namespace Observer.Service.Persistence;

/// <summary>
/// La matematica del rollup, senza database. E' logica pura di proposito: il rollup e' il
/// punto in cui un errore non fa fallire nulla e produce numeri plausibili ma falsi, e
/// quella classe di bug si scopre solo con test che confrontano l'aggregato con il calcolo
/// diretto sui campioni grezzi.
/// </summary>
public static class RollupMath
{
    /// <summary>
    /// Riporta un istante all'inizio del bucket che lo contiene.
    /// </summary>
    /// <param name="timestampMs">Istante, in millisecondi da Unix epoch (UTC).</param>
    /// <param name="bucketWidth">Ampiezza del bucket.</param>
    /// <returns>L'inizio del bucket, in millisecondi da Unix epoch (UTC).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se l'ampiezza non e' positiva.</exception>
    public static long AlignToBucketStart(long timestampMs, TimeSpan bucketWidth)
    {
        long widthMs = RequireWidthMs(bucketWidth, nameof(bucketWidth));
        long remainder = timestampMs % widthMs;

        // La divisione intera del C# tronca verso lo zero, non verso il basso: senza questa
        // correzione un istante negativo finirebbe nel bucket successivo invece che nel
        // precedente. Qui serve un floor vero.
        return remainder >= 0 ? timestampMs - remainder : timestampMs - remainder - widthMs;
    }

    /// <summary>Aggrega campioni grezzi in bucket della larghezza richiesta.</summary>
    /// <param name="samples">I campioni, in qualunque ordine.</param>
    /// <param name="bucketWidth">Ampiezza dei bucket da produrre.</param>
    /// <returns>I bucket ottenuti, ordinati per istante di inizio crescente.</returns>
    public static IReadOnlyList<RollupBucket> Aggregate(IEnumerable<RawSample> samples, TimeSpan bucketWidth)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // Un campione grezzo E' un bucket da un campione: passando per la stessa
        // ricombinazione, "grezzo -> 1 minuto" e "1 minuto -> 5 minuti" non possono
        // divergere, perche' sono letteralmente lo stesso codice.
        return Combine(
            samples.Select(sample => RollupBucket.FromSample(sample.TimestampMs, sample.Value)),
            bucketWidth);
    }

    /// <summary>Ricombina bucket stretti in bucket piu' larghi.</summary>
    /// <param name="buckets">I bucket di partenza, in qualunque ordine.</param>
    /// <param name="targetWidth">Ampiezza dei bucket da produrre.</param>
    /// <returns>I bucket ottenuti, ordinati per istante di inizio crescente.</returns>
    public static IReadOnlyList<RollupBucket> Combine(IEnumerable<RollupBucket> buckets, TimeSpan targetWidth)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        RequireWidthMs(targetWidth, nameof(targetWidth));

        Dictionary<long, Accumulator> byBucketStart = [];

        foreach (RollupBucket bucket in buckets)
        {
            long start = AlignToBucketStart(bucket.BucketStartMs, targetWidth);

            if (byBucketStart.TryGetValue(start, out Accumulator? accumulator))
            {
                accumulator.Add(bucket);
            }
            else
            {
                byBucketStart[start] = new Accumulator(bucket);
            }
        }

        // L'ordine di un Dictionary non e' definito: senza questo ordinamento i punti
        // arriverebbero al grafico mescolati, e un grafico con l'asse dei tempi mescolato
        // sembra rumore di misura invece che un bug.
        return byBucketStart
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value.ToBucket(entry.Key))
            .ToList();
    }

    private static long RequireWidthMs(TimeSpan width, string paramName)
    {
        long widthMs = (long)width.TotalMilliseconds;

        if (widthMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                width,
                "L'ampiezza di un bucket deve essere positiva: con zero la divisione per allinearlo non esiste.");
        }

        return widthMs;
    }

    /// <summary>
    /// Accumula i bucket di uno stesso intervallo. Mutabile e privato di proposito: il tipo
    /// pubblico <see cref="RollupBucket"/> resta immutabile e valido per costruzione.
    /// </summary>
    private sealed class Accumulator
    {
        private int count;
        private double sum;
        private double min;
        private double max;
        private double last;
        private long lastSourceStartMs;

        public Accumulator(RollupBucket first)
        {
            count = first.Count;
            sum = first.Sum;
            min = first.Min;
            max = first.Max;
            last = first.Last;
            lastSourceStartMs = first.BucketStartMs;
        }

        public void Add(RollupBucket bucket)
        {
            count += bucket.Count;
            sum += bucket.Sum;
            min = Math.Min(min, bucket.Min);
            max = Math.Max(max, bucket.Max);

            // "Ultimo" significa piu' recente, non ultimo arrivato: l'ordine della sorgente
            // non deve poter cambiare il valore corrente mostrato in dashboard.
            if (bucket.BucketStartMs >= lastSourceStartMs)
            {
                last = bucket.Last;
                lastSourceStartMs = bucket.BucketStartMs;
            }
        }

        public RollupBucket ToBucket(long bucketStartMs) =>
            new(bucketStartMs, count, sum, min, max, last);
    }
}
