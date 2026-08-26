namespace Observer.Core.Units;

/// <summary>
/// Una percentuale in punti, garantita finita e non negativa. Esiste per separare in modo
/// visibile il rapporto 0..1 dai punti percentuali 0..100, che e' la confusione piu'
/// frequente, e per impedire che un NaN arrivi al serializzatore JSON.
/// </summary>
/// <remarks>
/// Il limite INFERIORE e' imposto, quello superiore no, ed e' una scelta deliberata: una
/// percentuale d'uso negativa non significa nulla e a grafico passa per rumore, mentre un
/// valore sopra il 100 e' legittimo — un collector per-processo su una macchina multi-core
/// deve poter dire 350%.
/// </remarks>
public readonly record struct Percent
{
    private Percent(double points) => Points = points;

    /// <summary>Valore in punti percentuali, da 0 a 100.</summary>
    public double Points { get; }

    /// <summary>
    /// Converte un rapporto 0..1 in punti percentuali. Restituisce false se il valore non
    /// e' finito — un NaN serializzato farebbe lanciare Utf8JsonWriter, azzerando l'intera
    /// risposta HTTP per colpa di una sola metrica — oppure se e' negativo.
    /// </summary>
    public static bool TryFromRatio(double ratio, out Percent result)
    {
        if (!double.IsFinite(ratio) || ratio < 0.0)
        {
            result = default;
            return false;
        }

        result = new Percent(ratio * 100.0);
        return true;
    }
}
