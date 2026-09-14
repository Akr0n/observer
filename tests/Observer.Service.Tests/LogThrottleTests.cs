using Observer.Service;

namespace Observer.Service.Tests;

/// <summary>
/// The throttle that keeps a repeated fault out of the event log.
/// </summary>
/// <remarks>
/// There are three rules and they pull in opposite directions: do not repeat what has already
/// been said, do not keep quiet about what has changed, and do not let anyone believe a fault
/// is still going on when it is over. Get the first wrong and the Windows event log fills up in
/// a few hours; get the second wrong and the new fault hides behind the old one; get the third
/// wrong and someone goes hunting for a fault that is no longer there.
/// </remarks>
public class LogThrottleTests
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    [Fact]
    public void TheFirstFaultIsLogged()
    {
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void TheSameReasonIsNotLoggedAgain()
    {
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void ADifferentReasonIsLoggedImmediately()
    {
        // The case that makes the throttle dangerous if it gets this wrong: the disk frees up
        // and a fault of a different nature begins. Keeping quiet about it because "we are
        // already reporting something" would leave the log telling the wrong fault.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        Assert.True(throttle.ShouldLog("file-agganciato"));
    }

    [Fact]
    public void AFaultThatLastsIsLoggedAgainAfterTheWindow()
    {
        // Without this, a permanent fault leaves ONE line and then silence: whoever reads the
        // log an hour later cannot tell whether it is still going on. And the numbers inside
        // the message would stay those of the first round.
        LogThrottle throttle = Create(out FakeClock clock);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        clock.Advance(RepeatWindow - TimeSpan.FromSeconds(1));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void AFlickeringFaultDoesNotLogOnEveryRecovery()
    {
        // This is why this class is not content to compare the reason. A collector wavering
        // around its deadline alternates fault and success at every round: if every return
        // were "a new reason", half the flood would be left.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("scaduto"));
        Assert.True(throttle.ShouldLogRecovery(out _));

        for (int round = 0; round < 100; round++)
        {
            Assert.False(throttle.ShouldLog("scaduto"));
            Assert.False(throttle.ShouldLogRecovery(out _));
        }
    }

    [Fact]
    public void TheRecoveryIsLoggedEvenIfTheFaultLastedASingleRound()
    {
        // It is the most common case, and it is precisely the one in which a single line
        // would let you believe a fault is still open.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));

        Assert.True(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(0, silenced);
    }

    [Fact]
    public void TheRecoverySaysHowManyLinesWereSilenced()
    {
        LogThrottle throttle = Create(out _);

        throttle.ShouldLog("disco-pieno");
        throttle.ShouldLog("disco-pieno");
        throttle.ShouldLog("disco-pieno");

        Assert.True(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(2, silenced);
    }

    [Fact]
    public void WithNoFaultThereIsNoRecoveryToAnnounce()
    {
        // Otherwise every healthy round would write "everything is back to normal", which is
        // the same flood as before in happier words.
        LogThrottle throttle = Create(out _);

        Assert.False(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(0, silenced);
    }

    [Fact]
    public void AFaultThatWasNeverLoggedHasNoRecoveryToAnnounce()
    {
        // The end of a story the log never began does not get told: it is the other half of
        // what keeps a flickering fault silent.
        LogThrottle throttle = Create(out _);

        throttle.ShouldLog("scaduto");
        throttle.ShouldLogRecovery(out _);

        Assert.False(throttle.ShouldLog("scaduto"));
        Assert.False(throttle.ShouldLogRecovery(out _));
    }

    [Fact]
    public void AfterTheWindowARecurringFaultIsLoggedOnceMore()
    {
        // The silence around an intermittent fault is not forever: once the window has
        // passed, a fault that still comes and goes shows up once more.
        LogThrottle throttle = Create(out FakeClock clock);

        throttle.ShouldLog("scaduto");
        throttle.ShouldLogRecovery(out _);
        Assert.False(throttle.ShouldLog("scaduto"));

        clock.Advance(RepeatWindow);

        Assert.True(throttle.ShouldLog("scaduto"));
    }

    [Fact]
    public void ANullReasonIsNotAReason()
    {
        LogThrottle throttle = Create(out _);

        Assert.Throws<ArgumentNullException>(() => throttle.ShouldLog(null!));
    }

    [Fact]
    public void TheDefaultWindowIsNotZero()
    {
        // Zero would make the throttle a piece of code that throttles nothing, and none of
        // the other tests would notice: in those the clock never advances on its own.
        Assert.True(LogThrottle.RepeatInterval >= TimeSpan.FromMinutes(1));
    }

    private static LogThrottle Create(out FakeClock clock)
    {
        clock = new FakeClock();

        return new LogThrottle(clock, RepeatWindow);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now;

        public void Advance(TimeSpan amount) => now += amount.Ticks;
    }
}
