using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Come si legge la durata di un guasto accanto al nome di una macchina.
/// </summary>
/// <remarks>
/// Il numero sbagliato qui non ha l'aria di un errore: ha l'aria di un'informazione. "3 min"
/// accanto a una macchina caduta da tre giorni manda a cercare un guasto appena nato, ed e'
/// esattamente la decisione che questa riga esiste per orientare.
/// </remarks>
public class DowntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    public void SottoUnMinutoNonSiContanoISecondi(int secondi)
    {
        // "0 min" sembrerebbe un guasto di durata nulla, e i secondi sarebbero una precisione
        // che una lettura ogni dieci o quindici secondi non ha.
        Assert.Equal("under 1 min", Downtime.Frase(TimeSpan.FromSeconds(secondi)));
    }

    [Fact]
    public void UnOrologioCheTornaIndietroNonProduceUnNumeroStrano()
    {
        // L'ora di sistema puo' cambiare fra una lettura e l'altra: senza questo ramo la
        // sottrazione darebbe una durata negativa e la frase un numero col segno meno.
        Assert.Equal("under 1 min", Downtime.Frase(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void IMinutiSiTronconoENonSiArrotondano()
    {
        // La proprieta' che rende onesto un testo in ritardo: il numero mostrato e' sempre un
        // limite inferiore della durata vera. Arrotondando, sommato al ritardo della lettura,
        // la riga direbbe piu' di quanto sa.
        Assert.Equal("2 min", Downtime.Frase(TimeSpan.FromSeconds(179)));
        Assert.Equal("59 min", Downtime.Frase(TimeSpan.FromSeconds(3599)));
    }

    [Fact]
    public void IlConfineDelMinutoNonLasciaBuchi()
    {
        // L'altro capo di "under 1 min": a cinquantanove secondi non si conta, a sessanta si.
        Assert.Equal("under 1 min", Downtime.Frase(TimeSpan.FromSeconds(59)));
        Assert.Equal("1 min", Downtime.Frase(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void DallOraInSuSiLeggonoDueUnita()
    {
        Assert.Equal("1 h", Downtime.Frase(TimeSpan.FromHours(1)));
        Assert.Equal("2 h 10 min", Downtime.Frase(new TimeSpan(2, 10, 30)));
        Assert.Equal("23 h 59 min", Downtime.Frase(new TimeSpan(23, 59, 59)));
    }

    [Fact]
    public void IlConfineDelGiornoNonLasciaBuchi()
    {
        // Un secondo prima delle ventiquattro ore si contano ancora le ore; un secondo dopo
        // si contano i giorni, e le ore ripartono da zero senza sparire.
        Assert.Equal("23 h 59 min", Downtime.Frase(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1)));
        Assert.Equal("1 day", Downtime.Frase(TimeSpan.FromDays(1)));
        Assert.Equal("1 day 1 h", Downtime.Frase(new TimeSpan(0, 25, 30, 0)));
    }

    [Fact]
    public void OltreIlGiornoSiContanoIGiorniEPoiLeOre()
    {
        Assert.Equal("1 day", Downtime.Frase(TimeSpan.FromDays(1)));
        Assert.Equal("1 day 23 h", Downtime.Frase(new TimeSpan(1, 23, 30, 0)));
        Assert.Equal("2 days", Downtime.Frase(TimeSpan.FromDays(2)));
        Assert.Equal("2 days 3 h", Downtime.Frase(new TimeSpan(2, 3, 0, 0)));
    }

    [Fact]
    public void LaSecondaUnitaSparisceQuandoEZero()
    {
        // "2 h 0 min" e "3 days 0 h" chiedono di leggere uno zero che non aggiunge niente.
        Assert.Equal("2 h", Downtime.Frase(TimeSpan.FromHours(2)));
        Assert.Equal("3 days", Downtime.Frase(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void UnSoloGiornoNonEPlurale() =>
        Assert.Equal("1 day", Downtime.Frase(TimeSpan.FromHours(24)));

    [Fact]
    public void LaFraseNonPortaMaiUnNumeroFrazionario()
    {
        // Non e' una prova sulla cultura: un intero non porta separatori in nessuna cultura,
        // e a proteggere la cultura c'e' CA1305, che senza il formato esplicito non fa
        // nemmeno compilare. Qui si pinna che la frase non contenga mai un numero con la
        // virgola, cioe' che le unita' restino intere invece di diventare "1,5 h".
        foreach (TimeSpan durata in new[]
        {
            TimeSpan.FromMinutes(90),
            TimeSpan.FromHours(36),
            TimeSpan.FromDays(400),
        })
        {
            Assert.DoesNotContain(",", Downtime.Frase(durata), StringComparison.Ordinal);
            Assert.DoesNotContain(".", Downtime.Frase(durata), StringComparison.Ordinal);
        }
    }
}
