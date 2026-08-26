namespace Observer.Core.Metrics;

/// <summary>
/// Perche' un campione non ha prodotto un valore. Esiste per non perdere il MOTIVO:
/// restituire semplicemente "nessun valore" costringerebbe chi guarda la dashboard a
/// indovinare se il dato manca, e' rotto o non e' ancora pronto.
/// </summary>
public enum SampleFailure
{
    /// <summary>
    /// Nessuna diagnosi. E' il valore di default(SampleFailure) e non deve mai spacciarsi
    /// per una causa reale: uno zero che significasse "contatori tornati indietro" farebbe
    /// apparire in dashboard una diagnosi mai effettuata.
    /// </summary>
    Unknown = 0,

    /// <summary>Prima lettura: serve un secondo campione per calcolare una differenza.</summary>
    FirstSample = 1,

    /// <summary>I contatori sono diminuiti (sospensione, ripristino, migrazione di VM).</summary>
    CounterWentBackwards = 2,

    /// <summary>Fra i due campioni non e' trascorso tempo misurabile.</summary>
    NoElapsedTime = 3,

    /// <summary>Il calcolo ha prodotto un valore non finito (NaN o infinito).</summary>
    NotFinite = 4,
}

/// <summary>
/// Traduce un <see cref="SampleFailure"/> in una frase leggibile da mostrare al posto del
/// valore mancante.
/// </summary>
public static class SampleFailureText
{
    /// <summary>Spiegazione in italiano del motivo per cui il campione non ha un valore.</summary>
    public static string Describe(SampleFailure failure) => failure switch
    {
        SampleFailure.FirstSample =>
            "first reading: waiting for a second sample to measure the change",
        SampleFailure.CounterWentBackwards =>
            "counters went backwards (machine suspended, resumed or migrated)",
        SampleFailure.NoElapsedTime =>
            "no measurable time passed between the two samples",
        SampleFailure.NotFinite =>
            "the computed value wasn't finite and was discarded",
        _ => "cause unknown",
    };
}
