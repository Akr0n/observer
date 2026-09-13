using System.Threading.Channels;
using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Where the sampler drops a snapshot for somebody else to write to disk.
/// </summary>
/// <remarks>
/// It exists for one reason only: the sampler runs at 1 Hz and the CPU percentage is
/// computed from the DISTANCE between two readings. If the sampler waited for the disk, a
/// slow fsync would not slow the write down, it would falsify the next measurement.
/// </remarks>
public interface IMetricSnapshotSink
{
    /// <summary>Drops a snapshot. Must NEVER block or throw.</summary>
    /// <param name="snapshot">The snapshot just sampled.</param>
    void Enqueue(MachineSnapshot snapshot);
}

/// <summary>
/// The sink that throws everything away. It is used when persistence is off: this way the
/// sampler does not have to know whether the history exists, and there is no "if nobody is
/// listening" branch to get wrong.
/// </summary>
public sealed class NullMetricSnapshotSink : IMetricSnapshotSink
{
    /// <inheritdoc />
    public void Enqueue(MachineSnapshot snapshot)
    {
        // Deliberate: persistence is disabled.
    }
}

/// <summary>
/// The in-memory queue between the sampler and the writer to disk. When it is full it drops
/// the oldest: in a machine monitor the reading just taken is worth more than the one from
/// thirty seconds ago, and the alternative — making the sampler wait — is worse than the gap.
/// </summary>
public sealed class SnapshotBuffer : IMetricSnapshotSink
{
    private readonly Channel<MachineSnapshot> channel;

    private long dropped;

    /// <summary>Creates the queue.</summary>
    /// <param name="capacity">How many snapshots may wait before dropping starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">If the capacity is not positive.</exception>
    public SnapshotBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        channel = Channel.CreateBounded<MachineSnapshot>(
            new BoundedChannelOptions(capacity)
            {
                // DropOldest is what makes Enqueue non-blocking WITHOUT losing the
                // freshest reading. Wait would block the sampler; DropWrite would throw
                // away precisely the sample just taken, that is, the only one anybody
                // is looking at while the machine is under load.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref dropped));
    }

    /// <summary>How many snapshots were dropped because the queue was full.</summary>
    /// <remarks>
    /// Exposed in /metrics/storage deliberately: a history with gaps must be
    /// measurable, otherwise it simply looks like a history.
    /// </remarks>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <inheritdoc />
    public void Enqueue(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // TryWrite on a bounded channel with DropOldest never waits and never throws:
        // it returns false only on a closed channel, that is, during shutdown.
        channel.Writer.TryWrite(snapshot);
    }

    /// <summary>Takes everything that is there now, without waiting.</summary>
    /// <returns>The queued snapshots, from the oldest to the most recent.</returns>
    public IReadOnlyList<MachineSnapshot> DrainAll()
    {
        List<MachineSnapshot> drained = [];

        while (channel.Reader.TryRead(out MachineSnapshot? snapshot))
        {
            drained.Add(snapshot);
        }

        return drained;
    }
}
