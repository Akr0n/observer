using System.Globalization;
using Observer.Core.Platform;
using Observer.Core.Security;

namespace Observer.Core.Tests;

/// <summary>
/// Il deposito dei token delle macchine remote.
/// </summary>
/// <remarks>
/// Esiste perche' quei token stavano in chiaro dentro <c>machines.json</c>, un file scritto a
/// mano e fatto per essere guardato. Da quando lo stesso token autorizza anche a terminare
/// processi su un'altra macchina, quel file vale molto piu' di prima.
/// </remarks>
public class SecretStoreTests
{
    [Theory]
    [InlineData("lavoro")]
    [InlineData("PC di Federico")]
    [InlineData("nas-01.locale")]
    public void UnNomeNormaleVaBene(string nome) => Assert.Equal(nome, SecretName.Valida(nome));

    [Theory]
    [InlineData("../../id_rsa")]
    [InlineData("..\\altrove")]
    [InlineData("/etc/shadow")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("con*asterisco")]
    public void UnNomeCheProvaAUscireDallaCartellaVieneRifiutato(string nome)
    {
        // Il nome arriva da machines.json, che lo scrive una persona, e finisce a comporre un
        // percorso di file: senza questo controllo una voce chiamata "../../id_rsa" farebbe
        // leggere - e riscrivere - un file fuori dalla cartella dei segreti.
        Assert.Throws<SecretStoreException>(() => SecretName.Valida(nome));
    }

    [Fact]
    public void GliSpaziAiBordiNonFannoDueSegretiDiversi() =>
        Assert.Equal("lavoro", SecretName.Valida("  lavoro  "));

    [Fact]
    public void SuUnaPiattaformaSconosciutaIlDepositoLoDice()
    {
        // Non un deposito vuoto: un deposito vuoto farebbe concludere di essersi dimenticati
        // di depositare il token, e manderebbe a cercare il problema dalla parte sbagliata.
        ISecretStore deposito = SecretStores.Per(HostPlatform.Unknown);

        Assert.Throws<SecretStoreException>(() => deposito.TryRead("lavoro", out _));
    }

    [SoloSuWindows]
    public void SuWindowsIlSegretoVaETornaDalCredentialManager()
    {
        // L'unica cosa che a tavolino non si puo' sapere: che advapi32 accetti la struct come
        // l'abbiamo dichiarata e restituisca gli stessi byte. Il nome porta un guid, cosi'
        // questa prova non puo' toccare una credenziale vera.
        string nome = "observer-prova-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        const string Segreto = "un-token-che-non-serve-a-niente";

        ISecretStore deposito = SecretStores.Per(HostPlatform.Windows);

        Assert.False(deposito.TryRead(nome, out _), "il deposito conteneva gia' un nome col guid");

        try
        {
            deposito.Write(nome, Segreto);

            Assert.True(deposito.TryRead(nome, out string letto));
            Assert.Equal(Segreto, letto);
        }
        finally
        {
            deposito.Delete(nome);
        }

        Assert.False(deposito.TryRead(nome, out _), "il segreto e' rimasto dopo la cancellazione");
    }

    [SoloSuWindows]
    public void SuWindowsCancellareUnSegretoAssenteDiceFalsoInveceDiLanciare()
    {
        string nome = "observer-mai-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        Assert.False(SecretStores.Per(HostPlatform.Windows).Delete(nome));
    }
}
