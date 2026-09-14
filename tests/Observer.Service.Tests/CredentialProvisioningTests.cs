using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Da dove il servizio prende il proprio token di macchina, e cosa fa quando non ci riesce.
/// </summary>
/// <remarks>
/// E' il pezzo che rende possibile un installer: finche' il servizio pretende un token in
/// configurazione, chi installa deve generarne uno, cioe' conoscerlo, tracciarlo nel proprio
/// log e lasciarselo dietro se fallisce a meta'.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
public class CredentialProvisioningTests : IDisposable
{
    private readonly string directory;

    public CredentialProvisioningTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-prov-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    private string StorePath => Path.Combine(directory, "credentials.json");

    [Fact]
    public void ATokenInCONFIGURATIONTakesPrecedence()
    {
        // Retrocompatibilita', ed e' cio' che tiene in piedi i test e la CI: chi ha gia' un
        // token in appsettings.Local.json non deve accorgersi di niente.
        ProvisionedCredentials result = CredentialProvisioning.Provision(
            "token-scelto-a-mano", StorePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Configuration, result.Origin);
        Assert.Equal("token-scelto-a-mano", result.Credentials.Current);
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
        // Rigenerare a ogni avvio taglierebbe fuori ogni client remoto ogni volta che la
        // macchina si riavvia, e nessuno collegherebbe le due cose.
        ProvisionedCredentials first = CredentialProvisioning.Provision(null, StorePath, runningAsService: false);
        ProvisionedCredentials second = CredentialProvisioning.Provision(null, StorePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Stored, second.Origin);
        Assert.Equal(first.Credentials.Current, second.Credentials.Current);
    }

    [Fact]
    public void IfTheStoreCannotBeSecuredAndRunningASASERVICE_TheServiceRefusesToStart()
    {
        // Un servizio che deposita in silenzio un token leggibile da tutti e' peggio di un
        // servizio che non parte. Un servizio che non parte si nota subito.
        Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, ImpossiblePath(), runningAsService: true));
    }

    [Fact]
    public void IfTheStoreCannotBeSecuredButRunningBYHAND_TheTokenIsEPHEMERAL()
    {
        // E' il caso di "dotnet run" durante lo sviluppo, e di meta' della CI. Mai un ripiego
        // per-utente su disco: sposterebbe il segreto in un posto meno protetto facendo
        // credere di averlo messo al sicuro.
        string impossiblePath = ImpossiblePath();

        ProvisionedCredentials result = CredentialProvisioning.Provision(null, impossiblePath, runningAsService: false);

        Assert.Equal(CredentialOrigin.Ephemeral, result.Origin);
        Assert.False(string.IsNullOrWhiteSpace(result.Credentials.Current));
        Assert.False(File.Exists(impossiblePath));
    }

    [Fact]
    public void ACORRUPTStoreIsNotOverwrittenSilently()
    {
        // Sovrascriverlo genererebbe una chiave nuova e butterebbe via quella che i client
        // remoti stanno usando, per un guasto che potrebbe essere una modifica a mano
        // sbagliata di un minuto prima.
        File.WriteAllText(StorePath, "non e' JSON {{{");

        Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, StorePath, runningAsService: false));
    }

    /// <summary>Un percorso in cui nessun utente, su nessun sistema, puo' creare una cartella.</summary>
    private string ImpossiblePath()
    {
        // Una cartella non puo' esistere DENTRO un file: vale su Windows come su Linux, per
        // l'amministratore come per l'utente standard. Serve un caso deterministico, non uno
        // che dipenda da chi esegue i test — su un runner di CI si e' spesso amministratori.
        string blockingFile = Path.Combine(directory, "sono-un-file");
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