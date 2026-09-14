using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>Il deposito su disco del token di macchina.</summary>
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
        // Il primo avvio e' il caso normale, non un guasto.
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
        // Un temporaneo dimenticato contiene il segreto in chiaro, e con i permessi ereditati
        // della cartella invece di quelli del deposito. Misurato: capita davvero quando la
        // sostituzione fallisce.
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
        // Distinzione portante: "non c'e'" significa generane uno nuovo, "non riesco a
        // leggerlo" significa fermati. Confonderli farebbe rigenerare la chiave a ogni avvio,
        // tagliando fuori ogni client remoto senza che nessuno capisca perche'.
        File.WriteAllText(StorePath, "questo non e' JSON {{{");

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Read(StorePath));
    }

    [Fact]
    public void TheStoreHoldsNothingButTheKeysAndTheExpiry()
    {
        // Il file finisce sotto gli occhi di un amministratore che indaga: deve essere ovvio
        // cosa contiene, e non deve contenere niente di piu'.
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