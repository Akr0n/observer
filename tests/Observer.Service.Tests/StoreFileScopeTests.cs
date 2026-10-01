using System.Globalization;
using System.Runtime.Versioning;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>Which readers read strictly: root, in a folder that another account owns.</summary>
/// <remarks>
/// This is the rule that decides WHO is refused a link or a FIFO, so it is pinned where it can run
/// for everyone: the table needs no file system, and the folder test makes its own directories and
/// reads their owners with stat(1), never with the code under test. The attribute on the class is
/// for the analyzer only (CA1416 looks at call sites). The table runs everywhere because it is pure;
/// the folder test runs on Linux only, so no test on Windows reaches the operating-system gate of
/// <c>MustBeStrict</c>.
/// </remarks>
[SupportedOSPlatform("linux")]
public class StoreFileScopeTests : IDisposable
{
    private readonly string directory;

    public StoreFileScopeTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-scope-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    [Theory]
    [InlineData(true, 998u, true)]
    [InlineData(true, 0u, false)]
    [InlineData(true, null, false)]
    [InlineData(false, 998u, false)]
    [InlineData(false, 0u, false)]
    [InlineData(false, null, false)]
    public void OnlyRootInAFolderOfAnotherAccountIsStrict(bool privileged, uint? directoryOwner, bool expected)
    {
        // Root in its own folder has nobody to be protected from. An owner nobody can name is not
        // a mismatch, for the same reason FollowDirectory does not call it one. And the service,
        // which is not root, must read exactly as it always did.
        Assert.Equal(expected, StoreFile.IsStrict(privileged, directoryOwner));
    }

    [LinuxOnly]
    public void TheFoldersOwnerDecidesAndNotTheFilesOrTheReaders()
    {
        uint owner = uint.Parse(Run("stat", "-c", "%u", directory), CultureInfo.InvariantCulture);
        bool expected = owner != 0;

        string store = Path.Combine(directory, "credentials.json");

        Assert.Equal(expected, StoreFile.MustBeStrict(store, privileged: true));

        // The name the attacker plants is a link to a root-owned file. Asking who owns THAT would
        // answer "root" and switch the protection off at the moment it is needed.
        string link = Path.Combine(directory, "certificate.pfx");
        File.CreateSymbolicLink(link, "/etc/hostname");

        Assert.Equal(expected, StoreFile.MustBeStrict(link, privileged: true));

        Assert.False(StoreFile.MustBeStrict("/etc/credentials.json", privileged: true));
        Assert.False(StoreFile.MustBeStrict(Path.Combine(directory, "missing", "credentials.json"), privileged: true));
        Assert.False(StoreFile.MustBeStrict(store, privileged: false));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Run(string tool, params string[] arguments) =>
        CredentialOwnershipLinuxTests.Run(tool, arguments);
}