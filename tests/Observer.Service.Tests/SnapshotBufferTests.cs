using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The queue between the sampler and the disk. What it protects is not speed: it is the
/// CORRECTNESS of the next measurement. CPU percentage is computed from the distance between
/// two readings, so a sampler that waits for the disk does not produce a slow chart, it
/// produces wrong numbers.
/// </summary>
public class SnapshotBufferTests
{
    private static MachineSnapshot Snapshot(int second) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            new DateTimeOffset(2026, 8, 26, 12, 0, second, TimeSpan.Zero),
            []);

    [Fact]
    public void Enqueue_DrainsInArrivalOrder()
    {
        SnapshotBuffer buffer = new(capacity: 8);

        buffer.Enqueue(Snapshot(1));
        buffer.Enqueue(Snapshot(2));

        IReadOnlyList<MachineSnapshot> drained = buffer.DrainAll();

        Assert.Equal(2, drained.Count);
        Assert.Equal(Snapshot(1).CapturedAt, drained[0].CapturedAt);
        Assert.Equal(Snapshot(2).CapturedAt, drained[1].CapturedAt);
        Assert.Equal(0L, buffer.DroppedCount);
    }

    [Fact]
    public void Enqueue_WhenFullDropsTheOldestNotTheNewest()
    {
        SnapshotBuffer buffer = new(capacity: 2);

        buffer.Enqueue(Snapshot(1));
        buffer.Enqueue(Snapshot(2));
        buffer.Enqueue(Snapshot(3));

        IReadOnlyList<MachineSnapshot> drained = buffer.DrainAll();

        // In a machine monitor the sample just read is worth more than the one before it:
        // dropping the newest would leave the dashboard behind exactly when the machine is
        // under load, which is the only moment anyone is looking at it.
        Assert.Equal(2, drained.Count);
        Assert.Equal(Snapshot(2).CapturedAt, drained[0].CapturedAt);
        Assert.Equal(Snapshot(3).CapturedAt, drained[1].CapturedAt);
        Assert.Equal(1L, buffer.DroppedCount);
    }

    [Fact]
    public void Enqueue_CountsDroppedSnapshotsSoTheyAreVisible()
    {
        SnapshotBuffer buffer = new(capacity: 4);

        for (int i = 0; i < 1000; i++)
        {
            // None of these calls may block: if one did, the test would never finish
            // instead of failing. This is the most direct way of showing it.
            buffer.Enqueue(Snapshot(i % 60));
        }

        Assert.Equal(996L, buffer.DroppedCount);
        Assert.Equal(4, buffer.DrainAll().Count);
    }

    [Fact]
    public void DrainAll_OnAnEmptyBufferReturnsNothing()
    {
        SnapshotBuffer buffer = new(capacity: 4);

        Assert.Empty(buffer.DrainAll());
    }

    [Fact]
    public void DrainAll_LeavesTheBufferEmpty()
    {
        SnapshotBuffer buffer = new(capacity: 4);
        buffer.Enqueue(Snapshot(1));

        buffer.DrainAll();

        Assert.Empty(buffer.DrainAll());
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotBuffer(capacity: 0));
    }

    [Fact]
    public void Enqueue_RejectsANullSnapshot()
    {
        SnapshotBuffer buffer = new(capacity: 4);

        Assert.Throws<ArgumentNullException>(() => buffer.Enqueue(null!));
    }
}
