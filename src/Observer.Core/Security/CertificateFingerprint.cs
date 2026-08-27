using System.Globalization;
using System.Security.Cryptography;

namespace Observer.Core.Security;

/// <summary>
/// L'impronta di un certificato: come si calcola, come si scrive, come si confronta.
/// </summary>
/// <remarks>
/// Sta in Observer.Core e non da una parte sola perche' servono ENTRAMBI i lati: il servizio la
/// stampa, il client la confronta con quella che si e' portato dietro. Client e servizio non
/// possono referenziarsi, quindi una copia per parte sarebbero due copie della regola che
/// decide di chi fidarsi - e il giorno che divergono, il sintomo e' un rifiuto che nessuno
/// riesce a spiegare.
/// <para>
/// Il certificato di Observer e' AUTOFIRMATO: nessuna autorita' lo garantisce, e la catena non
/// dice niente. L'unica cosa che lega un collegamento a una macchina precisa e' questa
/// impronta, presa a mano dalla macchina stessa con <c>observer share</c>.
/// </para>
/// </remarks>
public static class CertificateFingerprint
{
    /// <summary>Il prefisso che dichiara l'algoritmo. Sempre presente in uscita.</summary>
    public const string Prefisso = "sha256:";

    /// <summary>Quanti caratteri esadecimali ha un SHA-256.</summary>
    public const int CifreEsadecimali = 64;

    /// <summary>Calcola l'impronta della codifica DER di un certificato.</summary>
    /// <param name="certificatoDer">Il certificato codificato in DER.</param>
    /// <returns>L'impronta in forma canonica, con il prefisso.</returns>
    public static string Da(ReadOnlySpan<byte> certificatoDer) =>
        Prefisso + Convert.ToHexString(SHA256.HashData(certificatoDer));

    /// <summary>
    /// Riduce alla forma canonica cio' che un umano ha copiato a mano.
    /// </summary>
    /// <param name="testo">L'impronta scritta in un file di configurazione.</param>
    /// <returns>Le 64 cifre in maiuscolo, oppure null se non e' un'impronta SHA-256.</returns>
    /// <remarks>
    /// Tollerante in ingresso e rigida in uscita, e non e' indulgenza: questo valore lo copia
    /// una persona da un terminale a un file di testo, e gli strumenti che lo stampano non
    /// concordano su come separarlo. Due punti, spazi e trattini si accettano; tutto il resto
    /// no, perche' un'impronta "quasi giusta" deve essere rifiutata e non aggiustata.
    /// </remarks>
    public static string? Normalizza(string? testo)
    {
        if (string.IsNullOrWhiteSpace(testo))
        {
            return null;
        }

        ReadOnlySpan<char> resto = testo.AsSpan().Trim();

        if (resto.StartsWith(Prefisso, StringComparison.OrdinalIgnoreCase))
        {
            resto = resto[Prefisso.Length..];
        }

        Span<char> cifre = stackalloc char[CifreEsadecimali];
        int quante = 0;

        foreach (char carattere in resto)
        {
            if (carattere is ':' or ' ' or '-')
            {
                continue;
            }

            if (quante == CifreEsadecimali || !Uri.IsHexDigit(carattere))
            {
                return null;
            }

            cifre[quante++] = char.ToUpperInvariant(carattere);
        }

        return quante == CifreEsadecimali ? new string(cifre) : null;
    }

    /// <summary>Dice se due impronte designano lo stesso certificato.</summary>
    /// <param name="attesa">L'impronta fissata nella configurazione.</param>
    /// <param name="presentata">L'impronta del certificato arrivato dalla rete.</param>
    /// <returns>True solo se sono entrambe valide e uguali.</returns>
    /// <remarks>
    /// Un'impronta illeggibile non e' mai uguale a niente. Trattarla come "salta il controllo"
    /// trasformerebbe un errore di battitura nella disattivazione silenziosa della sola cosa
    /// che protegge il collegamento.
    /// </remarks>
    public static bool Uguali(string? attesa, string? presentata) =>
        Normalizza(attesa) is { } a && Normalizza(presentata) is { } b
        && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Scrive l'impronta a gruppi di due cifre, per chi la deve confrontare a occhio.</summary>
    /// <param name="impronta">L'impronta, in qualsiasi forma accettata.</param>
    /// <returns>La forma leggibile, oppure il testo originale se non e' un'impronta valida.</returns>
    public static string PerLUomo(string impronta)
    {
        if (Normalizza(impronta) is not { } cifre)
        {
            return impronta;
        }

        string[] coppie = new string[CifreEsadecimali / 2];

        for (int i = 0; i < coppie.Length; i++)
        {
            coppie[i] = cifre.Substring(i * 2, 2);
        }

        return string.Join(':', coppie).ToUpperInvariant();
    }

    /// <summary>Il numero di cifre, per i messaggi d'errore.</summary>
    /// <returns>Il conteggio come testo.</returns>
    public static string QuanteCifre() =>
        CifreEsadecimali.ToString(CultureInfo.InvariantCulture);
}