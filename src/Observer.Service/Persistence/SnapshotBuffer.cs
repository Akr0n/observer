using System.Threading.Channels;
using Observer.Core.Metrics;

namespace Observer.Service.Persistence;

/// <summary>
/// Dove il campionatore deposita uno snapshot perche' qualcun altro lo scriva su disco.
/// </summary>
/// <remarks>
/// Esiste solo per una ragione: il campionatore gira a 1 Hz e il calcolo della percentuale
/// di CPU dipende dalla DISTANZA fra due letture. Se il campionatore aspettasse il disco,
/// un fsync lento non rallenterebbe la scrittura, falserebbe la misura successiva.
/// </remarks>
public interface IMetricSnapshotSink
{
    /// <summary>Deposita uno snapshot. Non deve MAI bloccare ne' lanciare.</summary>
    /// <param name="snapshot">Lo snapshot appena campionato.</param>
    void Enqueue(MachineSnapshot snapshot);
}

/// <summary>
/// Il deposito che butta via tutto. Si usa quando la persistenza e' spenta: cosi' il
/// campionatore non deve sapere se lo storico esiste, e non c'e' un ramo "se non c'e'
/// nessuno in ascolto" da sbagliare.
/// </summary>
public sealed class NullMetricSnapshotSink : IMetricSnapshotSink
{
    /// <inheritdoc />
    public void Enqueue(MachineSnapshot snapshot)
    {
        // Di proposito: la persistenza e' disattivata.
    }
}

/// <summary>
/// La coda in memoria fra il campionatore e lo scrittore su disco. Quando e' piena scarta i
/// piu' vecchi: in un monitor di macchina il dato appena letto vale piu' di quello di trenta
/// secondi fa, e l'alternativa — far aspettare il campionatore — e' peggio del buco.
/// </summary>
public sealed class SnapshotBuffer : IMetricSnapshotSink
{
    private readonly Channel<MachineSnapshot> channel;

    private long dropped;

    /// <summary>Crea la coda.</summary>
    /// <param name="capacity">Quanti snapshot possono aspettare prima di scartare.</param>
    /// <exception cref="ArgumentOutOfRangeException">Se la capacita' non e' positiva.</exception>
    public SnapshotBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        channel = Channel.CreateBounded<MachineSnapshot>(
            new BoundedChannelOptions(capacity)
            {
                // DropOldest e' cio' che rende Enqueue non bloccante SENZA perdere il dato
                // piu' fresco. Wait bloccherebbe il campionatore; DropWrite butterebbe via
                // proprio il campione appena letto, cioe' l'unico che qualcuno sta
                // guardando mentre la macchina e' sotto carico.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref dropped));
    }

    /// <summary>Quanti snapshot sono stati scartati perche' la coda era piena.</summary>
    /// <remarks>
    /// Esposto in /metrics/storage di proposito: uno storico con buchi deve essere
    /// misurabile, altrimenti sembra semplicemente uno storico.
    /// </remarks>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <inheritdoc />
    public void Enqueue(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // TryWrite su un canale limitato con DropOldest non aspetta e non lancia mai:
        // restituisce false solo a canale chiuso, cioe' durante l'arresto.
        channel.Writer.TryWrite(snapshot);
    }

    /// <summary>Preleva tutto cio' che c'e' adesso, senza aspettare.</summary>
    /// <returns>Gli snapshot in coda, dal piu' vecchio al piu' recente.</returns>
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
