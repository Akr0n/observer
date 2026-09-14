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
public class LocalConfigurationFileTests : IDisposable
{
    private readonly string folder;

    public LocalConfigurationFileTests()
    {
        folder = Path.Combine(Path.GetTempPath(), "obs-cfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
    }

    [Fact]
    public void AnABSENTFileIsNotLoaded()
    {
        Assert.False(LocalConfigurationFile.ShouldLoad(Path.Combine(folder, "non-c-e.json")));
    }

    [Fact]
    public void AnEMPTYFileIsNotLoaded()
    {
        string filePath = Path.Combine(folder, "vuoto.json");
        File.WriteAllText(filePath, string.Empty);

        Assert.False(LocalConfigurationFile.ShouldLoad(filePath));
    }

    [Fact]
    public void AFileOfONLYWHITESPACEIsNotLoaded()
    {
        // Un file "svuotato" con un editor spesso resta con un ritorno a capo dentro.
        string filePath = Path.Combine(folder, "spazi.json");
        File.WriteAllText(filePath, "\r\n   \r\n");

        Assert.False(LocalConfigurationFile.ShouldLoad(filePath));
    }

    [Fact]
    public void AFileWITHCONTENTIsLoaded()
    {
        string filePath = Path.Combine(folder, "pieno.json");
        File.WriteAllText(filePath, "{ \"Observer\": { \"ApiToken\": \"x\" } }");

        Assert.True(LocalConfigurationFile.ShouldLoad(filePath));
    }

    [Fact]
    public void AFileWithWRONGJSONIsLoadedALLTHESAME()
    {
        // Qui NON si tollera: un file con dentro qualcosa che non e' JSON e' un errore vero, e
        // farlo fallire e' giusto. La tolleranza vale solo per "non c'e' niente da leggere",
        // che e' indistinguibile dall'assenza.
        string filePath = Path.Combine(folder, "rotto.json");
        File.WriteAllText(filePath, "{{{ non e' json");

        Assert.True(LocalConfigurationFile.ShouldLoad(filePath));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}