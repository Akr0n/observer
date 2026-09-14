using Observer.Service;

namespace Observer.Service.Tests;

/// <summary>
/// The local configuration file, which may be there, may not be there, or may be there empty.
/// </summary>
/// <remarks>
/// AddJsonFile(optional: true) tolerates an ABSENT file, not an EMPTY one. A zero-byte file makes
/// start-up fail with "The input does not contain any JSON tokens" and a stack trace, which is a
/// terrible way to find out you emptied a file instead of deleting it — and emptying it is what
/// one naturally does when told to take the token out of that file.
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
        // A file "emptied" with an editor is often left with a newline inside it.
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
        // Here there is NO tolerance: a file with something in it that is not JSON is a real
        // error, and failing is the right thing. The tolerance only covers "there is nothing to
        // read", which is indistinguishable from the file being absent.
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