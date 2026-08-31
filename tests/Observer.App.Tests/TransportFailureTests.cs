using System.Net.Sockets;
using System.Security.Authentication;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Perche' il collegamento non e' riuscito, quando non riesce.
/// </summary>
/// <remarks>
/// Due guasti che sul filo si somigliano e nella stanza no. Una connessione <b>rifiutata</b>
/// torna indietro subito, e dice una cosa precisa: la macchina c'e' ed e' raggiungibile, e' il
/// servizio che non ascolta su quella porta. Un <b>tempo scaduto</b> dice il contrario: non ha
/// risposto nessuno, e la causa piu' comune e' qualcosa che scarta i pacchetti senza dirlo.
/// <para>
/// I rimedi sono opposti — avviare un servizio contro aprire una porta — e finche' la
/// dashboard li chiamava tutti e due "Service unreachable" chi guardava doveva indovinare.
/// E' costato un pomeriggio vero su una macchina in dominio, dove la rete di casa era
/// classificata come pubblica e la regola del firewall valeva su un altro profilo.
/// </para>
/// </remarks>
public class TransportFailureTests
{
    [Fact]
    public void UnaConnessioneRifiutataSiRiconosce()
    {
        SocketException socket = new((int)SocketError.ConnectionRefused);

        // Guardia: se questa riga cadesse, il resto del test starebbe misurando un'altra cosa
        // e passerebbe o fallirebbe per il motivo sbagliato.
        Assert.Equal(SocketError.ConnectionRefused, socket.SocketErrorCode);

        Assert.Equal(
            ServiceOutcome.ConnessioneRifiutata,
            TransportFailure.Classifica(new HttpRequestException("rifiutata", socket)));
    }

    [Fact]
    public void UnTempoScadutoSulSocketSiRiconosce()
    {
        HttpRequestException guasto = new("scaduta", new SocketException((int)SocketError.TimedOut));

        Assert.Equal(ServiceOutcome.TempoScaduto, TransportFailure.Classifica(guasto));
    }

    [Fact]
    public void IlTimeoutDelClientArrivaComeAnnullamento()
    {
        // Quando scade HttpClient.Timeout non arriva nessuna SocketException: HttpClient
        // annulla la propria richiesta, e cio' che si vede e' un OperationCanceledException
        // con dentro un TimeoutException. Chi cercasse solo nel socket non lo troverebbe mai.
        TaskCanceledException scaduto = new("annullata", new TimeoutException());

        Assert.Equal(ServiceOutcome.TempoScaduto, TransportFailure.Classifica(scaduto));
    }

    [Fact]
    public void IlSocketSiCercaInFondoAllaCatena()
    {
        // .NET non consegna la SocketException al primo livello: la incarta in una
        // IOException e quella in una HttpRequestException. Guardare solo InnerException
        // basterebbe oggi e smetterebbe di bastare al primo cambio di runtime.
        HttpRequestException profonda = new(
            "rifiutata",
            new IOException(
                "connessione interrotta",
                new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(ServiceOutcome.ConnessioneRifiutata, TransportFailure.Classifica(profonda));
    }

    [Fact]
    public void UnNomeCheNonSiRisolveNonDiventaUnRifiuto()
    {
        // Un nome sbagliato non e' ne' un servizio spento ne' un firewall: dire "il servizio
        // non e' in esecuzione" manderebbe a cercare su una macchina che non esiste.
        HttpRequestException nome = new("nome ignoto", new SocketException((int)SocketError.HostNotFound));

        Assert.Equal(ServiceOutcome.NonRaggiungibile, TransportFailure.Classifica(nome));
    }

    [Fact]
    public void UnGuastoTlsNonDiventaUnRifiuto()
    {
        // L'impronta che non corrisponde ha un esito suo, deciso prima di arrivare qui. Se
        // questo classificatore se ne appropriasse, un certificato cambiato — cioe' una
        // reinstallazione oppure qualcuno in mezzo — si leggerebbe come "servizio spento".
        HttpRequestException tls = new("handshake", new AuthenticationException("certificato"));

        Assert.Equal(ServiceOutcome.NonRaggiungibile, TransportFailure.Classifica(tls));
    }

    [Fact]
    public void UnGuastoSenzaSocketRestaGenerico()
    {
        Assert.Equal(
            ServiceOutcome.NonRaggiungibile,
            TransportFailure.Classifica(new HttpRequestException("qualcosa e' andato storto")));
    }

    [Fact]
    public void UnaCatenaSenzaSocketNonBloccaIlClassificatore()
    {
        // Difensivo, ma il costo di sbagliarlo e' un'interfaccia che si pianta invece di
        // mostrare un errore: la ricerca nella catena deve avere un fondo comunque.
        InvalidOperationException dentro = new("dentro");
        HttpRequestException fuori = new("fuori", dentro);

        Assert.Equal(ServiceOutcome.NonRaggiungibile, TransportFailure.Classifica(fuori));
    }
}