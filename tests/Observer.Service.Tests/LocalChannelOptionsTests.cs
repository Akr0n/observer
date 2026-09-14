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
    public void TheDefaultValuesAreValid()
    {
        LocalChannelOptions options = new();

        options.Validate();

        Assert.True(options.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.PipeName));
        Assert.False(string.IsNullOrWhiteSpace(options.SocketPath));
    }

    [Fact]
    public void ASocketPathThatIsTooLongIsRejected()
    {
        // Il limite e' 107 byte. La convalida deve scattare all'avvio e non a StartAsync, dove
        // porterebbe giu' anche l'endpoint TCP.
        LocalChannelOptions options = new()
        {
            SocketPath = "/" + new string('a', 200) + "/observer.sock",
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("107", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeSocketPathIsRejected()
    {
        LocalChannelOptions options = new() { SocketPath = "observer.sock" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AnEmptyPipeNameIsRejected()
    {
        LocalChannelOptions options = new() { PipeName = "   " };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void NothingIsValidatedWhenTheChannelIsDisabled()
    {
        // Una macchina che non vuole il canale locale non deve inventarsi un percorso valido
        // per poter partire.
        LocalChannelOptions options = new()
        {
            Enabled = false,
            PipeName = string.Empty,
            SocketPath = "non-assoluto",
        };

        options.Validate();
    }
}