using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// When a fault turns red, and when it is still normal.
/// </summary>
/// <remarks>
/// The defect this class closes: on a freshly installed machine the window used to open with a
/// RED bar — "Service unreachable" — because the first attempt landed while the service was
/// still starting. That is, the program's first second of life showed an error, and the error
/// cleared itself a moment later. An alarm that switches itself off teaches you to ignore the
/// real ones too.
/// <para>
/// The rule is not "never raise an alarm": it is that the severity depends on HOW LONG the
/// fault has lasted, not on the single attempt that went wrong. A service unreachable for one
/// second is a service that is starting; for half a minute it is a service that is not there.
/// </para>
/// </remarks>
public class StatusEscalationTests
{
    private static readonly ObserverEndpoint Local = ObserverEndpoint.LocalChannel();

    private static readonly ObserverEndpoint Remote =
        ObserverEndpoint.Remote(new Uri("http://other:5057/"), "t", "from the test");

    private static StatusMessage MessageFor(
        ServiceOutcome outcome,
        TimeSpan failingFor,
        ObserverEndpoint endpoint,
        bool hasValuesOnScreen = false,
        bool hasMeasured = false) =>
        StatusEscalation.MessageFor(
            outcome, "technical detail from the test", failingFor, endpoint, hasValuesOnScreen, hasMeasured);

    [Fact]
    public void TheFirstFailedAttempt_IsNotAnError()
    {
        // The measured case: the window opens while the service is still starting.
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);

        Assert.Equal(StatusTone.Informational, message.Tone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void AServiceUnreachableForAShortTimeIsAServiceThatIsStarting(int seconds)
    {
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromSeconds(seconds), Local);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Equal("Connecting", message.Title);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(3600)]
    public void AServiceUnreachableForAWhileIsAFault(int seconds)
    {
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromSeconds(seconds), Local);

