using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Il token di macchina, la sua rotazione e la finestra in cui la chiave precedente vale ancora.
/// </summary>
public class MachineCredentialsTests
{
    private static readonly DateTimeOffset Adesso =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnTokenGeneratoENuovoOgniVolta()
    {
        // Due chiamate non devono mai coincidere: un generatore che ripete si nota solo il
        // giorno in cui due macchine hanno la stessa chiave.
        HashSet<string> visti = [];

        for (int i = 0; i < 200; i++)
        {
            Assert.True(visti.Add(TokenGenerator.Generate()), "token ripetuto");
        }
    }

    [Fact]
    public void UnTokenNonContieneCaratteriDaCodificareInUnHeader()
    {
        // Finisce dentro "Authorization: Bearer ...". Base64 normale userebbe + / =, che in un
        // header vanno codificati e che chiunque copi-incolli sbaglierebbe.
        string token = TokenGenerator.Generate();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain(' ', token);
        Assert.True(token.Length >= 40, "token troppo corto: " + token.Length);
    }

    [Fact]
    public void LaChiaveCorrenteEAccettata()
    {
        MachineCredentials credenziali = MachineCredentials.Create();

        Assert.True(credenziali.Accepts(credenziali.Current, Adesso));
    }

    [Fact]
    public void UnaChiaveSbagliataERifiutata()
    {
        MachineCredentials credenziali = MachineCredentials.Create();

        Assert.False(credenziali.Accepts("non-e-il-token", Adesso));
        Assert.False(credenziali.Accepts(string.Empty, Adesso));
    }

    [Fact]
    public void DopoLaRotazioneVALGONOENTRAMBE_FinoAllaScadenza()
    {
        // Senza questa finestra, ruotare taglierebbe fuori ogni client remoto all'ISTANTE, e la
        // rotazione diventerebbe un'operazione che nessuno osa fare.
        MachineCredentials prima = MachineCredentials.Create();
        string vecchia = prima.Current;

        MachineCredentials dopo = prima.Rotate(Adesso, MachineCredentials.GracePeriod);

        Assert.NotEqual(vecchia, dopo.Current);
        Assert.True(dopo.Accepts(dopo.Current, Adesso));
        Assert.True(dopo.Accepts(vecchia, Adesso));
    }

    [Fact]
    public void LaChiavePrecedenteSmetteDiValereAllaScadenza()
    {
        MachineCredentials prima = MachineCredentials.Create();
        string vecchia = prima.Current;

        MachineCredentials dopo = prima.Rotate(Adesso, TimeSpan.FromHours(24));

        Assert.True(dopo.Accepts(vecchia, Adesso.AddHours(23)));
        Assert.False(dopo.Accepts(vecchia, Adesso.AddHours(25)));

        // La corrente non scade con lei.
        Assert.True(dopo.Accepts(dopo.Current, Adesso.AddHours(25)));
    }

    [Fact]
    public void DueRotazioniDiFilaDimenticanoLaPiuVecchia()
    {
        // Si conserva UNA sola chiave precedente. Tenerne una catena significherebbe che una
        // chiave compromessa resta valida finche' qualcuno non ruota abbastanza volte.
        MachineCredentials prima = MachineCredentials.Create();
        string primissima = prima.Current;

        MachineCredentials dopo = prima
            .Rotate(Adesso, TimeSpan.FromHours(24))
            .Rotate(Adesso, TimeSpan.FromHours(24));

        Assert.False(dopo.Accepts(primissima, Adesso));
    }

    [Fact]
    public void SenzaChiavePrecedenteNonSiAccettaNulla_NemmenoUnaStringaVuota()
    {
        // Il caso in cui Previous e' null non deve degenerare in "accetta tutto": e' il ramo
        // che un confronto scritto male trasforma in un passaggio libero.
        MachineCredentials credenziali = MachineCredentials.Create();

        Assert.Null(credenziali.Previous);
        Assert.False(credenziali.Accepts(string.Empty, Adesso));
    }
}