using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.App.Services;

/// <summary>
/// Decide se il certificate che arriva dalla rete e' quello della macchina giusta.
/// </summary>
/// <remarks>
/// Il certificate di Observer e' <b>autofirmato</b>: nessuna autorita' lo garantisce, e la
/// validazione ordinaria di TLS lo rifiuterebbe sempre. Al suo posto c'e' un confronto con
/// l'fingerprint presa a mano dalla macchina stessa, con <c>observer share</c>.
/// <para>
/// Gli errori di catena vengono ignorati <b>di proposito</b>, e non e' una scorciatoia: una
/// catena che non porta a nessuna autorita' e' esattamente cio' che ci si aspetta qui. Cio' che
/// NON viene ignorato e' l'identita', ed e' l'unica cosa che conta: senza questo confronto, chi
/// riesce a mettersi in mezzo presenta il proprio certificate, il collegamento riesce, e il
/// token gli arriva addosso.
/// </para>
/// <para>
/// L'ultima fingerprint seenFingerprint viene conservata per poterla <b>mostrare</b>. Dopo una
/// reinstallazione del servizio l'fingerprint cambia per un motivo legittimo, e senza vedere
/// quella nuova l'utente non ha modo di aggiornare la propria configurazione.
/// </para>
/// </remarks>
public sealed class CertificatePinning
{
    private string? lastSeenFingerprint;
    private int rejected;

    /// <summary>Costruisce il confronto su un'fingerprint attesa.</summary>
    /// <param name="fingerprint">L'fingerprint che quella macchina deve presentare.</param>
    public CertificatePinning(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        ExpectedFingerprint = fingerprint;
    }

    /// <summary>L'fingerprint che ci si aspetta.</summary>
    public string ExpectedFingerprint { get; }

    /// <summary>L'ultima fingerprint received dalla rete, oppure null se non ne e' received.</summary>
    public string? LastSeenFingerprint => Volatile.Read(ref lastSeenFingerprint);

    /// <summary>Vero se l'ultima volta che un certificate e' stato esaminato e' stato respinto.</summary>
    /// <remarks>
    /// Serve a non attribuire al fissaggio guasti che non sono suoi. Una connessione TLS puo'
    /// fallire per molte ragioni - protocolli incompatibili, un intermediario che chiude, un
    /// server che non parla TLS affatto - e tutte arrivano come la stessa eccezione. Senza
    /// questo, un guasto qualunque verrebbe raccontato all'utente come "qualcuno si sta
    /// mettendo in mezzo", che e' un'accusa pesante da fare senza prove.
    /// <para>
    /// Il callback NON viene invocato quando la connessione viene riusata, quindi la sola
    /// <see cref="LastSeenFingerprint"/> potrebbe essere vecchia: e' questo indicatore, azzerato a ogni
    /// esame riuscito, a dire se il rifiuto e' di adesso.
    /// </para>
    /// </remarks>
    public bool HasRejected => Volatile.Read(ref rejected) != 0;

    /// <summary>Un handler che accetta solo quella macchina.</summary>
    /// <returns>L'handler, gia' configurato.</returns>
    public SocketsHttpHandler Handler()
    {
        // Questo e' il percorso di RETE, quindi e' qui che i byte costano: senza questa riga il
        // servizio non comprimerebbe niente, perche' la compressione si negozia per richiesta e
        // un client che non manda Accept-Encoding riceve il chiaro. Le due meta' stanno insieme
        // o non stanno. Il canale locale NON la mette, di proposito: li' i byte non attraversano
        // niente, e comprimerli sarebbe CPU spesa dalla macchina che questo programma misura.
        SocketsHttpHandler handler = new() { AutomaticDecompression = DecompressionMethods.All };

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presentedCertificate, _, _) =>
        {
            if (presentedCertificate is not X509Certificate2 certificate)
            {
                Volatile.Write(ref lastSeenFingerprint, null);
                Volatile.Write(ref rejected, 1);

                return false;
            }

            string seenFingerprint = CertificateFingerprint.From(certificate.RawDataMemory.Span);
            bool matches = CertificateFingerprint.Match(ExpectedFingerprint, seenFingerprint);

            Volatile.Write(ref lastSeenFingerprint, seenFingerprint);
            Volatile.Write(ref rejected, matches ? 0 : 1);

            return matches;
        };

        return handler;
    }

    /// <summary>La frase da mostrare quando il certificate non e' quello atteso.</summary>
    /// <param name="description">Come si chiama la macchina interrogata.</param>
    /// <returns>Il testo per la barra di stato.</returns>
    /// <remarks>
    /// Dice tutte e due le impronte. Un messaggio che si limita a "non matches" lascia
    /// l'utente senza il valore nuovo, cioe' senza il modo di distinguere una reinstallazione
    /// da un attacco e senza il dato da incollare per rimettere le cose a posto.
    /// </remarks>
    public string DescribeMismatch(string description)
    {
        string seenFingerprint = LastSeenFingerprint is { } received
            ? CertificateFingerprint.ForHumans(received)
            : "none - the machine presented no certificate at all";

        return
            $"{description} presented a certificate that is not the one pinned for it, so the " +
            "connection was refused before anything was sent. Nothing was disclosed: the token " +
            "never left this machine." + Environment.NewLine +
            "Expected: " + CertificateFingerprint.ForHumans(ExpectedFingerprint) + Environment.NewLine +
            "Received: " + seenFingerprint + Environment.NewLine +
            "If Observer was reinstalled on that machine this is expected, and the fix is to run " +
            "\"observer share\" there and copy the new fingerprint into this machine's " +
            "machines.json. If it was not reinstalled, do NOT copy the new value: this is what a " +
            "machine standing in the middle of the connection looks like.";
    }
}
