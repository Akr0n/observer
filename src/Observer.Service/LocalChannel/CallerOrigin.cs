namespace Observer.Service.LocalChannel;

/// <summary>Come il servizio ha classificato chi sta chiamando.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="Unidentified"/>, cioe' il caso che NEGA. Cosi' un campo
/// dimenticato, una struct non inizializzata o un ramo aggiunto per distrazione rifiutano
/// invece di concedere.
/// </remarks>
public enum CallerKind
{
    /// <summary>Non si e' potuto stabilire chi sia. Rifiuto.</summary>
    Unidentified = 0,

    /// <summary>Arrivato dalla rete: su Windows anche via SMB, non dalla macchina.</summary>
    FromNetwork,

    /// <summary>Locale, e con un'identita' leggibile.</summary>
    LocalIdentified,
}

/// <summary>L'origine del chiamante, con la diagnosi che l'ha prodotta.</summary>
/// <param name="Kind">La classificazione.</param>
/// <param name="Sid">Il SID su Windows o l'uid su Linux, quando leggibile.</param>
/// <param name="Reason">Perche' e' stata decisa cosi'. In inglese: finisce nei log.</param>
public sealed record CallerOrigin(CallerKind Kind, string? Sid, string Reason);