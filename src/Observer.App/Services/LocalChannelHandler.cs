using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;

namespace Observer.App.Services;

/// <summary>Apre il canale locale verso il servizio su questa macchina.</summary>
/// <remarks>
/// Non nasce un secondo protocollo: sopra questo trasporto viaggia lo stesso HTTP/1.1 del
/// percorso di rete, e il resto del client non sa nemmeno quale dei due sta usando.
/// </remarks>
public static class LocalChannelHandler
{
    /// <summary>Quanto si aspetta la connessione al canale locale.</summary>
    /// <remarks>
    /// Corto e SEPARATO dal timeout della richiesta, per una ragione misurata: su TCP un
    /// servizio spento fallisce in millisecondi, ma su una pipe assente la connect consuma
    /// l'intero timeout della richiesta. Con i tre secondi della richiesta, la finestra
    /// passerebbe da un aggiornamento al secondo a uno ogni quattro appena il servizio si ferma.
    /// </remarks>
    public static readonly TimeSpan TimeoutDiConnessione = TimeSpan.FromMilliseconds(500);

    /// <summary>Costruisce l'handler per il canale locale di questa macchina.</summary>
    /// <returns>L'handler, da consegnare a un client HTTP.</returns>
    public static SocketsHttpHandler Crea() =>
        new()
        {
            ConnectCallback = async (_, annulla) =>
            {
                using CancellationTokenSource scadenza = new(TimeoutDiConnessione);
                using CancellationTokenSource insieme =
                    CancellationTokenSource.CreateLinkedTokenSource(annulla, scadenza.Token);

                return OperatingSystem.IsWindows()
                    ? await ApriPipeAsync(insieme.Token).ConfigureAwait(false)
                    : await ApriSocketAsync(insieme.Token).ConfigureAwait(false);
            },
        };

    private static async Task<Stream> ApriPipeAsync(CancellationToken annulla)
    {
        // Il punto, e NON "localhost". Misurato: con "localhost" la connessione passa da SMB e
        // il servizio la classifica come proveniente dalla RETE, quindi pretenderebbe il token
        // che qui non abbiamo. Solo il punto e' la via locale.
        NamedPipeClientStream flusso = new(
            ".",
            ObserverEndpoint.NomeCanaleLocale,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // Identification e non Impersonation: al servizio basta SAPERE chi siamo, non gli
            // serve poter agire per conto nostro. Si concede il minimo che funziona.
            TokenImpersonationLevel.Identification);

        await flusso.ConnectAsync(annulla).ConfigureAwait(false);

        return flusso;
    }

    private static async Task<Stream> ApriSocketAsync(CancellationToken annulla)
    {
        Socket presa = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await presa.ConnectAsync(
                new UnixDomainSocketEndPoint(ObserverEndpoint.PercorsoSocketLocale), annulla)
                .ConfigureAwait(false);
        }
        catch
        {
            presa.Dispose();
            throw;
        }

        return new NetworkStream(presa, ownsSocket: true);
    }
}