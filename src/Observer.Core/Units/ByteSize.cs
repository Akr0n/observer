namespace Observer.Core.Units;

/// <summary>
/// Una quantita' di byte. Esiste per rendere impossibile confondere le unita' di partenza:
/// /proc/meminfo scrive "kB" ma intende KiB (1024 byte), e sbagliare quel fattore produce
/// numeri credibili e sbagliati invece di un errore.
/// </summary>
public readonly record struct ByteSize
{
    private ByteSize(long bytes) => Bytes = bytes;

    /// <summary>Quantita' in byte.</summary>
    public long Bytes { get; }

    /// <summary>Costruisce da un valore gia' espresso in byte.</summary>
    public static ByteSize FromBytes(long bytes) => new(bytes);

    /// <summary>
    /// Costruisce da kibibyte (1024 byte). E' la fabbrica da usare per /proc/meminfo,
    /// che etichetta i suoi valori "kB" pur essendo KiB.
    /// </summary>
    public static ByteSize FromKibibytes(long kibibytes) => new(kibibytes * 1024L);

    /// <summary>
    /// Sottrazione che si ferma a zero invece di diventare negativa. Serve perche' su
    /// alcune macchine virtuali "available" supera momentaneamente "total": senza
    /// saturazione l'usato diventerebbe negativo e il grafico impazzirebbe in silenzio.
    /// </summary>
    public ByteSize SaturatingSubtract(ByteSize other) =>
        new(Bytes > other.Bytes ? Bytes - other.Bytes : 0L);
}
