namespace Observer.Core.Metrics.Disk;

/// <summary>
/// Lo spazio occupato sui volumi montati.
/// </summary>
/// <remarks>
/// E' il primo collector <b>per istanza</b>: un volume, un'istanza. La dimensione per istanza
/// e' un campo del punto e non una gerarchia di tipi, quindi qui non serve niente di nuovo —
/// era gia' previsto, e questa e' la prima sorgente che lo usa davvero.
/// <para>
/// Misura lo SPAZIO e non l'attivita' di lettura e scrittura. Non e' una dimenticanza:
/// l'attivita' si legge con <c>DeviceIoControl</c> e il marshalling di una struct, e porta
/// dentro trappole che vanno affrontate con calma — fra le altre, che la percentuale di tempo
/// occupato si calcola dall'INATTIVITA' e non sommando i tempi di lettura e scrittura, che si
/// sovrappongono in coda e su una stessa finestra hanno dato 843%.
/// </para>
/// <para>
/// Non c'e' nemmeno la distinzione fra disco a stato solido e meccanico, ed e' una scelta:
/// e' stato misurato che <b>mente</b>. In macchina virtuale quattro dischi si dichiarano
/// meccanici mentre il supporto fisico e' NVMe, e dietro un adattatore USB la domanda non
/// passa affatto. Una colonna che dice "meccanico" su un SSD e' peggio di una colonna che non
/// c'e'.
/// </para>
/// </remarks>
public sealed class DiskCollector : IMetricCollector
{
    /// <summary>Capienza del volume.</summary>
    public const string TotalBytesMetricId = "disk.total.bytes";

    /// <summary>Spazio ancora scrivibile.</summary>
    public const string FreeBytesMetricId = "disk.free.bytes";

    /// <summary>Spazio occupato.</summary>
    public const string UsedBytesMetricId = "disk.used.bytes";

    /// <summary>Quanto e' pieno il volume, in percentuale.</summary>
    public const string UsedPercentMetricId = "disk.used.percent";

    private static readonly IReadOnlyList<MetricDescriptor> DescriptorList =
    [
        new(TotalBytesMetricId, "Volume size", MetricUnit.Bytes, IsPerInstance: true),
        new(FreeBytesMetricId, "Free space", MetricUnit.Bytes, IsPerInstance: true),
        new(UsedBytesMetricId, "Used space", MetricUnit.Bytes, IsPerInstance: true),
        new(UsedPercentMetricId, "Disk usage", MetricUnit.Percent, IsPerInstance: true),
    ];

    private readonly IDiskReadingProvider provider;

    /// <summary>Crea il collector sopra la porta indicata.</summary>
    /// <param name="provider">Da dove si leggono i volumi.</param>
    public DiskCollector(IDiskReadingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "disk";

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Descriptors => DescriptorList;

    /// <inheritdoc />
    public ValueTask<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Collect());
    }

    private MetricSnapshot Collect()
    {
        if (!provider.IsSupported)
        {
            return new MetricSnapshot(
                Id,
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform",
                []);
        }

        if (!provider.TryRead(out IReadOnlyList<DiskReading> readings))
        {
            return new MetricSnapshot(
                Id,
                CollectorStatus.Unavailable,
                "the list of mounted volumes could not be read",
                []);
        }

        // Nessun volume NON e' un guasto, ed e' importante non chiamarlo tale: dentro un
        // container minimale puo' non esserci un solo filesystem che valga la pena mostrare.
        // "Ok con zero punti" e "non sono riuscito a leggere" devono restare distinguibili.
        if (readings.Count == 0)
        {
            return new MetricSnapshot(
                Id,
                CollectorStatus.Ok,
                "no mounted volume worth reporting on this machine",
                []);
        }

        List<MetricPoint> points = new(readings.Count * 4);

        foreach (DiskReading reading in readings)
        {
            points.Add(MetricPoint.Measured(
                TotalBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Total.Bytes)));

            points.Add(MetricPoint.Measured(
                FreeBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Free.Bytes)));

            points.Add(MetricPoint.Measured(
                UsedBytesMetricId, reading.Instance, MetricValue.FromNumber(reading.Used.Bytes)));

            // La percentuale e' l'unica che puo' MANCARE su un volume che esiste: la capienza
            // arriva a zero dai montaggi speciali e dai dispositivi che si smontano mentre li
            // si legge. Si dichiara non disponibile su quel volume soltanto, con il motivo,
            // invece di pubblicare uno zero che si leggerebbe come "vuoto".
            points.Add(reading.Fraction is { } quanto
                ? MetricPoint.Measured(
                    UsedPercentMetricId,
                    reading.Instance,
                    MetricValue.FromNumber(quanto * 100d))
                : MetricPoint.Unavailable(
                    UsedPercentMetricId,
                    reading.Instance,
                    "the volume reports a size of zero, so how full it is cannot be computed"));
        }

        return new MetricSnapshot(Id, CollectorStatus.Ok, null, points);
    }
}