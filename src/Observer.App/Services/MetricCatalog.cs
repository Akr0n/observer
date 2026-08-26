using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Una voce di <c>/metrics/catalog</c>: un collector con i descrittori delle sue metriche.
/// </summary>
/// <param name="CollectorId">Chi produce queste metriche, per esempio "cpu".</param>
/// <param name="Descriptors">Nome leggibile, unita' e per-istanza di ogni metrica.</param>
public sealed record CollectorCatalogEntry(string CollectorId, IReadOnlyList<MetricDescriptor> Descriptors);

/// <summary>
/// Il catalogo, in forma consultabile per identificatore di metrica.
/// </summary>
/// <remarks>
/// E' cio' che permette di scrivere "CPU usage 12.3 %" invece di "cpu.usage.total 12.3":
/// il nome leggibile e l'unita' arrivano dal servizio, non da costanti compilate nel client.
/// Una metrica assente dal catalogo non fa sparire nulla, si mostra con il suo
/// identificatore grezzo.
/// </remarks>
public sealed class MetricCatalog
{
    private readonly Dictionary<string, MetricDescriptor> byMetricId;
    private readonly Dictionary<string, IReadOnlyList<MetricDescriptor>> byCollectorId;

    /// <summary>Costruisce il catalogo dalle voci restituite dal servizio.</summary>
    public MetricCatalog(IEnumerable<CollectorCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        byMetricId = new Dictionary<string, MetricDescriptor>(StringComparer.Ordinal);
        byCollectorId = new Dictionary<string, IReadOnlyList<MetricDescriptor>>(StringComparer.Ordinal);

        foreach (CollectorCatalogEntry entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.CollectorId))
            {
                continue;
            }

            IReadOnlyList<MetricDescriptor> descriptors = entry.Descriptors ?? [];
            byCollectorId[entry.CollectorId] = descriptors;

            foreach (MetricDescriptor descriptor in descriptors)
            {
                if (descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.MetricId))
                {
                    // L'ultimo vince: due collector che dichiarano lo stesso identificatore
                    // sono un errore del servizio, non del client, e non deve far lanciare qui.
                    byMetricId[descriptor.MetricId] = descriptor;
                }
            }
        }
    }

    /// <summary>Catalogo vuoto, usato finche' il servizio non lo ha fornito.</summary>
    public static MetricCatalog Empty { get; } = new([]);

    /// <summary>Il descrittore della metrica, oppure null se il catalogo non la conosce.</summary>
    public MetricDescriptor? Find(string metricId) =>
        metricId is not null && byMetricId.TryGetValue(metricId, out MetricDescriptor? descriptor)
            ? descriptor
            : null;

    /// <summary>True se il catalogo conosce il collector indicato.</summary>
    public bool KnowsCollector(string collectorId) =>
        collectorId is not null && byCollectorId.ContainsKey(collectorId);
}
