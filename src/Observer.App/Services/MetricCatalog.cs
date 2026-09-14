using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// One entry of <c>/metrics/catalog</c>: a collector with the descriptors of its metrics.
/// </summary>
/// <param name="CollectorId">Who produces these metrics, for example "cpu".</param>
/// <param name="Descriptors">Readable name, unit and per-instance flag of each metric.</param>
public sealed record CollectorCatalogEntry(string CollectorId, IReadOnlyList<MetricDescriptor> Descriptors);

/// <summary>
/// The catalog, in a form that can be looked up by metric identifier.
/// </summary>
/// <remarks>
/// It is what makes it possible to write "CPU usage 12.3 %" instead of "cpu.usage.total 12.3":
/// the readable name and the unit come from the service, not from constants compiled into the
/// client. A metric missing from the catalog hides nothing: it is shown with its raw
/// identifier.
/// </remarks>
public sealed class MetricCatalog
{
    private readonly Dictionary<string, MetricDescriptor> byMetricId;
    private readonly Dictionary<string, IReadOnlyList<MetricDescriptor>> byCollectorId;

    /// <summary>Builds the catalog from the entries the service returned.</summary>
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
                    // The last one wins: two collectors declaring the same identifier are a bug in
                    // the service, not in the client, and this must not throw here.
                    byMetricId[descriptor.MetricId] = descriptor;
                }
            }
        }
    }

    /// <summary>Empty catalog, used until the service has supplied one.</summary>
    public static MetricCatalog Empty { get; } = new([]);

    /// <summary>The metric's descriptor, or null if the catalog does not know it.</summary>
    public MetricDescriptor? Find(string metricId) =>
        metricId is not null && byMetricId.TryGetValue(metricId, out MetricDescriptor? descriptor)
            ? descriptor
            : null;

}