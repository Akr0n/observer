namespace Observer.Core.Metrics.Cpu;

/// <summary>
/// Porta verso i contatori di tempo CPU della piattaforma. Esiste per tenere la lettura
/// grezza fuori dal collector: e' cio' che rende il calcolo testabile senza hardware.
/// </summary>
public interface ICpuTimesProvider
{
    /// <summary>False quando la piattaforma non espone affatto questi contatori.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Perche' non e' supportata, quando <see cref="IsSupported"/> e' false. E' la frase
    /// che finisce in dashboard al posto del valore.
    /// </summary>
    string? UnsupportedReason { get; }

    /// <summary>Legge i contatori cumulativi. False se la lettura non riesce ora.</summary>
    bool TryRead(out CpuTimes times);
}

/// <summary>
/// Collector dell'utilizzo CPU. Non apre file e non chiama l'OS: chiede i tick alla porta e
/// applica <see cref="CpuUsage"/>. Conserva il campione precedente perche' una percentuale
/// e' per definizione una differenza fra due letture.
/// </summary>
public sealed class CpuCollector : IMetricCollector
{
    /// <summary>Identificatore della metrica di utilizzo CPU totale.</summary>
    public const string TotalUsageMetricId = "cpu.usage.total";

    private static readonly MetricDescriptor[] DescriptorList =
    [
        new(TotalUsageMetricId, "CPU usage", MetricUnit.Percent, IsPerInstance: false),
    ];

    private readonly ICpuTimesProvider provider;
    private CpuTimes? previous;

    /// <summary>Crea il collector sopra la porta indicata.</summary>
    public CpuCollector(ICpuTimesProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public string Id => "cpu";

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
            // Resta nel catalogo con la spiegazione: "non si puo' misurare qui" e'
            // un'informazione, "la metrica e' sparita" e' un bug apparente.
            return Degraded(
                CollectorStatus.Unsupported,
                provider.UnsupportedReason ?? "source not supported on this platform");
        }

        if (!provider.TryRead(out CpuTimes current))
        {
            // Azzera la storia: calcolare un delta a cavallo di un buco produrrebbe una
            // percentuale mediata su un intervallo sconosciuto, cioe' un numero inventato.
            previous = null;
            return Degraded(CollectorStatus.Unavailable, "couldn't read the CPU counters");
        }

        if (previous is not CpuTimes last)
        {
            previous = current;
            return Degraded(
                CollectorStatus.Warmup,
                SampleFailureText.Describe(SampleFailure.FirstSample));
        }

        previous = current;

        if (!CpuUsage.TryComputePercent(last, current, out Units.Percent usage, out SampleFailure failure))
        {
            // Vuoto e spiegato, mai un numero sbagliato.
            return Degraded(CollectorStatus.Unavailable, SampleFailureText.Describe(failure));
        }

        return new MetricSnapshot(
            Id,
            CollectorStatus.Ok,
            Message: null,
            [MetricPoint.Measured(TotalUsageMetricId, instance: null, MetricValue.FromNumber(usage.Points))]);
    }

    private MetricSnapshot Degraded(CollectorStatus status, string message) =>
        new(Id, status, message, []);
}
