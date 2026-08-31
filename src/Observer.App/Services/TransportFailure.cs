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
    private const int ProfonditaMassima = 8;

    /// <summary>Traduce un guasto di trasporto nell'esito da mostrare.</summary>
    /// <param name="eccezione">L'eccezione arrivata dal client HTTP.</param>
    /// <returns>L'esito corrispondente.</returns>
    public static ServiceOutcome Classifica(Exception eccezione)
    {
        ArgumentNullException.ThrowIfNull(eccezione);

        // Il timeout del client non passa mai per il socket: e' HttpClient ad annullare la
        // propria richiesta, e cio' che si vede e' un annullamento. Chi cercasse soltanto
        // SocketError.TimedOut non troverebbe mai il caso piu' frequente di tutti.
        if (eccezione is OperationCanceledException)
        {
            return ServiceOutcome.TempoScaduto;
        }

        return ErroreDiSocket(eccezione) switch
        {
            SocketError.ConnectionRefused => ServiceOutcome.ConnessioneRifiutata,
            SocketError.TimedOut => ServiceOutcome.TempoScaduto,

            // Tutto il resto resta generico apposta. Un nome che non si risolve, una rete
            // irraggiungibile e un handshake TLS fallito sono guasti diversi fra loro, e
            // inventare per ciascuno un titolo che non si sa scrivere bene sarebbe peggio di
            // un titolo onestamente generico.
            _ => ServiceOutcome.NonRaggiungibile,
        };
    }

    private static SocketError? ErroreDiSocket(Exception eccezione)
    {
        Exception? corrente = eccezione;

        for (int passo = 0; corrente is not null && passo < ProfonditaMassima; passo++)
        {
            if (corrente is SocketException socket)
            {
                return socket.SocketErrorCode;
            }

            corrente = corrente.InnerException;
        }

        return null;
    }
}