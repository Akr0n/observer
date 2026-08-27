using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.App.Services;

/// <summary>
/// Decide se il certificato che arriva dalla rete e' quello della macchina giusta.
/// </summary>
/// <remarks>
/// Il certificato di Observer e' <b>autofirmato</b>: nessuna autorita' lo garantisce, e la
/// validazione ordinaria di TLS lo rifiuterebbe sempre. Al suo posto c'e' un confronto con
/// l'impronta presa a mano dalla macchina stessa, con <c>observer share</c>.
/// <para>
/// Gli errori di catena vengono ignorati <b>di proposito</b>, e non e' una scorciatoia: una
/// catena che non porta a nessuna autorita' e' esattamente cio' che ci si aspetta qui. Cio' che
/// NON viene ignorato e' l'identita', ed e' l'unica cosa che conta: senza questo confronto, chi
/// riesce a mettersi in mezzo presenta il proprio certificato, il collegamento riesce, e il
/// token gli arriva addosso.
/// </para>
/// <para>
/// L'ultima impronta vista viene conservata per poterla <b>mostrare</b>. Dopo una
/// reinstallazione del servizio l'impronta cambia per un motivo legittimo, e senza vedere
/// quella nuova l'utente non ha modo di aggiornare la propria configurazione.
/// </para>
/// </remarks>
public sealed class CertificatePinning
{
    private string? ultimaVista;

    /// <summary>Costruisce il confronto su un'impronta attesa.</summary>
    /// <param name="impronta">L'impronta che quella macchina deve presentare.</param>
    public CertificatePinning(string impronta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(impronta);

        Attesa = impronta;
    }

    /// <summary>L'impronta che ci si aspetta.</summary>
    public string Attesa { get; }

    /// <summary>L'ultima impronta arrivata dalla rete, oppure null se non ne e' arrivata.</summary>
    public string? UltimaVista => Volatile.Read(ref ultimaVista);

    /// <summary>Un handler che accetta solo quella macchina.</summary>
    /// <returns>L'handler, gia' configurato.</returns>
    public SocketsHttpHandler Handler()
    {
        SocketsHttpHandler handler = new();

        handler.SslOptions.RemoteCertificateValidationCallback = (_, presentato, _, _) =>
        {
            if (presentato is not X509Certificate2 certificato)
            {
                Volatile.Write(ref ultimaVista, null);

                return false;
            }

            string vista = CertificateFingerprint.Da(certificato.RawDataMemory.Span);

            Volatile.Write(ref ultimaVista, vista);

            return CertificateFingerprint.Uguali(Attesa, vista);
        };

        return handler;
    }

    /// <summary>La frase da mostrare quando il certificato non e' quello atteso.</summary>
    /// <param name="descrizione">Come si chiama la macchina interrogata.</param>
    /// <returns>Il testo per la barra di stato.</returns>
    /// <remarks>
    /// Dice tutte e due le impronte. Un messaggio che si limita a "non corrisponde" lascia
    /// l'utente senza il valore nuovo, cioe' senza il modo di distinguere una reinstallazione
    /// da un attacco e senza il dato da incollare per rimettere le cose a posto.
    /// </remarks>
    public string Spiegazione(string descrizione)
    {
        string vista = UltimaVista is { } arrivata
            ? CertificateFingerprint.PerLUomo(arrivata)
            : "none - the machine presented no certificate at all";

        return
            $"{descrizione} presented a certificate that is not the one pinned for it, so the " +
            "connection was refused before anything was sent. Nothing was disclosed: the token " +
            "never left this machine." + Environment.NewLine +
            "Expected: " + CertificateFingerprint.PerLUomo(Attesa) + Environment.NewLine +
            "Received: " + vista + Environment.NewLine +
            "If Observer was reinstalled on that machine this is expected, and the fix is to run " +
            "\"observer share\" there and copy the new fingerprint into this machine's " +
            "machines.json. If it was not reinstalled, do NOT copy the new value: this is what a " +
            "machine standing in the middle of the connection looks like.";
    }
}
