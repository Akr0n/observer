using System.Globalization;
using System.Net;
using Observer.Cli;
using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>
/// Every way a rotation can fail to reach the running service, and what each one is told.
/// </summary>
/// <remarks>
/// The rule the whole table is built on: <b>exit 0 means the old key is provably dead, or
/// provably has nothing left to answer it.</b> A verb whose promise is a revocation is worth
/// less than nothing if it can print success it has not witnessed, so the interesting assertions
/// here are the ones that demand a NON-zero exit.
/// </remarks>
public class RevocationTests
{
    private static readonly DateTimeOffset Written =
        new(2026, 9, 20, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TheStampMustMatchTheFileThatWasJustWritten()
    {
        // The proof, and the only assertion that earns an exit code of zero on a running service.
        RevocationVerdict verdict = Revocation.Judge(Answer(HttpStatusCode.OK), Written, Written, false);

        Assert.Equal(RevocationState.Applied, verdict.State);
        Assert.Equal(0, verdict.ExitCode);
    }

    [Fact]
    public void AServiceThatReadADifferentStoreHasNotRevokedAnything()
    {
        // "It said 200" is not the claim being made. A second service, another store path, or a
        // reload that raced this write all land here, and every one of them may still be holding
        // the old key - so this must not read as success.
        RevocationVerdict verdict = Revocation.Judge(
            Answer(HttpStatusCode.OK), Written, Written.AddMinutes(-5), false);

        Assert.Equal(RevocationState.DifferentStore, verdict.State);
        Assert.Equal(1, verdict.ExitCode);
    }

    [Fact]
    public void AnAnswerWithNoStampAtAllIsNotSuccessEither()
    {
        // An older or unexpected body shape. It has to degrade into a warning, not into a
        // success and not into a stack trace: this is the verb someone runs during an incident.
        RevocationVerdict verdict = Revocation.Judge(
            new LocalChannelAnswer(HttpStatusCode.OK, "{}", Silent: false), Written, null, false);

        Assert.Equal(RevocationState.DifferentStore, verdict.State);
        Assert.Equal(1, verdict.ExitCode);
    }

    [Fact]
    public void ASilentChannelWithNothingOnThePortMeansNothingHereAcceptsAnything()
    {
        RevocationVerdict verdict = Revocation.Judge(Silent(), Written, null, somethingOnThePort: false);

        Assert.Equal(RevocationState.NotRunning, verdict.State);
        Assert.Equal(0, verdict.ExitCode);
    }

    [Fact]
    public void ASilentChannelWithSomethingOnThePortIsTheDangerousCaseAndIsLoud()
    {
        // The case the whole port probe exists for: the local channel can be switched off in
        // configuration while the service goes on answering the network with the leaked key.
        // Collapsing this into "not running" would print reassurance over an open door.
        RevocationVerdict verdict = Revocation.Judge(Silent(), Written, null, somethingOnThePort: true);

        Assert.Equal(RevocationState.StillAccepted, verdict.State);
        Assert.Equal(1, verdict.ExitCode);
        Assert.Contains("STILL ACCEPTS", verdict.Headline, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, verdict.Advice);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, RevocationState.ServiceTooOld)]
    [InlineData(HttpStatusCode.Forbidden, RevocationState.Refused)]
    [InlineData(HttpStatusCode.Conflict, RevocationState.NothingToAdopt)]
    [InlineData(HttpStatusCode.InternalServerError, RevocationState.ServiceFailed)]
    [InlineData(HttpStatusCode.BadGateway, RevocationState.ServiceFailed)]
    public void EveryOtherAnswerIsNamedAndNoneOfThemExitsZero(HttpStatusCode status, RevocationState expected)
    {
        // A 404 on the LOCAL channel cannot mean "hidden from you" - the access rule answers
        // NotFound only to a caller that is not local and identified - so it genuinely means the
        // service has no such route, which is to say it predates this command.
        RevocationVerdict verdict = Revocation.Judge(Answer(status), Written, null, false);

        Assert.Equal(expected, verdict.State);
        Assert.Equal(1, verdict.ExitCode);
    }

    [Fact]
    public void TheServicesOwnReasonIsCarriedThroughRatherThanReplaced()
    {
        // The service can tell apart "you were given a token in configuration" from "your store
        // is gone", and those send the operator to different places. Swallowing the detail would
        // throw that away at the last step.
        LocalChannelAnswer answer = new(
            HttpStatusCode.Conflict,
            "{\"detail\":\"this service is not serving a stored token\"}",
            Silent: false);

        Assert.Contains(
            "not serving a stored token",
            Revocation.Judge(answer, Written, null, false).Headline,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"storeWrittenAt\":\"yesterday\"}")]
    [InlineData("{\"somethingElse\":1}")]
    public void AStampThatCannotBeReadIsNullRatherThanAnException(string body) =>
        Assert.Null(Revocation.StampIn(body));

    [Fact]
    public void AStampThatCanBeReadComesBackAsWritten() =>
        Assert.Equal(
            Written,
            Revocation.StampIn(string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"storePath\":\"somewhere\",\"storeWrittenAt\":\"{Written:O}\",\"keys\":\"one current key, no previous key\"}}")));

    [Fact]
    public void AnImmediateRotationLeavesTheLeakedKeyNowhereOnDisk()
    {
        // The one line that decides whether a compromised secret is written back to the file.
        // Rotate(now, TimeSpan.Zero) looks equivalent and is not: it keeps the leaked key with an
        // expiry in the past, which is a secret on disk for nothing - and it is still accepted at
        // the exact instant of that expiry, because the comparison is inclusive.
        MachineCredentials leaked = new("leaked-key", null, null);

        MachineCredentials immediate = Commands.Replacement(leaked, immediately: true, Written);

        Assert.Null(immediate.Previous);
        Assert.Null(immediate.PreviousExpiresAt);
        Assert.NotEqual("leaked-key", immediate.Current);
        Assert.False(immediate.Accepts("leaked-key", Written));
    }

    [Fact]
    public void AGracefulRotationKeepsThePreviousKeyForTheWholeGracePeriod()
    {
        // The other half, and the reason --now had to be a separate flag rather than a change of
        // behaviour: cutting every watching machine off at once is exactly what makes a rotation
        // something nobody dares to run.
        MachineCredentials tired = new("old-key", null, null);

        MachineCredentials graceful = Commands.Replacement(tired, immediately: false, Written);

        Assert.Equal("old-key", graceful.Previous);
        Assert.True(graceful.Accepts("old-key", Written));
        Assert.True(graceful.Accepts("old-key", Written + MachineCredentials.GracePeriod));
        Assert.False(graceful.Accepts("old-key", Written + MachineCredentials.GracePeriod + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TheDefaultStateClaimsNothing() =>
        // A value nobody filled in must not be able to say a revocation happened.
        Assert.Equal(RevocationState.Unknown, default(RevocationState));

    private static LocalChannelAnswer Answer(HttpStatusCode status) =>
        new(status, "{}", Silent: false);

    private static LocalChannelAnswer Silent() =>
        new(null, string.Empty, Silent: true);
}
