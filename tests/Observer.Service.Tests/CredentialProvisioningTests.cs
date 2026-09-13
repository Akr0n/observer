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
[Collection(AmbienteDelProcesso.Nome)]
public class CredentialProvisioningTests : IDisposable
{
    private readonly string cartella;

    public CredentialProvisioningTests()
    {
        cartella = Path.Combine(Path.GetTempPath(), "obs-prov-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(cartella);
    }

    private string Percorso => Path.Combine(cartella, "credentials.json");

    [Fact]
    public void UnTokenInCONFIGURAZIONEVinceSuTutto()
    {
        // Retrocompatibilita', ed e' cio' che tiene in piedi i test e la CI: chi ha gia' un
        // token in appsettings.Local.json non deve accorgersi di niente.
        ProvisionedCredentials esito = CredentialProvisioning.Provision(
            "token-scelto-a-mano", Percorso, runningAsService: false);

        Assert.Equal(CredentialOrigin.Configuration, esito.Origin);
        Assert.Equal("token-scelto-a-mano", esito.Credentials.Current);
        Assert.False(File.Exists(Percorso));
    }

    [Fact]
    public void SenzaDepositoNeConfigurazione_NeGeneraUnoELoDeposita()
    {
        ProvisionedCredentials esito = CredentialProvisioning.Provision(null, Percorso, runningAsService: false);

        Assert.Equal(CredentialOrigin.CreatedAndStored, esito.Origin);
        Assert.False(string.IsNullOrWhiteSpace(esito.Credentials.Current));
        Assert.NotNull(CredentialStore.Read(Percorso));
    }

    [Fact]
    public void AlSecondoAvvioRIUSALaChiaveInvecediRigenerarla()
    {
        // Rigenerare a ogni avvio taglierebbe fuori ogni client remoto ogni volta che la
        // macchina si riavvia, e nessuno collegherebbe le due cose.
        ProvisionedCredentials primo = CredentialProvisioning.Provision(null, Percorso, runningAsService: false);
        ProvisionedCredentials secondo = CredentialProvisioning.Provision(null, Percorso, runningAsService: false);

        Assert.Equal(CredentialOrigin.Stored, secondo.Origin);
        Assert.Equal(primo.Credentials.Current, secondo.Credentials.Current);
    }

    [Fact]
    public void SeIlDepositoNonESicuroEsiGiraCOMESERVIZIO_nonSiParte()
    {
        // Un servizio che deposita in silenzio un token leggibile da tutti e' peggio di un
        // servizio che non parte. Un servizio che non parte si nota subito.
        Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, PercorsoImpossibile(), runningAsService: true));
    }

    [Fact]
    public void SeIlDepositoNonESicuroMaSiGiraAMANO_tokenEFFIMERO()
    {
        // E' il caso di "dotnet run" durante lo sviluppo, e di meta' della CI. Mai un ripiego
        // per-utente su disco: sposterebbe il segreto in un posto meno protetto facendo
        // credere di averlo messo al sicuro.
        string impossibile = PercorsoImpossibile();

        ProvisionedCredentials esito = CredentialProvisioning.Provision(null, impossibile, runningAsService: false);

        Assert.Equal(CredentialOrigin.Ephemeral, esito.Origin);
        Assert.False(string.IsNullOrWhiteSpace(esito.Credentials.Current));
        Assert.False(File.Exists(impossibile));
    }

    [Fact]
    public void UnDepositoDANNEGGIATONonVieneSovrascrittoInSilenzio()
    {
        // Sovrascriverlo genererebbe una chiave nuova e butterebbe via quella che i client
        // remoti stanno usando, per un guasto che potrebbe essere una modifica a mano
        // sbagliata di un minuto prima.
        File.WriteAllText(Percorso, "non e' JSON {{{");

        Assert.Throws<InvalidOperationException>(
            () => CredentialProvisioning.Provision(null, Percorso, runningAsService: false));
    }

    /// <summary>Un percorso in cui nessun utente, su nessun sistema, puo' creare una cartella.</summary>
    private string PercorsoImpossibile()
    {
        // Una cartella non puo' esistere DENTRO un file: vale su Windows come su Linux, per
        // l'amministratore come per l'utente standard. Serve un caso deterministico, non uno
        // che dipenda da chi esegue i test — su un runner di CI si e' spesso amministratori.
        string ostacolo = Path.Combine(cartella, "sono-un-file");
        File.WriteAllText(ostacolo, "x");

        return Path.Combine(ostacolo, "Observer", "credentials.json");
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}