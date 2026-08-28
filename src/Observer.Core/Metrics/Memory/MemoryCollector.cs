using Observer.Core.Units;

namespace Observer.Core.Metrics.Memory;

/// <summary>
/// Porta verso i valori di memoria della piattaforma. A differenza della CPU non serve un
/// delta: sono valori istantanei, basta una lettura.
/// </summary>
public interface IMemoryReadingProvider
{
    /// <summary>False quando la piattaforma non espone questi valori.</summary>
    bool IsSupported { get; }

    /// <summary>Perche' non e' supportata, quando <see cref="IsSupported"/> e' false.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Legge i valori di memoria. False se la lettura non riesce ora.</summary>
    bool TryRead(out MemoryReading value);
}

/// <summary>
/// Collector della memoria. Pubblica l'uso calcolato su "disponibile" e non su "libera":
/// su Linux la differenza fra le due e' quella fra dire 50% e dire 99% sulla stessa
/// macchina rilassata.
/// </summary>
public sealed class MemoryCollector : IMetricCollector
{
    /// <summary>Memoria fisica totale, in byte.</summary>
    public const string TotalBytesMetricId = "memory.total.bytes";

    /// <summary>Memoria disponibile per nuove allocazioni, in byte.</summary>
    public const string AvailableBytesMetricId = "memory.available.bytes";

    /// <summary>Memoria in uso, in byte.</summary>
    public const string UsedBytesMetricId = "memory.used.bytes";

    /// <summary>Memoria in uso, in punti percentuali.</summary>
    public const string UsedPercentMetricId = "memory.used.percent";

    /// <summary>True quando "disponibile" e' una stima e non una misura.</summary>
    public const string AvailableEstimatedMetricId = "memory.available.estimated";

    /// <summary>Swap totale, in byte. Assente su macchine senza swap.</summary>
    public const string SwapTotalMetricId = "memory.swap.total.bytes";

    /// <summary>Swap in uso, in byte. Assente su macchine senza swap.</summary>
    public const string SwapUsedMetricId = "memory.swap.used.bytes";

    private static readonly MetricDescriptor[] DescriptorList =
    [
        new(TotalBytesMetricId, "Total memory", MetricUnit.Bytes, IsPerInstance: false),
        new(AvailableBytesMetricId, "Available memory", MetricUnit.Bytes, IsPerInstance: false),
        new(UsedBytesMetricId, "Used memory", MetricUnit.Bytes, IsPerInstance: false),
        // "Memory usage" e non un secondo "Used memory": due righe con lo STESSO nome
        // costringevano la proiezione a distinguerle con il simbolo dell'unita', e quel
        // simbolo non e' quello che si legge nel valore - la riga diceva "Used memory (B)"
        // mentre a destra c'era "11.2 GiB", perche' le dimensioni si mostrano scalate.
        // Il nome fa anche il paio con "CPU usage", che e' la stessa cosa per l'altro riquadro.
        new(UsedPercentMetricId, "Memory usage", MetricUnit.Percent, IsPerInstance: false),
        // Per esteso: "Available is estimated" non dice a chi legge di COSA si sta parlando.
        // Vale "Yes" quando il sistema non riporta la memoria disponibile e va dedotta.
        new(AvailableEstimatedMetricId, "Available memory is an estimate", MetricUnit.None, IsPerInstance: false),
        new(SwapTotalMetricId, "Total swap", MetricUnit.Bytes, IsPerInstance: false),
        new(SwapUsedMetricId, "Used swap", MetricUnit.Bytes, IsPerInstance: false),
    ];

    private readonly IMemoryReadingProvider provider;

    /// <summary>Crea il collector sopra la porta indicata.</summary>
    public MemoryCollector(IMemoryReadingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "memory";

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
            return Degraded(
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform");
        }

        if (!provider.TryRead(out MemoryReading reading))
        {
            return Degraded(CollectorStatus.Unavailable, "couldn't read the memory values");
        }

        List<MetricPoint> points =
        [
            MetricPoint.Measured(TotalBytesMetricId, null, MetricValue.FromNumber(reading.Total.Bytes)),
            MetricPoint.Measured(AvailableBytesMetricId, null, MetricValue.FromNumber(reading.Available.Bytes)),
            MetricPoint.Measured(UsedBytesMetricId, null, MetricValue.FromNumber(reading.Used.Bytes)),
        ];

        // Il totale a zero renderebbe la percentuale una divisione per zero: si omette il
        // punto invece di pubblicare un NaN, che oltretutto non e' JSON valido.
        if (reading.Total.Bytes > 0L
            && Percent.TryFromRatio((double)reading.Used.Bytes / reading.Total.Bytes, out Percent used))
        {
            points.Add(MetricPoint.Measured(UsedPercentMetricId, null, MetricValue.FromNumber(used.Points)));
        }

        // Una macchina senza swap e' una configurazione legittima, non un guasto: l'assenza
        // dei punti dice "non applicabile", mentre uno zero direbbe "c'e' ed e' vuoto".
        if (reading.SwapTotal.Bytes > 0L)
        {
            points.Add(MetricPoint.Measured(
                SwapTotalMetricId,
                null,
                MetricValue.FromNumber(reading.SwapTotal.Bytes)));
            points.Add(MetricPoint.Measured(
                SwapUsedMetricId,
                null,
                MetricValue.FromNumber(reading.SwapTotal.SaturatingSubtract(reading.SwapFree).Bytes)));
        }

        // In FONDO, e non fra le quantita': e' l'unica riga che non e' una misura ma una nota
        // su un'altra riga, e in mezzo alle altre spezzava la lettura.
        points.Add(MetricPoint.Measured(
            AvailableEstimatedMetricId,
            null,
            MetricValue.FromFlag(reading.AvailableWasEstimated)));

        return new MetricSnapshot(Id, CollectorStatus.Ok, Message: null, points);
    }

    private MetricSnapshot Degraded(CollectorStatus status, string message) =>
        new(Id, status, message, []);
}
