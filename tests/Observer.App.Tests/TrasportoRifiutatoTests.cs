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
public class TrasportoRifiutatoTests
{
    [Fact]
    public async Task UnaPortaChiusaSuIndirizzoLetteraleSiPresentaComeRifiuto()
    {
        using MetricsClient client = new(Verso($"http://127.0.0.1:{PortaChiusa()}/"));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnessioneRifiutata, esito.Outcome);
    }

    [Fact]
    public async Task UnaPortaChiusaSuUnNomeADoppiaPilaSiPresentaComeRifiuto()
    {
        // Il caso che il budget precedente non copriva. Se un giorno qualcuno riabbassasse
        // RequestTimeout, questo test tornerebbe rosso — ed e' l'unico posto in cui quel
        // numero e' legato a cio' che protegge.
        using MetricsClient client = new(Verso($"http://localhost:{PortaChiusa()}/"));

        SnapshotFetch esito = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(ServiceOutcome.ConnessioneRifiutata, esito.Outcome);

        // Il danno vero non era l'etichetta: era il consiglio. Mandare a cercare un firewall
        // mentre il servizio e' spento costa il pomeriggio di chi lo segue.
        Assert.DoesNotContain("dropping the packets", esito.Problem, StringComparison.Ordinal);
    }

    private static ObserverEndpoint Verso(string indirizzo) =>
        ObserverEndpoint.Remoto(new Uri(indirizzo), "il-token", "dalla prova");

    /// <summary>Una porta su cui si e' sicuri che non ascolti nessuno.</summary>
    /// <remarks>
    /// Si fa aprire al sistema una porta effimera e la si chiude subito: e' l'unico modo di
    /// avere un numero libero senza sceglierlo a caso e sperare.
    /// </remarks>
    private static int PortaChiusa()
    {
        TcpListener ascoltatore = new(IPAddress.Loopback, 0);
        ascoltatore.Start();
        int porta = ((IPEndPoint)ascoltatore.LocalEndpoint).Port;
        ascoltatore.Stop();

        return porta;
    }
}