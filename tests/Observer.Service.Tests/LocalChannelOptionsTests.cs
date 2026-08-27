using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// Le opzioni del canale locale si rifiutano di partire con valori inutilizzabili.
/// </summary>
/// <remarks>
/// Nome della pipe e percorso del socket sono CONFIGURABILI, e non e' una comodita': un
/// endpoint che non si binda abbatte l'INTERO host, endpoint TCP compreso. Con valori fissi,
/// lanciare il servizio a mano su una macchina dove quello installato gira non fallirebbe piu'
/// "solo sulla porta": non partirebbe affatto.
/// </remarks>
public class LocalChannelOptionsTests
{
    [Fact]
    public void IValoriPredefinitiSonoValidi()
    {
        LocalChannelOptions opzioni = new();

        opzioni.Validate();

        Assert.True(opzioni.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(opzioni.PipeName));
        Assert.False(string.IsNullOrWhiteSpace(opzioni.SocketPath));
    }

    [Fact]
    public void UnPercorsoDiSocketTroppoLungoVieneRifiutato()
    {
        // Il limite e' 107 byte. La convalida deve scattare all'avvio e non a StartAsync, dove
        // porterebbe giu' anche l'endpoint TCP.
        LocalChannelOptions opzioni = new()
        {
            SocketPath = "/" + new string('a', 200) + "/observer.sock",
        };

        InvalidOperationException errore = Assert.Throws<InvalidOperationException>(opzioni.Validate);

        Assert.Contains("107", errore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnPercorsoDiSocketRelativoVieneRifiutato()
    {
        LocalChannelOptions opzioni = new() { SocketPath = "observer.sock" };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void UnNomeDiPipeVuotoVieneRifiutato()
    {
        LocalChannelOptions opzioni = new() { PipeName = "   " };

        Assert.Throws<InvalidOperationException>(opzioni.Validate);
    }

    [Fact]
    public void ACanaleSpentoNienteVieneConvalidato()
    {
        // Una macchina che non vuole il canale locale non deve inventarsi un percorso valido
        // per poter partire.
        LocalChannelOptions opzioni = new()
        {
            Enabled = false,
            PipeName = string.Empty,
            SocketPath = "non-assoluto",
        };

        opzioni.Validate();
    }
}