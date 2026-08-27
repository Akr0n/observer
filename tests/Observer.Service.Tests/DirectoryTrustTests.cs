using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>
/// Se ci si puo' fidare della cartella che ospitera' il token di macchina.
/// </summary>
/// <remarks>
/// Funzione PURA sui fatti osservati, per due motivi. Il primo e' che i casi che contano non si
/// possono costruire tutti su una macchina qualsiasi: una cartella posseduta da SYSTEM richiede
/// una sessione amministrativa. Il secondo e' che questa e' la decisione di sicurezza portante
/// del deposito, e va verificata a tabella e non per campione.
/// </remarks>
public class DirectoryTrustTests
{
    private const string Sistema = "S-1-5-18";
    private const string Amministratori = "S-1-5-32-544";
    private const string Utente = "S-1-5-21-1-2-3-1001";
    private const string Tutti = "S-1-1-0";
    private const string UtentiIntegrati = "S-1-5-32-545";

    [Fact]
    public void UnaCartellaAssenteSiPuoCreare()
    {
        Assert.Equal(DirectoryVerdict.Assente, DirectoryTrust.Valuta(Fatti(esiste: false)));
    }

    [Fact]
    public void UnPuntoDiReparseVinceSuTUTTOilResto()
    {
        // Una giunzione la crea un utente standard SENZA privilegi. Se il controllo non venisse
        // per primo, si correggerebbero proprietario e ACL della cartella dell'ATTACCANTE, e ci
        // si depositerebbe dentro il token. Qui i fatti sono per il resto perfetti, apposta.
        DirectoryFacts perfettaMaGiunzione = Fatti(
            puntoDiReparse: true,
            proprietario: Sistema,
            daclProtetta: true,
            sid: [Sistema, Amministratori]);

        Assert.Equal(DirectoryVerdict.PuntoDiReparse, DirectoryTrust.Valuta(perfettaMaGiunzione));
    }

    [Fact]
    public void UnDescrittoreIlleggibileENonSicuro_NonSconosciutoEBasta()
    {
        Assert.Equal(
            DirectoryVerdict.Sconosciuto,
            DirectoryTrust.Valuta(Fatti(descrittoreLeggibile: false)));
    }

    [Fact]
    public void UnaDaclPerfettaConProprietarioUTENTE_ENONsicura()
    {
        // E' il "finto protetto": la DACL non nomina l'utente in alcun modo, ma il proprietario
        // ha WRITE_DAC implicito e se la riscrive quando vuole. Misurato: una sola chiamata e
        // l'accesso torna completo. Chi guarda solo le ACE dice "sicura" e sbaglia.
        DirectoryFacts fintoProtetto = Fatti(
            proprietario: Utente,
            daclProtetta: true,
            sid: [Sistema, Amministratori]);

        Assert.Equal(DirectoryVerdict.ProprietarioNonFidato, DirectoryTrust.Valuta(fintoProtetto));
    }

    [Theory]
    [InlineData(Sistema)]
    [InlineData(Amministratori)]
    public void IDueSoliProprietariAmmessi(string proprietario)
    {
        Assert.Equal(
            DirectoryVerdict.Sicura,
            DirectoryTrust.Valuta(Fatti(proprietario: proprietario, daclProtetta: true, sid: [Sistema, Amministratori])));
    }

    [Fact]
    public void UnaDaclNonProtettaENONsicura_AncheSeLeAceSonoGiuste()
    {
        // Non protetta significa che eredita: e la cartella di sistema che ospita il deposito
        // concede a BUILTIN\Users la lettura ereditabile. Ereditare basta a perdere il segreto,
        // senza bisogno di alcun attaccante.
        Assert.Equal(
            DirectoryVerdict.DaclAperta,
            DirectoryTrust.Valuta(Fatti(proprietario: Sistema, daclProtetta: false, sid: [Sistema, Amministratori])));
    }

    [Theory]
    [InlineData(Tutti)]
    [InlineData(UtentiIntegrati)]
    [InlineData(Utente)]
    public void UnaSolaAceDiTroppoBastaARenderlaNonSicura(string intruso)
    {
        Assert.Equal(
            DirectoryVerdict.DaclAperta,
            DirectoryTrust.Valuta(Fatti(proprietario: Sistema, daclProtetta: true, sid: [Sistema, Amministratori, intruso])));
    }

    [Fact]
    public void IlValoreZeroDelVerdettoNonEQuelloCheAutorizza()
    {
        // Un campo dimenticato o una struct non inizializzata non devono produrre "Sicura".
        Assert.Equal(DirectoryVerdict.Sconosciuto, default(DirectoryVerdict));
        Assert.NotEqual(DirectoryVerdict.Sicura, default(DirectoryVerdict));
    }

    [Fact]
    public void SoloSicuraEAssenteSonoEsitiUtilizzabili()
    {
        // Chiunque usi il verdetto deve poter distinguere "vai avanti" da "fermati", senza
        // dover elencare a mano i casi negativi e senza dimenticarne uno.
        Assert.True(DirectoryVerdict.Sicura.PuoOspitareUnSegreto());
        Assert.False(DirectoryVerdict.Assente.PuoOspitareUnSegreto());

        foreach (DirectoryVerdict verdetto in Enum.GetValues<DirectoryVerdict>())
        {
            if (verdetto != DirectoryVerdict.Sicura)
            {
                Assert.False(verdetto.PuoOspitareUnSegreto(), verdetto.ToString());
            }
        }
    }

    private static DirectoryFacts Fatti(
        bool esiste = true,
        bool puntoDiReparse = false,
        bool descrittoreLeggibile = true,
        string? proprietario = Sistema,
        bool daclProtetta = true,
        IReadOnlyList<string>? sid = null) =>
        new(esiste, puntoDiReparse, descrittoreLeggibile, proprietario, daclProtetta, sid ?? [Sistema, Amministratori]);
}