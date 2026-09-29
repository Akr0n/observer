using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Where the service gets its own machine token from, and what it does when it cannot.
/// </summary>
/// <remarks>
/// This is the piece that makes an installer possible: as long as the service demands a token in
/// configuration, whoever installs it has to generate one — which means knowing it, recording it
/// in their own log, and leaving it behind if the install fails halfway through.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class CredentialProvisioningTests : IDisposable
{
    private readonly string directory;

    public CredentialProvisioningTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-prov-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        // Secured before anything is written into it, which is the order production has: the service
        // writes the store only after Prepare made the directory safe. On Windows a fresh temp
        // directory inherits its DACL and evaluates as OpenDacl, and a store found in a directory
        // whose permissions vouch for nobody is now refused rather than adopted.
        CredentialDirectory.Prepare(StorePath);
    }

    private string StorePath => Path.Combine(directory, "credentials.json");

    [Fact]
    public void ATokenInCONFIGURATIONTakesPrecedence()
    {
        // Backward compatibility, and it is what keeps the tests and CI working: anyone who
        // already has a token in appsettings.Local.json should not notice any difference.
        ProvisionedCredentials result = CredentialProvisioning.Provision(
            "hand-picked-token", StorePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Configuration, result.Origin);
        Assert.Equal("hand-picked-token", result.Credentials.Current);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WithNoStoreAndNoConfiguration_ItGeneratesOneAndStoresIt()
    {
        ProvisionedCredentials result = CredentialProvisioning.Provision(null, StorePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.CreatedAndStored, result.Origin);
        Assert.False(string.IsNullOrWhiteSpace(result.Credentials.Current));
        Assert.NotNull(CredentialStore.Read(StorePath));
    }

    [Fact]
    public void TheSecondStartREUSESTheKeyInsteadOfRegeneratingIt()
    {
        // Regenerating at every start would cut off every remote client each time the machine
        // reboots, and nobody would connect the two things.
        ProvisionedCredentials first = CredentialProvisioning.Provision(null, StorePath, runningAsService: false);
        ProvisionedCredentials second = CredentialProvisioning.Provision(null, StorePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Stored, second.Origin);
        Assert.Equal(first.Credentials.Current, second.Credentials.Current);
    }

    [Fact]
    public void IfTheStoreCannotBeSecuredAndRunningASASERVICE_TheServiceRefusesToStart()
    {
        // A service that silently stores a token everyone can read is worse than a service
        // that does not start. A service that does not start gets noticed immediately.
        Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, ImpossiblePath(), runningAsService: true));
    }

    [Fact]
    public void IfTheStoreCannotBeSecuredButRunningBYHAND_TheTokenIsEPHEMERAL()
    {
        // This is the "dotnet run" case during development, and half of CI. Never a per-user
        // fallback on disk: it would move the secret somewhere less protected while making
        // you believe it had been put somewhere safe.
        string impossiblePath = ImpossiblePath();

        ProvisionedCredentials result = CredentialProvisioning.Provision(null, impossiblePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Ephemeral, result.Origin);
        Assert.False(string.IsNullOrWhiteSpace(result.Credentials.Current));
        Assert.False(File.Exists(impossiblePath));
    }

    [Fact]
    public void ACORRUPTStoreIsNotOverwrittenSilently()
    {
        // Overwriting it would generate a new key and throw away the one the remote clients
        // are using, over a fault that could be a botched hand edit from a minute earlier.
        File.WriteAllText(StorePath, "not JSON {{{");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, StorePath, runningAsService: false));

        // The MESSAGE and not just the type. Several guards on this path throw
        // InvalidOperationException - the directory-trust refusal among them - so a type-only
        // assertion would go on passing while this test had stopped being about corrupt JSON at all.
        Assert.Contains("isn't valid JSON", error.Message, StringComparison.Ordinal);
        Assert.Equal("not JSON {{{", File.ReadAllText(StorePath));
    }

    /// <summary>A path where no user, on any system, can create a directory.</summary>
    private string ImpossiblePath()
    {
        // A directory cannot exist INSIDE a file: that holds on Windows as on Linux, for the
        // administrator as for the standard user. This needs a deterministic case, not one
        // that depends on who runs the tests — on a CI runner you are often an administrator.
        string blockingFile = Path.Combine(directory, "blocking-file");
        File.WriteAllText(blockingFile, "x");

        return Path.Combine(blockingFile, "Observer", "credentials.json");
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
}