using Observer.Cli;
using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>
/// Le frasi che <c>doctor</c> mostra a chi legge lo schermo.
/// </summary>
/// <remarks>
/// Sono codice a tutti gli effetti: dicono all'utente se il token della sua macchina e' al
/// sicuro e cosa fare se non lo e'. Una frase mancante o ambigua vale un difetto.
/// </remarks>
public class DiagnosiTests
{
    [Fact]
    public void OgniVerdettoHaLaSuaFrase_ENessunaEUgualeAUnAltra()
    {
        List<string> frasi = [];

        foreach (DirectoryVerdict verdetto in Enum.GetValues<DirectoryVerdict>())
        {
            string frase = Diagnosi.Frase(verdetto);

            Assert.False(string.IsNullOrWhiteSpace(frase), verdetto.ToString());
            frasi.Add(frase);
        }

        Assert.Equal(frasi.Count, frasi.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SoloIlVerdettoSicuroDiceCheEProtetto()
    {
        // "PROTECTED" e' la parola su cui un amministratore smette di leggere: non deve
        // comparire in nessuno degli altri casi, e in particolare non nel FAKE PROTECTED.
        foreach (DirectoryVerdict verdetto in Enum.GetValues<DirectoryVerdict>())
        {
            bool diceProtetto = Diagnosi.Frase(verdetto).StartsWith("PROTECTED", StringComparison.Ordinal);

            Assert.Equal(verdetto == DirectoryVerdict.Sicura, diceProtetto);
        }
    }

    [Fact]
    public void IlFintoProtettoSpiegaPercheNonLoE()
    {
        // E' il verdetto che nessuno scriverebbe senza averlo misurato: la DACL sembra giusta,
        // ma il proprietario se la riscrive quando vuole. Se la frase non lo spiega, chi legge
        // conclude che sia un falso allarme.
        string frase = Diagnosi.Frase(DirectoryVerdict.ProprietarioNonFidato);

        Assert.Contains("OWNER", frase, StringComparison.Ordinal);
        Assert.Contains("looks safe", frase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IVerdettiPericolosiDiconoCheIlTokenValeDallaRETE()
    {
        // Senza questa riga, "altri utenti possono leggerlo" suona come un problema di
        // riservatezza locale, e non come un accesso permanente da un altro computer.
        Assert.Contains("NETWORK", Diagnosi.Frase(DirectoryVerdict.DaclAperta), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help", 0)]
    [InlineData("--help", 0)]
    [InlineData("verbo-che-non-esiste", 2)]
    public void IVerbiSconosciutiEsconoConUnCodiceDiversoDaZero(string verbo, int atteso)
    {
        Assert.Equal(atteso, Comandi.Esegui([verbo]));
    }

    [Fact]
    public void SenzaArgomentiSiMostraLAiuto()
    {
        Assert.Equal(0, Comandi.Esegui([]));
    }
}