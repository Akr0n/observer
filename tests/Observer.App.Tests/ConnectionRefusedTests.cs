using System.Net;
using System.Net.Sockets;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Un rifiuto vero, su un socket vero.
/// </summary>
/// <remarks>
/// <see cref="TransportFailureTests"/> prova la REGOLA costruendo le eccezioni a mano; questo
/// prova l'unica cosa che a tavolino non si puo' sapere, cioe' che .NET consegni davvero
/// quello che quella regola si aspetta, e che ci arrivi dentro il tempo concesso.
/// <para>
/// La seconda meta' e' quella che serviva. Misurato su Windows con .NET 10, sei giri per
/// indirizzo: un rifiuto costa 2018-2104 ms, su loopback come sull'indirizzo di rete. Un nome
/// a doppia pila lo paga due volte, perche' gli indirizzi si provano in fila, e senza tappo
/// costa 4035-4121 ms. Con i 3 secondi di budget che il client aveva, "localhost" su una
/// porta chiusa non arrivava mai a dire "rifiutata": scadeva prima, e la finestra consigliava
/// di controllare il firewall per un servizio semplicemente spento. Un test costruito solo
/// sulle eccezioni sarebbe rimasto verde tutto il tempo.
/// <para>
/// E' anche il test che ha bocciato il primo budget. Sei secondi passavano su una macchina
/// scarica e sono caduti su una occupata, perche' meno di due secondi di margine su 4,1 non
/// sono un margine. Otto danno quasi il doppio del costo misurato.
/// </para>
/// </para>
/// </remarks>
public class ConnectionRefusedTests
{
    [Fact]
    public async Task AClosedPortOnALiteralAddressComesBackAsARefusal()
    {
        using MetricsClient client = new(EndpointFor($"http://127.0.0.1:{ClosedPort()}/"));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnectionRefused, fetch.Outcome);
    }

    [Fact]
    public async Task AClosedPortOnADualStackNameComesBackAsARefusalAndDoesNotBlameTheFirewall()
    {
        // Il caso che il budget precedente non copriva. Se un giorno qualcuno riabbassasse
        // RequestTimeout, questo test tornerebbe rosso — ed e' l'unico posto in cui quel
        // numero e' legato a cio' che protegge.
        using MetricsClient client = new(EndpointFor($"http://localhost:{ClosedPort()}/"));

        SnapshotFetch fetch = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnectionRefused, fetch.Outcome);

        // Il danno vero non era l'etichetta: era il consiglio. Mandare a cercare un firewall
        // mentre il servizio e' spento costa il pomeriggio di chi lo segue.
        Assert.DoesNotContain("dropping the packets", fetch.Problem, StringComparison.Ordinal);
    }

    private static ObserverEndpoint EndpointFor(string address) =>
        ObserverEndpoint.Remote(new Uri(address), "il-token", "dalla prova");

    /// <summary>Una porta su cui si e' sicuri che non ascolti nessuno.</summary>
    /// <remarks>
    /// Si fa aprire al sistema una porta effimera e la si chiude subito: e' l'unico modo di
    /// avere un numero libero senza sceglierlo a caso e sperare.
    /// </remarks>
    private static int ClosedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}