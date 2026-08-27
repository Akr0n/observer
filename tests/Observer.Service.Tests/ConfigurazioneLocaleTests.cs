using Observer.Service;

namespace Observer.Service.Tests;

/// <summary>
/// Il file di configurazione locale, che puo' esserci, non esserci, o esserci vuoto.
/// </summary>
/// <remarks>
/// AddJsonFile(optional: true) tollera un file ASSENTE, non un file VUOTO. Un file di zero byte
/// fa fallire l'avvio con "The input does not contain any JSON tokens" e uno stack trace, che e'
/// un modo pessimo di scoprire di aver svuotato un file invece di cancellarlo — ed e' la cosa
/// che uno fa naturalmente quando gli si dice di togliere il token da quel file.
/// </remarks>
public class ConfigurazioneLocaleTests : IDisposable
{
    private readonly string cartella;

    public ConfigurazioneLocaleTests()
    {
        cartella = Path.Combine(Path.GetTempPath(), "obs-cfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(cartella);
    }

    [Fact]
    public void UnFileASSENTENonVaCaricato()
    {
        Assert.False(ConfigurazioneLocale.VaCaricato(Path.Combine(cartella, "non-c-e.json")));
    }

    [Fact]
    public void UnFileVUOTONonVaCaricato()
    {
        string percorso = Path.Combine(cartella, "vuoto.json");
        File.WriteAllText(percorso, string.Empty);

        Assert.False(ConfigurazioneLocale.VaCaricato(percorso));
    }

    [Fact]
    public void UnFileDiSOLOSPAZIONonVaCaricato()
    {
        // Un file "svuotato" con un editor spesso resta con un ritorno a capo dentro.
        string percorso = Path.Combine(cartella, "spazi.json");
        File.WriteAllText(percorso, "\r\n   \r\n");

        Assert.False(ConfigurazioneLocale.VaCaricato(percorso));
    }

    [Fact]
    public void UnFileCONCONTENUTOVaCaricato()
    {
        string percorso = Path.Combine(cartella, "pieno.json");
        File.WriteAllText(percorso, "{ \"Observer\": { \"ApiToken\": \"x\" } }");

        Assert.True(ConfigurazioneLocale.VaCaricato(percorso));
    }

    [Fact]
    public void UnFileCONTENENTEJSONSBAGLIATOVaCaricatoLoSTESSO()
    {
        // Qui NON si tollera: un file con dentro qualcosa che non e' JSON e' un errore vero, e
        // farlo fallire e' giusto. La tolleranza vale solo per "non c'e' niente da leggere",
        // che e' indistinguibile dall'assenza.
        string percorso = Path.Combine(cartella, "rotto.json");
        File.WriteAllText(percorso, "{{{ non e' json");

        Assert.True(ConfigurazioneLocale.VaCaricato(percorso));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(cartella, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}