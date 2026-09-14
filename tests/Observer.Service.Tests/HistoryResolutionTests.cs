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
    private const int PointLimit = 5000;

    private static DateTimeOffset T(string isoInstant) =>
        DateTimeOffset.Parse(isoInstant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public void Choose_PicksRawForAShortWindow()
    {
        int resolution = HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:05:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.RawSeconds, resolution);
    }

    [Fact]
    public void Choose_PicksMinutesWhenTheRawHasAlreadyBeenPurged()
    {
        // La finestra e' corta e il grezzo ci starebbe: ma il grezzo di ieri non esiste
        // piu'. Restituirlo comunque darebbe un grafico vuoto invece di uno aggregato.
        int resolution = HistoryResolution.Choose(
            T("2026-08-25T12:00:00Z"),
            T("2026-08-25T12:05:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.MinuteSeconds, resolution);
    }

    [Fact]
    public void Choose_PicksMinutesWhenRawWouldExceedTheLimit()
    {
        // Due ore sono 7200 secondi: oltre il limite di 5000 punti. A un minuto sono 120.
        int resolution = HistoryResolution.Choose(
            T("2026-08-26T10:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.MinuteSeconds, resolution);
    }

    [Fact]
    public void Choose_PicksFiveMinutesForAWeek()
    {
        // Sette giorni sono 10080 minuti, oltre il limite; a cinque minuti sono 2016.
        int resolution = HistoryResolution.Choose(
            T("2026-08-19T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.FiveMinuteSeconds, resolution);
    }

    [Fact]
    public void Choose_PicksFiveMinutesEvenWhenTheyStillExceedTheLimit()
    {
        // Un anno a cinque minuti sono piu' di centomila punti: sfora comunque. Non esiste
        // un livello piu' grosso, quindi si restituisce il piu' grosso che c'e' e il limite
        // di righe della query fa il resto. Meglio un grafico troncato di un errore.
        int resolution = HistoryResolution.Choose(
            T("2025-08-26T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z"));

        Assert.Equal(BucketWidths.FiveMinuteSeconds, resolution);
    }

    [Fact]
    public void Choose_RejectsAnEmptyOrBackwardsWindow()
    {
        Assert.Throws<ArgumentException>(() => HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T12:00:00Z"),
            PointLimit,
            rawAvailableFrom: T("2026-08-26T06:00:00Z")));
    }

    [Fact]
    public void Choose_RejectsANonPositivePointLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryResolution.Choose(
            T("2026-08-26T12:00:00Z"),
            T("2026-08-26T13:00:00Z"),
            0,
            rawAvailableFrom: T("2026-08-26T06:00:00Z")));
    }
}
