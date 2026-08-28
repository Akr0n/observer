using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// Lo spazio di UN volume montato.
/// </summary>
/// <param name="Instance">
/// Come si chiama il volume per chi guarda: <c>C:</c> su Windows, il punto di innesto su
/// Linux. E' anche l'istanza con cui i punti vengono pubblicati, quindi deve restare stabile
/// da un campione all'altro: se cambiasse, la riga a schermo verrebbe ricostruita ogni volta
/// e lo storico si spezzerebbe in due serie diverse.
/// </param>
/// <param name="Total">Capienza del volume.</param>
/// <param name="Free">
/// Spazio disponibile <b>a questo utente</b>. Su un volume con quote non coincide con lo
/// spazio libero del disco, ed e' comunque il numero giusto: dice quanto ci si puo' ancora
/// scrivere, che e' la domanda che si fa chi guarda.
/// </param>
public readonly record struct DiskReading(string Instance, ByteSize Total, ByteSize Free)
{
    /// <summary>Spazio occupato, saturato a zero se "libero" superasse "totale".</summary>
    /// <remarks>
    /// La sottrazione satura per la stessa ragione della memoria: su un volume con quote o
    /// con blocchi riservati i due numeri arrivano da contatori diversi, e una differenza
    /// negativa produrrebbe una percentuale assurda invece di un numero mancante.
    /// </remarks>
    public ByteSize Used => Total.SaturatingSubtract(Free);

    /// <summary>Quanto e' pieno, da 0 a 1, oppure null se la capienza non e' nota.</summary>
    /// <remarks>
    /// Null e non zero quando il totale e' zero: un volume di capienza nulla non e' "vuoto",
    /// e' un volume di cui non si conosce la dimensione — succede sui montaggi speciali e sui
    /// dispositivi che si smontano mentre li si legge. Zero direbbe "c'e' tutto lo spazio del
    /// mondo", che e' esattamente il contrario.
    /// </remarks>
    public double? Fraction => Total.Bytes > 0L ? (double)Used.Bytes / Total.Bytes : null;
}

/// <summary>
/// Porta di lettura dello spazio sui dischi.
/// </summary>
/// <remarks>
/// Restituisce una lista perche' i volumi sono piu' d'uno e cambiano mentre il programma
/// gira: una chiavetta compare, un disco di rete sparisce. Un volume che non si riesce a
/// interrogare non fa fallire gli altri — la lista torna con quelli letti, e chi manca manca.
/// </remarks>
public interface IDiskReadingProvider
{
    /// <summary>Falso quando su questa piattaforma non si misura affatto.</summary>
    bool IsSupported { get; }

    /// <summary>Perche' non si misura, quando non si misura.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Legge i volumi. False quando la lettura fallisce del tutto.</summary>
    bool TryRead(out IReadOnlyList<DiskReading> readings);
}