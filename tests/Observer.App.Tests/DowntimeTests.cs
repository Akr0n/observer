using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// How the duration of a fault reads next to a machine's name.
/// </summary>
/// <remarks>
/// The wrong number here does not look like an error: it looks like information. "3 min" next
/// to a machine that has been down for three days sends you looking for a fault that has just
/// started, and that is exactly the decision this line exists to steer.
/// </remarks>
public class DowntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    public void UnderAMinuteSecondsAreNotCounted(int seconds)
    {
        // "0 min" would look like a fault of no duration at all, and seconds would be a
        // precision that a reading every ten or fifteen seconds does not have.
        Assert.Equal("under 1 min", Downtime.Describe(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void AClockGoingBackwardsDoesNotShowANegativeDuration()
    {
        // The system clock can change between one reading and the next: without this branch the
        // subtraction would give a negative duration and the text a number with a minus sign.
        Assert.Equal("under 1 min", Downtime.Describe(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void MinutesAreTruncatedNotRounded()
    {
        // The property that keeps a late text honest: the number shown is always a lower bound
        // on the real duration. With rounding, added to the lag of the reading, the row would
        // say more than it knows.
        Assert.Equal("2 min", Downtime.Describe(TimeSpan.FromSeconds(179)));
        Assert.Equal("59 min", Downtime.Describe(TimeSpan.FromSeconds(3599)));
    }

    [Fact]
    public void TheMinuteBoundaryLeavesNoGap()
    {
        // The other end of "under 1 min": at fifty-nine seconds it does not count, at sixty it does.
        Assert.Equal("under 1 min", Downtime.Describe(TimeSpan.FromSeconds(59)));
        Assert.Equal("1 min", Downtime.Describe(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AnHourOrMoreShowsTwoUnits()
    {
        Assert.Equal("1 h", Downtime.Describe(TimeSpan.FromHours(1)));
        Assert.Equal("2 h 10 min", Downtime.Describe(new TimeSpan(2, 10, 30)));
        Assert.Equal("23 h 59 min", Downtime.Describe(new TimeSpan(23, 59, 59)));
    }

    [Fact]
    public void TheDayBoundaryLeavesNoGap()
    {
        // One second before twenty-four hours the hours are still counted; one second after,
        // the days are, and the hours start again from zero without disappearing.
        Assert.Equal("23 h 59 min", Downtime.Describe(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1)));
        Assert.Equal("1 day", Downtime.Describe(TimeSpan.FromDays(1)));
        Assert.Equal("1 day 1 h", Downtime.Describe(new TimeSpan(0, 25, 30, 0)));
    }

    [Fact]
    public void PastADayTheUnitsAreDaysAndHours()
    {
        Assert.Equal("1 day", Downtime.Describe(TimeSpan.FromDays(1)));
        Assert.Equal("1 day 23 h", Downtime.Describe(new TimeSpan(1, 23, 30, 0)));
        Assert.Equal("2 days", Downtime.Describe(TimeSpan.FromDays(2)));
        Assert.Equal("2 days 3 h", Downtime.Describe(new TimeSpan(2, 3, 0, 0)));
    }

    [Fact]
    public void TheSecondUnitDisappearsWhenItIsZero()
    {
        // "2 h 0 min" and "3 days 0 h" ask the reader to take in a zero that adds nothing.
        Assert.Equal("2 h", Downtime.Describe(TimeSpan.FromHours(2)));
        Assert.Equal("3 days", Downtime.Describe(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void ASingleDayIsNotPlural() =>
        Assert.Equal("1 day", Downtime.Describe(TimeSpan.FromHours(24)));

    [Fact]
    public void TheTextNeverShowsAFractionalNumber()
    {
        // This is not a test about the culture: an integer carries no separators in any
        // culture, and the culture is guarded by CA1305, which without an explicit format does
        // not even compile. What is pinned here is that the text never contains a number with
        // a comma, that is, that the units stay whole instead of becoming "1,5 h".
        foreach (TimeSpan duration in new[]
        {
            TimeSpan.FromMinutes(90),
            TimeSpan.FromHours(36),
            TimeSpan.FromDays(400),
        })
        {
            Assert.DoesNotContain(",", Downtime.Describe(duration), StringComparison.Ordinal);
            Assert.DoesNotContain(".", Downtime.Describe(duration), StringComparison.Ordinal);
        }
    }
}
