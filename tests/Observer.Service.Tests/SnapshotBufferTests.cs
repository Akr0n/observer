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
    private static MachineSnapshot Snapshot(int secondo) =>
        new(
            MachineSnapshot.CurrentSchemaVersion,
            new DateTimeOffset(2026, 8, 26, 12, 0, secondo, TimeSpan.Zero),
            []);

    [Fact]
    public void Accoda_ERestituisceInOrdineDiArrivo()
    {
        SnapshotBuffer coda = new(capacity: 8);

        coda.Enqueue(Snapshot(1));
        coda.Enqueue(Snapshot(2));

        IReadOnlyList<MachineSnapshot> svuotati = coda.DrainAll();

        Assert.Equal(2, svuotati.Count);
        Assert.Equal(Snapshot(1).CapturedAt, svuotati[0].CapturedAt);
        Assert.Equal(Snapshot(2).CapturedAt, svuotati[1].CapturedAt);
        Assert.Equal(0L, coda.DroppedCount);
    }

    [Fact]
    public void Accoda_QuandoEPienaScartaIPiuVecchiNonIPiuNuovi()
    {
        SnapshotBuffer coda = new(capacity: 2);

        coda.Enqueue(Snapshot(1));
        coda.Enqueue(Snapshot(2));
        coda.Enqueue(Snapshot(3));

        IReadOnlyList<MachineSnapshot> svuotati = coda.DrainAll();

        // In un monitor di macchina il campione appena letto vale piu' di quello di prima:
        // scartare il piu' nuovo lascerebbe la dashboard indietro proprio quando la
        // macchina e' sotto carico, cioe' l'unico momento in cui qualcuno la guarda.
        Assert.Equal(2, svuotati.Count);
        Assert.Equal(Snapshot(2).CapturedAt, svuotati[0].CapturedAt);
        Assert.Equal(Snapshot(3).CapturedAt, svuotati[1].CapturedAt);
        Assert.Equal(1L, coda.DroppedCount);
    }

    [Fact]
    public void Accoda_ContaGliScartiPerRenderliVisibili()
    {
        SnapshotBuffer coda = new(capacity: 4);

        for (int i = 0; i < 1000; i++)
        {
            // Nessuna di queste chiamate deve bloccare: se una lo facesse, il test non
            // finirebbe mai invece di fallire. E' il modo piu' diretto di dimostrarlo.
            coda.Enqueue(Snapshot(i % 60));
        }

        Assert.Equal(996L, coda.DroppedCount);
        Assert.Equal(4, coda.DrainAll().Count);
    }

    [Fact]
    public void Svuota_SuUnaCodaVuotaNonRestituisceNulla()
    {
        SnapshotBuffer coda = new(capacity: 4);

        Assert.Empty(coda.DrainAll());
    }

    [Fact]
    public void Svuota_LasciaLaCodaVuota()
    {
        SnapshotBuffer coda = new(capacity: 4);
        coda.Enqueue(Snapshot(1));

        coda.DrainAll();

        Assert.Empty(coda.DrainAll());
    }

    [Fact]
    public void Costruttore_RifiutaUnaCapacitaNonPositiva()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotBuffer(capacity: 0));
    }

    [Fact]
    public void Accoda_RifiutaUnoSnapshotNullo()
    {
        SnapshotBuffer coda = new(capacity: 4);

        Assert.Throws<ArgumentNullException>(() => coda.Enqueue(null!));
    }
}