        Assert.Equal(StatusTone.Error, message.Tone);
        Assert.Equal("Service unreachable", message.Title);
    }

    [Fact]
    public void OnceTheGraceHasExpired_TheTechnicalDetailComesBack()
    {
        // While waiting the detail is kept quiet because it is noise. Once the fault is real
        // the detail is needed, and it is the only thing there is to diagnose with.
        StatusMessage waiting = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);
        StatusMessage fault = MessageFor(ServiceOutcome.Unreachable, StatusEscalation.GracePeriod, Local);

        Assert.DoesNotContain("technical detail", waiting.Text, StringComparison.Ordinal);
        Assert.Contains("technical detail", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnARemoteMachine_TheWaitNamesThatMachineAndNotThisOne()
    {
        // There is no way to know whether somebody else's machine is starting: that is a claim
        // that cannot be made. It says what is being done — contacting it — and nothing more.
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Contains("other:5057", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("this machine", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatListensButHasNotSampledYet_IsNormalAtFirst()
    {
        StatusMessage message = MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.Zero, Local);

        Assert.Equal(StatusTone.Informational, message.Tone);

        // The title and the text too, and not the tone alone. A machine that has never answered
        // and one that has stopped answering produce the same tone here, so a test that checked
        // only the colour agreed with an arm that had lost its guard and swallowed both.
        Assert.Equal("Service is starting", message.Title);
        Assert.Contains("technical detail from the test", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatListensButNeverSamples_BecomesAWarning()
    {
        // The silent twin of the red bar, and just as wrong: a live service that produces no
        // sample used to stay "Service is starting" FOR EVER, with a text promising "this
        // usually clears itself in a second or two". If it does not clear, that sentence is a
        // lie nobody ever contradicts.
        StatusMessage message = MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.FromMinutes(5), Local);

        Assert.Equal(StatusTone.Warning, message.Tone);
        Assert.DoesNotContain("second or two", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineThatWasMeasuringAndStopped_IsNotToldItNeverStarted()
    {
        // Since the service refuses to serve a sample that has stopped advancing, this outcome
        // arrives in TWO completely different situations, and they had one wording between
        // them. "It still hasn't produced a reading" is false in front of a screen full of
        // numbers that machine measured - and "Not connected" is false as well, because it IS
        // connected: that is precisely what makes this case worth telling apart. What separates
        // them is already an argument of this function.
        StatusMessage message =
            MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.FromMinutes(5), Remote, hasValuesOnScreen: true, hasMeasured: true);

        Assert.Equal(StatusTone.Warning, message.Tone);
        Assert.Contains("stopped", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("still hasn't", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not connected", message.Subheading, StringComparison.Ordinal);
        Assert.Contains("measuring", message.Subheading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMachineWithNothingOnScreenIsStillToldItHasNotSampledYet()
    {
        // The other half of the pair, pinned so the branch cannot be collapsed in either
        // direction: with nothing drawn, "no readings yet" is what the dashboard actually
        // knows, whatever that machine did before it was being watched.
        StatusMessage message =
            MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.FromMinutes(5), Remote, hasValuesOnScreen: false);

        Assert.Contains("yet", message.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stopped", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AfterSwitchingToAStoppedMachineTheBarSaysWhatItsRowSays()
    {
        // The machine measured, the panels were cleared by the switch, so nothing is drawn. Both
        // facts are true at once and they answer different questions: the dashboard KNOWS this
        // machine was measuring - its row in the sidebar watched it - and it knows the screen is
        // empty. Read as one fact, the bar said the machine had never produced a reading while
        // the row beside it said it had stopped. Two sentences about one machine, at once.
        StatusMessage message = MessageFor(
            ServiceOutcome.NotReadyYet,
            TimeSpan.FromMinutes(5),
            Remote,
            hasValuesOnScreen: false,
            hasMeasured: true);

        Assert.Equal("Not measuring", message.Title);

        // And it does not promise values that are not there.
        Assert.DoesNotContain("values below", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("values shown", message.Subheading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMachineThatWasMeasuringIsNotPromisedItWillSortItselfOut()
    {
        // The same lie, in smaller type: inside the grace the bar shows the CLIENT's sentence,
        // which says the service "hasn't produced its FIRST reading yet" and that it "usually
        // clears on its own". Neither is true of a machine that was measuring a moment ago.
        StatusMessage message =
            MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.Zero, Remote, hasValuesOnScreen: true, hasMeasured: true);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.DoesNotContain("technical detail from the test", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("first", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ServiceOutcome.TokenRejected)]
    [InlineData(ServiceOutcome.IncompatibleVersion)]
    [InlineData(ServiceOutcome.UnreadableResponse)]
    [InlineData(ServiceOutcome.UnexpectedResponse)]
    [InlineData(ServiceOutcome.Unknown)]
    public void WhatWaitingCannotFix_IsRedStraightAway(ServiceOutcome outcome)
    {
        // Waiting only helps where waiting can change the outcome. A wrong token, an
        // incompatible version or an unreadable response will be identical in a minute:
        // postponing the alarm would only postpone the moment the user can act.
        StatusMessage immediate = MessageFor(outcome, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Error, immediate.Tone);
        Assert.Contains("technical detail", immediate.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoValuesOnScreen_TheSubheadingDoesNotInventAny()
    {
        StatusMessage waiting = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);
        StatusMessage fault = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromMinutes(1), Local);

        Assert.DoesNotContain("last successful reading", waiting.Subheading, StringComparison.Ordinal);
        Assert.DoesNotContain("last successful reading", fault.Subheading, StringComparison.Ordinal);
        Assert.Equal("Not connected.", fault.Subheading);
    }

    [Fact]
    public void WithValuesOnScreen_TheSubheadingSaysTheyAreStale()
    {
        // Leaving the values on screen without saying so would make them read as current: it
        // is the easiest way to make a machine look healthy while it is down.
        StatusMessage fault = MessageFor(
            ServiceOutcome.Unreachable,
            TimeSpan.FromMinutes(1),
            Local,
            hasValuesOnScreen: true);

        Assert.Contains("last successful reading", fault.Subheading, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOutcomeProducesAnEmptyBar()
    {
        // A visible bar with no title or no text is a coloured panel that says nothing, and it
        // is worse than no bar at all.
        foreach (ServiceOutcome outcome in Enum.GetValues<ServiceOutcome>())
        {
            if (outcome == ServiceOutcome.Ok)
            {
                continue;
            }

            foreach (TimeSpan failingFor in new[] { TimeSpan.Zero, TimeSpan.FromHours(1) })
            {
                StatusMessage message = MessageFor(outcome, failingFor, Local);

                Assert.False(string.IsNullOrWhiteSpace(message.Title), $"{outcome} at {failingFor}: the title is empty");
                Assert.False(string.IsNullOrWhiteSpace(message.Text), $"{outcome} at {failingFor}: the text is empty");
                Assert.False(
                    string.IsNullOrWhiteSpace(message.Subheading),
                    $"{outcome} at {failingFor}: the subheading is empty");
            }
        }
    }

    [Theory]
    [InlineData(ServiceOutcome.ConnectionRefused)]
    [InlineData(ServiceOutcome.TimedOut)]
    public void BothWaysOfNotAnsweringDeserveTheGraceToo(ServiceOutcome outcome)
    {
        // Just after booting, a machine refuses the connection because the port is not open
        // yet, and later in the boot it accepts it. Taking the grace away from these two would
        // put the red bar back when the window opens, which is the defect this class exists to
        // close.
        StatusMessage message = MessageFor(outcome, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Equal("Connecting", message.Title);
    }

    [Fact]
    public void ARefusalAndATimeoutDoNotReadTheSame()
    {
        // The heart of this fix. The two faults have opposite remedies: one is solved by
        // starting a service, the other by opening a port. If the title is the same, whoever
        // is looking at the window has nothing else to tell them apart by.
        StatusMessage refusal = MessageFor(ServiceOutcome.ConnectionRefused, TimeSpan.FromMinutes(1), Remote);
        StatusMessage timeout = MessageFor(ServiceOutcome.TimedOut, TimeSpan.FromMinutes(1), Remote);

        Assert.Equal(StatusTone.Error, refusal.Tone);
        Assert.Equal(StatusTone.Error, timeout.Tone);
        Assert.NotEqual(refusal.Title, timeout.Title);
        Assert.NotEqual("Service unreachable", refusal.Title);
        Assert.NotEqual("Service unreachable", timeout.Title);
    }

    [Fact]
    public void EveryWayOfFailingHasATitleOfItsOwn()
    {
        // The hole this test closes is not visible on screen: it is the discard arm at the
        // bottom of the switch. Adding a value to the enum COMPILES, and the new fault ends up
        // silently under a generic title, with the grace taken away without anyone having
        // decided it. No test used to fail. Now this one does.
        List<string> withGenericTitle = [];

        foreach (ServiceOutcome outcome in Enum.GetValues<ServiceOutcome>())
        {
            if (outcome is ServiceOutcome.Ok or ServiceOutcome.Unknown)
            {
                continue;
            }

            if (MessageFor(outcome, TimeSpan.FromHours(1), Remote).Title == "Reading failed")
            {
                withGenericTitle.Add(outcome.ToString());
            }
        }

        Assert.True(
            withGenericTitle.Count == 0,
            "these outcomes end up under the generic title instead of having one of their own: "
                + string.Join(", ", withGenericTitle));
    }
}