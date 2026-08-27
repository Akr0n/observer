using System.Text;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// Un URL di endpoint scritto male non fallisce: fallisce PEGGIO.
/// </summary>
/// <remarks>
/// Misurato: con "http://unix:C:\percorso\x.sock" Kestrel non lancia e non avvisa, lega
/// [::]:80 su TUTTE le interfacce e ci mette dietro la telemetria della macchina. Questa
/// funzione esiste per trasformare quel silenzio in un rifiuto all'avvio.
/// </remarks>
public class EndpointUrlTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5057")]
    [InlineData("https://0.0.0.0:7051")]
    [InlineData("http://localhost:5057")]
    [InlineData("http://unix:/run/observer/observer.sock")]
    [InlineData("http://pipe:/Observer")]
    public void UrlValidi_NonProduconoAlcunProblema(string url) =>
        Assert.Null(EndpointUrl.Problema(url));

    [Theory]
    // Il caso che ha aperto la porta 80 su tutte le interfacce senza dire niente.
    [InlineData(@"http://unix:C:\Users\tizio\AppData\Local\Temp\x.sock")]
    // Percorso unix relativo: Kestrel lo rifiuta a StartAsync, cioe' troppo tardi per capirlo.
    [InlineData("http://unix:relativo.sock")]
    // Pipe senza la barra: stessa trappola del percorso Windows.
    [InlineData("http://pipe:Observer")]
    [InlineData("http://pipe:/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non-un-url")]
    public void UrlRotti_SpieganoIlProblema(string url) =>
        Assert.False(string.IsNullOrWhiteSpace(EndpointUrl.Problema(url)));

    [Fact]
    public void PercorsoDelSocketDi107Byte_Accettato_Di108_No()
    {
        // Il messaggio d'errore di .NET dice "between 1 and 108 characters" e MENTE: non conta
        // il terminatore NUL. Misurato per bisezione: 107 passa, 108 lancia
        // ArgumentOutOfRangeException. Una guardia scritta a 108 lascia passare esattamente il
        // caso di confine, che e' l'unico che conta.
        string a107 = "/" + new string('a', 106);
        string a108 = "/" + new string('a', 107);

        Assert.Equal(107, Encoding.UTF8.GetByteCount(a107));
        Assert.Equal(108, Encoding.UTF8.GetByteCount(a108));

        Assert.Null(EndpointUrl.Problema("http://unix:" + a107));
        Assert.NotNull(EndpointUrl.Problema("http://unix:" + a108));
    }

    [Fact]
    public void IlConteggioEInByteNonInCaratteri()
    {
        // Un percorso di 81 caratteri, meta' accentati, supera i 107 byte in UTF-8. Contare i
        // caratteri farebbe passare un percorso che il sistema operativo rifiuta.
        string accentato = "/" + new string('e', 40) + new string('\u00e8', 40);

        Assert.True(accentato.Length <= 107);
        Assert.True(Encoding.UTF8.GetByteCount(accentato) > 107);
        Assert.NotNull(EndpointUrl.Problema("http://unix:" + accentato));
    }
}