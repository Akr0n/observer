using System.Globalization;
using Observer.Service.Persistence;

namespace Observer.Service.Tests;

/// <summary>
/// The automatic choice of resolution. Getting it wrong does not produce false data, it produces
/// a request that never comes back: a month at one point per second is two and a half million
/// points to build in memory for a single HTTP response.
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
        // The window is short and the raw data would fit: but yesterday's raw data is gone.
        // Returning it anyway would give an empty chart instead of an aggregated one.
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
        // Two hours are 7200 seconds: past the 5000-point limit. At one minute they are 120.
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
        // Seven days are 10080 minutes, past the limit; at five minutes they are 2016.
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
        // A year at five minutes is more than a hundred thousand points: it goes past the limit
        // anyway. There is no coarser level, so the coarsest one there is gets returned and the
        // query's row limit does the rest. A truncated chart is better than an error.
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
