using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>Il deposito su disco del token di macchina.</summary>
[Collection(AmbienteDelProcesso.Nome)]
public class CredentialStoreTests : IDisposable
{
    private readonly string cartella;

    public CredentialStoreTests()
    {
        cartella = Path.Combine(Path.GetTempPath(), "obs-dep-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(cartella);
    }

    private string Percorso => Path.Combine(cartella, "credentials.json");

    [Fact]
    public void UnDepositoAssenteNonEUnErrore()
    {
        // Il primo avvio e' il caso normale, non un guasto.
        Assert.Null(CredentialStore.Leggi(Percorso));
    }

    [Fact]
    public void CioCheSiScriveSiRilegge()
    {
        MachineCredentials scritte = MachineCredentials.Nuove()
            .Ruota(DateTimeOffset.UtcNow, TimeSpan.FromHours(24));

        CredentialStore.Scrivi(Percorso, scritte);

        MachineCredentials? rilette = CredentialStore.Leggi(Percorso);

        Assert.NotNull(rilette);
        Assert.Equal(scritte.Current, rilette.Current);
        Assert.Equal(scritte.Previous, rilette.Previous);
        Assert.Equal(
            scritte.PreviousExpiresAt!.Value.ToUnixTimeSeconds(),
            rilette.PreviousExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void RiscrivereNonLasciaTemporaneiInGiro()
    {
        // Un temporaneo dimenticato contiene il segreto in chiaro, e con i permessi ereditati
        // della cartella invece di quelli del deposito. Misurato: capita davvero quando la
        // sostituzione fallisce.
        for (int i = 0; i < 3; i++)
        {
            CredentialStore.Scrivi(Percorso, MachineCredentials.Nuove());
        }

        string[] rimasti = Directory.GetFiles(cartella);

        Assert.Single(rimasti);
        Assert.Equal(Percorso, rimasti[0]);
    }

    [Fact]
    public void LaRiscritturaSostituisceDavvero()
    {
        MachineCredentials prime = MachineCredentials.Nuove();
        CredentialStore.Scrivi(Percorso, prime);

        MachineCredentials seconde = MachineCredentials.Nuove();
        CredentialStore.Scrivi(Percorso, seconde);

        MachineCredentials? rilette = CredentialStore.Leggi(Percorso);

        Assert.NotNull(rilette);
        Assert.Equal(seconde.Current, rilette.Current);
        Assert.NotEqual(prime.Current, rilette.Current);
    }

    [Fact]
    public void UnDepositoIlleggibileNonDiventaSilenziosamenteUnDepositoASSENTE()
    {
        // Distinzione portante: "non c'e'" significa generane uno nuovo, "non riesco a
        // leggerlo" significa fermati. Confonderli farebbe rigenerare la chiave a ogni avvio,
        // tagliando fuori ogni client remoto senza che nessuno capisca perche'.
        File.WriteAllText(Percorso, "questo non e' JSON {{{");

        Assert.Throws<InvalidOperationException>(() => CredentialStore.Leggi(Percorso));
    }

    [Fact]
    public void IlDepositoNonContieneAltroCheLeChiaviELaScadenza()
    {
        // Il file finisce sotto gli occhi di un amministratore che indaga: deve essere ovvio
        // cosa contiene, e non deve contenere niente di piu'.
        CredentialStore.Scrivi(Percorso, MachineCredentials.Nuove().Ruota(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));

        string contenuto = File.ReadAllText(Percorso);

        Assert.Contains("current", contenuto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("previous", contenuto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", contenuto, StringComparison.OrdinalIgnoreCase);
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