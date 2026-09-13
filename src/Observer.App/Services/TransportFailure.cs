using System.Net.Sockets;

namespace Observer.App.Services;

/// <summary>
/// Da che cosa e' fallito il trasporto, quando e' fallito.
/// </summary>
/// <remarks>
/// Funzione pura, e sta da sola per la stessa ragione di <see cref="StatusEscalation"/>: la
/// regola si prova costruendo le eccezioni a mano, senza aprire un socket. Cio' che a tavolino
/// NON si puo' sapere e' se .NET consegni davvero quello che questa tabella si aspetta, ed e'
/// per quello che esiste anche un test su un trasporto vero.
/// <para>
/// La distinzione che questo tipo esiste per fare: una connessione <b>rifiutata</b> e' la
/// risposta piu' informativa che un guasto possa dare — il pacchetto e' arrivato, la macchina
/// ha risposto, e manca solo qualcuno in ascolto su quella porta. Un <b>tempo scaduto</b> dice
/// l'opposto: non ha risposto nessuno. I rimedi non si somigliano affatto.
/// </para>
/// </remarks>
public static class TransportFailure
{
    // La catena di eccezioni non ha una profondita' garantita: .NET incarta la SocketException
    // dentro una IOException e quella dentro una HttpRequestException, ma e' un dettaglio di
    // implementazione. Si scende finche' si trova, con un fondo per non restare appesi a una
    // catena che si morde la coda.
    private const int MaxDepth = 8;

    /// <summary>Traduce un guasto di trasporto nell'esito da mostrare.</summary>
    /// <param name="error">L'error arrivata dal client HTTP.</param>
    /// <returns>L'esito corrispondente.</returns>
    public static ServiceOutcome Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // Il timeout del client non passa mai per il socket: e' HttpClient ad annullare la
        // propria richiesta, e cio' che si vede e' un annullamento. Chi cercasse soltanto
        // SocketError.TimedOut non troverebbe mai il caso piu' frequente di tutti.
        if (error is OperationCanceledException)
        {
            return ServiceOutcome.TimedOut;
        }

        return FindSocketError(error) switch
        {
            SocketError.ConnectionRefused => ServiceOutcome.ConnectionRefused,
            SocketError.TimedOut => ServiceOutcome.TimedOut,

            // Tutto il resto resta generico apposta. Un nome che non si risolve, una rete
            // irraggiungibile e un handshake TLS fallito sono guasti diversi fra loro, e
            // inventare per ciascuno un titolo che non si sa scrivere bene sarebbe peggio di
            // un titolo onestamente generico.
            _ => ServiceOutcome.Unreachable,
        };
    }

    private static SocketError? FindSocketError(Exception error)
    {
        Exception? current = error;

        for (int depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode;
            }

            current = current.InnerException;
        }

        return null;
    }
}