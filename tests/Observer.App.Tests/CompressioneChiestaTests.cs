using System.Net;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Chi chiede la compressione e chi no, che e' la meta' client della decisione.
/// </summary>
/// <remarks>
/// La codifica si NEGOZIA per richiesta: un servizio che comprime e un client che non lo chiede
/// si scambiano esattamente i byte di prima. Quindi le due meta' stanno insieme o non stanno, e
/// questa prova esiste perche' la meta' client e' invisibile - nessuna schermata cambia, nessun
/// numero si muove, e cancellarla non farebbe fallire niente altro.
/// </remarks>
public class CompressioneChiestaTests
{
    private const string ImprontaFinta =
        "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    [Fact]
    public void SulFiloSiChiedeLaCompressione()
    {
        // E' il percorso che paga i byte: la coda grezza dello storico pesa 76 kB a un'ora e
        // 114 a ventiquattro, una volta per quadrante.
        using SocketsHttpHandler handler = new CertificatePinning(ImprontaFinta).Handler();

        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }

    [Fact]
    public void SulCanaleLocaleNoEQuestaEUnaScelta()
    {
        // Sulla pipe (o sul socket unix) i byte non attraversano niente. Chiederla li'
        // significherebbe far comprimere e decomprimere la macchina che questo programma STA
        // MISURANDO, cioe' pagare CPU che finisce nel numero mostrato per risparmiare byte che
        // non esistono. E l'esclusione non ha bisogno di alcun ramo nel servizio: il servizio
        // comprime solo cio' che gli viene chiesto, quindi basta non chiedere.
        using SocketsHttpHandler handler = LocalChannelHandler.Crea();

        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }
}