using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// La scelta automatica della risoluzione. Sbagliarla non produce dati falsi, produce una
/// richiesta che non torna piu': un mese a un punto al secondo sono due milioni e mezzo di
/// punti da costruire in memoria per una singola risposta HTTP.
/// </summary>
public class HistoryResolutionTests
{
    private const int LimiteDiPunti = 5000;

    private static DateTimeOffset T(string istanteIso) =>
        DateTimeOffset.Parse(istanteIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public void Sceglie_IlGrezzoPerUnaFinestraCorta()
    {
        int risoluzione = HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:05:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.RawSeconds, risoluzione);
    }

    [Fact]
    public void Sceglie_IMinutiSeIlGrezzoEGiaStatoCancellato()
    {
        // La finestra e' corta e il grezzo ci starebbe: ma il grezzo di ieri non esiste
        // piu'. Restituirlo comunque darebbe un grafico vuoto invece di uno aggregato.
        int risoluzione = HistoryResolution.Choose(
            T("2026-08-25T12:00:00Z"),
            T("2026-08-25T12:05:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.MinuteSeconds, risoluzione);
    }

    [Fact]
    public void Sceglie_IMinutiQuandoIlGrezzoSforerebbeIlLimite()
    {
        // Due ore sono 7200 secondi: oltre il limite di 5000 punti. A un minuto sono 120.
        int risoluzione = HistoryResolution.Choose(
            T("2026-08-26T10:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.MinuteSeconds, risoluzione);
    }

    [Fact]
    public void Sceglie_ICinqueMinutiPerUnaSettimana()
    {
        // Sette giorni sono 10080 minuti, oltre il limite; a cinque minuti sono 2016.
        int risoluzione = HistoryResolution.Choose(
            T("2026-08-19T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.FiveMinuteSeconds, risoluzione);
    }

    [Fact]
    public void Sceglie_ICinqueMinutiAncheQuandoNemmenoLoroBastano()
    {
        // Un anno a cinque minuti sono piu' di centomila punti: sfora comunque. Non esiste
        // un livello piu' grosso, quindi si restituisce il piu' grosso che c'e' e il limite
        // di righe della query fa il resto. Meglio un grafico troncato di un errore.
        int risoluzione = HistoryResolution.Choose(
            T("2025-08-26T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.FiveMinuteSeconds, risoluzione);
    }

    [Fact]
    public void Sceglie_RifiutaUnaFinestraVuotaOrovesciata()
    {
        Assert.Throws<ArgumentException>(() => HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            LimiteDiPunti,
            rawAvailableFrom: T("2026-08-26T06:00:00Z")));
    }

    [Fact]
    public void Sceglie_RifiutaUnLimiteDiPuntiNonPositivo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T13:00:00Z"),
            0,
            rawAvailableFrom: T("2026-08-26T06:00:00Z")));
    }
}
