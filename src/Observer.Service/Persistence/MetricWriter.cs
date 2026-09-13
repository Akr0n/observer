using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// The piece that sits between the queue and the file: drains, flattens, writes. Kept apart
/// from <see cref="MetricPersistenceService"/> because a BackgroundService's loop cannot be
/// tested without a host, while this one can.
/// </summary>
public sealed class MetricWriter
{
    private readonly SnapshotBuffer buffer;
    private readonly MetricStore store;

    /// <summary>Creates the writer.</summary>
    /// <param name="buffer">The queue to take from.</param>
    /// <param name="store">The store to write to.</param>
    public MetricWriter(SnapshotBuffer buffer, MetricStore store)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(store);

        this.buffer = buffer;
        this.store = store;
    }

    /// <summary>Drains the queue and writes everything in a single transaction.</summary>
    /// <returns>How many raw rows were written.</returns>
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

        // One transaction per pass, not one per sample: with a transaction a second for each
        // metric the disk would become the bottleneck of the sampling.
        return store.WriteSamples(samples);
    }
}
