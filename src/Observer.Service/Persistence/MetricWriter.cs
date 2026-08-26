using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Il pezzo che sta fra la coda e il file: svuota, appiattisce, scrive. Separato dal
/// <see cref="MetricPersistenceService"/> perche' il ciclo di un BackgroundService non si
/// puo' provare senza un host, mentre questo si.
/// </summary>
public sealed class MetricWriter
{
    private readonly SnapshotBuffer buffer;
    private readonly MetricStore store;

    /// <summary>Crea lo scrittore.</summary>
    /// <param name="buffer">La coda da cui prelevare.</param>
    /// <param name="store">Il magazzino su cui scrivere.</param>
    public MetricWriter(SnapshotBuffer buffer, MetricStore store)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(store);

        this.buffer = buffer;
        this.store = store;
    }

    /// <summary>Svuota la coda e scrive tutto in un'unica transazione.</summary>
    /// <returns>Quante righe grezze sono state scritte.</returns>
    public int FlushPending()
    {
        IReadOnlyList<MachineSnapshot> pending = buffer.DrainAll();

        if (pending.Count == 0)
        {
            return 0;
        }

        List<SeriesSample> samples = [];

        foreach (MachineSnapshot snapshot in pending)
        {
            samples.AddRange(SnapshotFlattener.Flatten(snapshot));
        }

        // Una transazione per giro, non una per campione: con una transazione al secondo per
        // ogni metrica il disco diventerebbe il collo di bottiglia del campionamento.
        return store.WriteSamples(samples);
    }
}
