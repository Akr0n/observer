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
            Assert.True(visti.Add(TokenGenerator.Genera()), "token ripetuto");
        }
    }

    [Fact]
    public void UnTokenNonContieneCaratteriDaCodificareInUnHeader()
    {
        // Finisce dentro "Authorization: Bearer ...". Base64 normale userebbe + / =, che in un
        // header vanno codificati e che chiunque copi-incolli sbaglierebbe.
        string token = TokenGenerator.Genera();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain(' ', token);
        Assert.True(token.Length >= 40, "token troppo corto: " + token.Length);
    }

    [Fact]
    public void LaChiaveCorrenteEAccettata()
    {
        MachineCredentials credenziali = MachineCredentials.Nuove();

        Assert.True(credenziali.Accetta(credenziali.Current, Adesso));
    }

    [Fact]
    public void UnaChiaveSbagliataERifiutata()
    {
        MachineCredentials credenziali = MachineCredentials.Nuove();

        Assert.False(credenziali.Accetta("non-e-il-token", Adesso));
        Assert.False(credenziali.Accetta(string.Empty, Adesso));
    }

    [Fact]
    public void DopoLaRotazioneVALGONOENTRAMBE_FinoAllaScadenza()
    {
        // Senza questa finestra, ruotare taglierebbe fuori ogni client remoto all'ISTANTE, e la
        // rotazione diventerebbe un'operazione che nessuno osa fare.
        MachineCredentials prima = MachineCredentials.Nuove();
        string vecchia = prima.Current;

        MachineCredentials dopo = prima.Ruota(Adesso, MachineCredentials.FinestraDiGrazia);

        Assert.NotEqual(vecchia, dopo.Current);
        Assert.True(dopo.Accetta(dopo.Current, Adesso));
        Assert.True(dopo.Accetta(vecchia, Adesso));
    }

    [Fact]
    public void LaChiavePrecedenteSmetteDiValereAllaScadenza()
    {
        MachineCredentials prima = MachineCredentials.Nuove();
        string vecchia = prima.Current;

        MachineCredentials dopo = prima.Ruota(Adesso, TimeSpan.FromHours(24));

        Assert.True(dopo.Accetta(vecchia, Adesso.AddHours(23)));
        Assert.False(dopo.Accetta(vecchia, Adesso.AddHours(25)));

        // La corrente non scade con lei.
        Assert.True(dopo.Accetta(dopo.Current, Adesso.AddHours(25)));
    }

    [Fact]
    public void DueRotazioniDiFilaDimenticanoLaPiuVecchia()
    {
        // Si conserva UNA sola chiave precedente. Tenerne una catena significherebbe che una
        // chiave compromessa resta valida finche' qualcuno non ruota abbastanza volte.
        MachineCredentials prima = MachineCredentials.Nuove();
        string primissima = prima.Current;

        MachineCredentials dopo = prima
            .Ruota(Adesso, TimeSpan.FromHours(24))
            .Ruota(Adesso, TimeSpan.FromHours(24));

        Assert.False(dopo.Accetta(primissima, Adesso));
    }

    [Fact]
    public void SenzaChiavePrecedenteNonSiAccettaNulla_NemmenoUnaStringaVuota()
    {
        // Il caso in cui Previous e' null non deve degenerare in "accetta tutto": e' il ramo
        // che un confronto scritto male trasforma in un passaggio libero.
        MachineCredentials credenziali = MachineCredentials.Nuove();

        Assert.Null(credenziali.Previous);
        Assert.False(credenziali.Accetta(string.Empty, Adesso));
    }
}