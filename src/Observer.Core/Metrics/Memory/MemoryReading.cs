using Observer.Core.Units;

namespace Observer.Core.Metrics.Memory;

/// <summary>
/// Una lettura della memoria, indipendente dalla piattaforma che l'ha prodotta.
/// </summary>
/// <param name="Total">Memoria fisica totale.</param>
/// <param name="Available">
/// Memoria realmente disponibile per nuove allocazioni. Non e' la memoria "libera": su
/// Linux la cache riutilizzabile conta come disponibile, ed e' la differenza fra dire
/// "50% usata" e "99% usata" sulla stessa identica macchina.
/// </param>
/// <param name="SwapTotal">Spazio di swap totale. Zero e' una configurazione legittima.</param>
/// <param name="SwapFree">Spazio di swap libero.</param>
/// <param name="AvailableWasEstimated">
/// True quando <paramref name="Available"/> e' una stima e non una misura, perche' la
/// piattaforma non la espone direttamente. Va portato fino alla UI: presentare una stima
/// come misura e' una bugia silenziosa.
/// </param>
public readonly record struct MemoryReading(
    ByteSize Total,
    ByteSize Available,
    ByteSize SwapTotal,
    ByteSize SwapFree,
    bool AvailableWasEstimated)
{
    /// <summary>Memoria in uso, saturata a zero se "disponibile" superasse "totale".</summary>
    public ByteSize Used => Total.SaturatingSubtract(Available);
}
