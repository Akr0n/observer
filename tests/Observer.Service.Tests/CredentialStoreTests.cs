using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>The on-disk store for the machine token.</summary>
[Collection(ProcessEnvironment.Name)]
public class CredentialStoreTests : IDisposable
{
    private readonly string directory;

    public CredentialStoreTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-dep-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    private string StorePath => Path.Combine(directory, "credentials.json");

    [Fact]
    public void AMissingStoreIsNotAnError()
    {
        // The first start is the normal case, not a fault.
        Assert.Null(CredentialStore.Read(StorePath));
    }

    [Fact]
    public void WhatIsWrittenReadsBackUnchanged()
    {
        MachineCredentials written = MachineCredentials.Create()
            .Rotate(DateTimeOffset.UtcNow, TimeSpan.FromHours(24));

        CredentialStore.Write(StorePath, written);

        MachineCredentials? readBack = CredentialStore.Read(StorePath);

        Assert.NotNull(readBack);
        Assert.Equal(written.Current, readBack.Current);
        Assert.Equal(written.Previous, readBack.Previous);
        Assert.Equal(
            written.PreviousExpiresAt!.Value.ToUnixTimeSeconds(),
            readBack.PreviousExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void RewritingLeavesNoTemporaryFilesBehind()
    {
        // A forgotten temp file holds the secret in the clear, and with the permissions
        // inherited from the folder instead of the store's own. Measured: it really does
        // happen when the replacement fails.
        for (int i = 0; i < 3; i++)
        {
            CredentialStore.Write(StorePath, MachineCredentials.Create());
        }

        string[] remaining = Directory.GetFiles(directory);

        Assert.Single(remaining);
        Assert.Equal(StorePath, remaining[0]);
    }

    [Fact]
    public void ARewriteReallyReplacesTheStore()
    {
        MachineCredentials first = MachineCredentials.Create();
        CredentialStore.Write(StorePath, first);

        MachineCredentials second = MachineCredentials.Create();
        CredentialStore.Write(StorePath, second);

        MachineCredentials? readBack = CredentialStore.Read(StorePath);

        Assert.NotNull(readBack);
        Assert.Equal(second.Current, readBack.Current);
        Assert.NotEqual(first.Current, readBack.Current);
    }

    [Fact]
    public void AnUnreadableStoreDoesNotSilentlyBecomeAMISSINGStore()
    {
        // Load-bearing distinction: "it is not there" means generate a new one, "I cannot
        // read it" means stop. Blurring the two would regenerate the key at every start,
        // cutting off every remote client with nobody able to work out why.
        File.WriteAllText(StorePath, "questo non e' JSON {{{");

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Read(StorePath));
    }

    [Fact]
    public void TheStoreHoldsNothingButTheKeysAndTheExpiry()
    {
        // The file ends up in front of an administrator who is investigating: what it holds
        // must be obvious, and it must hold nothing more.
        CredentialStore.Write(StorePath, MachineCredentials.Create().Rotate(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));

        string contents = File.ReadAllText(StorePath);

        Assert.Contains("current", contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("previous", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", contents, StringComparison.OrdinalIgnoreCase);
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