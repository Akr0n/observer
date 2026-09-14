using Observer.Core.Metrics;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// La coda fra il campionatore e il disco. Il requisito che difende non e' la velocita': e'
/// la CORRETTEZZA della misura successiva. La percentuale di CPU si calcola sulla distanza
/// fra due letture, quindi un campionatore che aspetta il disco non produce un grafico
/// lento, produce numeri sbagliati.
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

        // In un monitor di macchina il campione appena letto vale piu' di quello di prima:
        // scartare il piu' nuovo lascerebbe la dashboard indietro proprio quando la
        // macchina e' sotto carico, cioe' l'unico momento in cui qualcuno la guarda.
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
            // Nessuna di queste chiamate deve bloccare: se una lo facesse, il test non
            // finirebbe mai invece di fallire. E' il modo piu' diretto di dimostrarlo.
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
