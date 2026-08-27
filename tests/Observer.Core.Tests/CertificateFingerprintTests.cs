using System.Security.Cryptography;
using Observer.Core.Security;

namespace Observer.Core.Tests;

/// <summary>
/// L'impronta e' l'unica cosa che lega un collegamento a una macchina precisa.
/// </summary>
/// <remarks>
/// Il certificato di Observer e' autofirmato: nessuna autorita' lo garantisce, la catena non
/// dice niente, e la validazione ordinaria di TLS rifiuterebbe tutto. Al suo posto c'e' questo
/// confronto. Se sbaglia in senso permissivo, il collegamento cifrato protegge da chi ascolta
/// ma non da chi si mette in mezzo - che e' il caso peggiore, perche' sembra sicuro.
/// </remarks>
public class CertificateFingerprintTests
{
    private static readonly byte[] Certificato = [1, 2, 3, 4, 5];

    private static string Attesa() => CertificateFingerprint.Da(Certificato);

    [Fact]
    public void LImprontaEQuellaDiSHA256()
    {
        // Non un formato inventato: dev'essere confrontabile con cio' che stampa openssl.
        string atteso = "sha256:" + Convert.ToHexString(SHA256.HashData(Certificato));

        Assert.Equal(atteso, CertificateFingerprint.Da(Certificato));
    }

    [Fact]
    public void CertificatiDiversiDannoImpronteDiverse()
    {
        Assert.NotEqual(
            CertificateFingerprint.Da([1, 2, 3]),
            CertificateFingerprint.Da([1, 2, 4]));
    }

    [Theory]
    [InlineData("sha256:")]
    [InlineData("SHA256:")]
    [InlineData("")]
    public void IlPrefissoEFacoltativoENonDistingueMaiuscole(string prefisso)
    {
        string cifre = Convert.ToHexString(SHA256.HashData(Certificato));

        Assert.True(CertificateFingerprint.Uguali(prefisso + cifre, Attesa()));
    }

    [Fact]
    public void SeparatoriEMaiuscoleNonCambianoIlVerdetto()
    {
        // Questo valore lo copia una PERSONA da un terminale a un file di testo, e gli
        // strumenti che stampano impronte non concordano su come separarle.
        string leggibile = CertificateFingerprint.PerLUomo(Attesa());

        Assert.Contains(":", leggibile, StringComparison.Ordinal);
        Assert.True(CertificateFingerprint.Uguali(leggibile, Attesa()));
        Assert.True(CertificateFingerprint.Uguali(leggibile.ToLowerInvariant(), Attesa()));
        Assert.True(CertificateFingerprint.Uguali(leggibile.Replace(':', ' '), Attesa()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]
    [InlineData("non-e-un-impronta")]
    public void CioCheNonEUnImprontaNonSiNormalizza(string? testo)
    {
        Assert.Null(CertificateFingerprint.Normalizza(testo));
    }

    [Fact]
    public void UnImprontaTroppoCortaOTroppoLungaVieneRIFIUTATA()
    {
        string cifre = Convert.ToHexString(SHA256.HashData(Certificato));

        Assert.Null(CertificateFingerprint.Normalizza(cifre[..62]));
        Assert.Null(CertificateFingerprint.Normalizza(cifre + "AB"));
    }

    [Fact]
    public void UnCarattereNONEsadecimaleRIFIUTAtuttaLImpronta()
    {
        // "Quasi giusta" va rifiutata, non aggiustata: saltare i caratteri strani
        // accetterebbe un'impronta con dentro un errore di battitura.
        string cifre = Convert.ToHexString(SHA256.HashData(Certificato));

        Assert.Null(CertificateFingerprint.Normalizza("Z" + cifre[1..]));
        Assert.Null(CertificateFingerprint.Normalizza(cifre[..63] + "Z"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "sha256:AA")]
    [InlineData("sha256:AA", null)]
    [InlineData("", "")]
    [InlineData("guarda-che-non-e-un-impronta", "guarda-che-non-e-un-impronta")]
    public void DueValORINONVALIDINONSonoMaiUguali(string? a, string? b)
    {
        // Il caso piu' pericoloso di tutti: se due impronte illeggibili risultassero uguali,
        // un errore di battitura in ENTRAMBI i posti spegnerebbe il controllo senza dirlo.
        Assert.False(CertificateFingerprint.Uguali(a, b));
    }

    [Fact]
    public void UnImprontaDiversaDiUnSoloCarattereNonPassa()
    {
        string cifre = Convert.ToHexString(SHA256.HashData(Certificato));
        char primo = cifre[0] == 'A' ? 'B' : 'A';

        Assert.False(CertificateFingerprint.Uguali(primo + cifre[1..], Attesa()));
    }

    [Fact]
    public void LaFormaLeggibileNonRovinaCioCheNonSaLeggere()
    {
        Assert.Equal("non lo so", CertificateFingerprint.PerLUomo("non lo so"));
    }
}