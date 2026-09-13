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
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Costruisce l'handler per il canale locale di questa macchina.</summary>
    /// <returns>L'handler, da consegnare a un client HTTP.</returns>
    public static SocketsHttpHandler Create() =>
        new()
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                using CancellationTokenSource deadline = new(ConnectTimeout);
                using CancellationTokenSource linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

                return OperatingSystem.IsWindows()
                    ? await OpenPipeAsync(linkedCancellation.Token).ConfigureAwait(false)
                    : await OpenSocketAsync(linkedCancellation.Token).ConfigureAwait(false);
            },
        };

    private static async Task<Stream> OpenPipeAsync(CancellationToken cancellationToken)
    {
        // Il punto, e NON "localhost". Misurato: con "localhost" la connessione passa da SMB e
        // il servizio la classifica come proveniente dalla RETE, quindi pretenderebbe il token
        // che qui non abbiamo. Solo il punto e' la via locale.
        NamedPipeClientStream pipe = new(
            ".",
            ObserverEndpoint.LocalChannelName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // Identification e non Impersonation: al servizio basta SAPERE chi siamo, non gli
            // serve poter agire per conto nostro. Si concede il minimo che funziona.
            TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        return pipe;
    }

    private static async Task<Stream> OpenSocketAsync(CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(ObserverEndpoint.LocalSocketPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}