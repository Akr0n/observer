using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// The authorization decision, as an exhaustive table.
/// </summary>
/// <remarks>
/// Twelve cases, which are ALL the cases: three kinds of caller times two endpoint scopes times
/// two token outcomes. Checking it this way costs less than starting the service, and above all
/// it runs identically on both CI runners, which a local channel does not.
/// </remarks>
public class AccessPolicyTests
{
    [Theory]
    // A local identified caller is ALWAYS allowed, with no token. That is the goal of the project.
    [InlineData(CallerKind.LocalIdentified, EndpointScope.Anywhere, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.Anywhere, false, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.LocalOnly, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.LocalOnly, false, AccessDecision.Allowed)]
    // From the network: the token is the only credential, as it is today.
    [InlineData(CallerKind.FromNetwork, EndpointScope.Anywhere, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.FromNetwork, EndpointScope.Anywhere, false, AccessDecision.Denied)]
    // Local-only endpoints do NOT exist for a caller that is not local, not even with the right
    // token: whoever steals the token must not be able to rotate the keys and lock the owner out.
    [InlineData(CallerKind.FromNetwork, EndpointScope.LocalOnly, true, AccessDecision.NotFound)]
    [InlineData(CallerKind.FromNetwork, EndpointScope.LocalOnly, false, AccessDecision.NotFound)]
    // An identity that cannot be read: refused, EVEN with a valid token.
    [InlineData(CallerKind.Unidentified, EndpointScope.Anywhere, true, AccessDecision.Denied)]
    [InlineData(CallerKind.Unidentified, EndpointScope.Anywhere, false, AccessDecision.Denied)]
    [InlineData(CallerKind.Unidentified, EndpointScope.LocalOnly, true, AccessDecision.NotFound)]
    [InlineData(CallerKind.Unidentified, EndpointScope.LocalOnly, false, AccessDecision.NotFound)]
    public void TheWholeTable(
        CallerKind caller,
        EndpointScope scope,
        bool tokenIsValid,
        AccessDecision expected) =>
        Assert.Equal(expected, AccessPolicy.Decide(caller, scope, tokenIsValid));

    [Theory]
    // On the machine itself the token is not the question - there is no token on that channel -
    // and the operating system's own answer is too generous: the pipe admits every INTERACTIVE
    // user, on purpose, so that the person at the console can WATCH without being put in a
    // group. Stopping a process that LocalSystem will then kill on their behalf is a different
    // matter, and it takes a token that really carries the group.
    [InlineData(CallerKind.LocalIdentified, CallerElevation.Yes, true)]
    [InlineData(CallerKind.LocalIdentified, CallerElevation.No, false)]
    // Linux: the socket's own mode already turned away anyone outside the service's group, so
    // there is nothing left here to ask. NotApplicable is a decision, not an absence.
    [InlineData(CallerKind.LocalIdentified, CallerElevation.NotApplicable, true)]
    // From the network the token stays the only credential, by decision: there is no identity to
    // read on that route, and refusing there would remove the reason the kill is reachable from
    // the network at all. The elevation column is therefore meaningless, and all three agree.
    [InlineData(CallerKind.FromNetwork, CallerElevation.Yes, true)]
    [InlineData(CallerKind.FromNetwork, CallerElevation.No, true)]
    [InlineData(CallerKind.FromNetwork, CallerElevation.NotApplicable, true)]
    // Already refused by Decide before the endpoint is reached. Answered here too, so the
    // function is total and the endpoint stays safe behind any other guard.
    [InlineData(CallerKind.Unidentified, CallerElevation.Yes, false)]
    [InlineData(CallerKind.Unidentified, CallerElevation.No, false)]
    [InlineData(CallerKind.Unidentified, CallerElevation.NotApplicable, false)]
    public void TheWholeTableForTheOneWrite(CallerKind caller, CallerElevation elevation, bool expected) =>
        Assert.Equal(expected, AccessPolicy.MayEndProcesses(caller, elevation));

    [Theory]
    // On the machine itself, and only with a token that really carries the group - the same
    // question the kill asks, for a reason of its own: this is a caller with no credential
    // reaching in and changing what the service will accept, on a channel Windows opens to every
    // interactive user.
    [InlineData(CallerKind.LocalIdentified, CallerElevation.Yes, true)]
    [InlineData(CallerKind.LocalIdentified, CallerElevation.No, false)]
    // Linux: the socket's own mode already turned away anyone outside the service's group.
    [InlineData(CallerKind.LocalIdentified, CallerElevation.NotApplicable, true)]
    // FROM THE NETWORK: NEVER, and this is the row where it parts company with the kill. The
    // endpoint is local-only, so Decide has already answered 404 - but the rule says no on its
    // own, because whoever steals the token must not be able to rotate the keys and lock the
    // owner out of their own machine.
    [InlineData(CallerKind.FromNetwork, CallerElevation.Yes, false)]
    [InlineData(CallerKind.FromNetwork, CallerElevation.No, false)]
    [InlineData(CallerKind.FromNetwork, CallerElevation.NotApplicable, false)]
    // An identity that cannot be read is refused here too, so the function is total.
    [InlineData(CallerKind.Unidentified, CallerElevation.Yes, false)]
    [InlineData(CallerKind.Unidentified, CallerElevation.No, false)]
    [InlineData(CallerKind.Unidentified, CallerElevation.NotApplicable, false)]
    public void TheWholeTableForReloadingTheCredentials(
        CallerKind caller, CallerElevation elevation, bool expected) =>
        Assert.Equal(expected, AccessPolicy.MayReloadCredentials(caller, elevation));

    [Fact]
    public void TheNetworkMayStopAProcessButMayNeverTouchTheKeys()
    {
        // The pair that says why reloading is a THIRD rule and not a reuse of the kill's. The
        // same caller - from the network, with a valid token - is allowed to destroy a process on
        // the machine, by an explicit decision, and is refused the one operation that could lock
        // the machine's owner out of it. Collapsing the two rules would give one of those away,
        // and the dangerous direction is the silent one.
        Assert.True(AccessPolicy.MayEndProcesses(CallerKind.FromNetwork, CallerElevation.Yes));
        Assert.False(AccessPolicy.MayReloadCredentials(CallerKind.FromNetwork, CallerElevation.Yes));
    }

    [Fact]
    public void ReachingTheEndpointAndBeingAllowedToKillAreTwoDifferentQuestions()
    {
        // The pair that says why this is a second rule and not a change to the first. The same
        // caller passes Decide - it is local and identified, so it reads everything with no
        // token, which is the project's whole point - and is refused the one write. Collapsing
        // the two would either lock the console user out of the gauges or hand them the kill.
        Assert.Equal(
            AccessDecision.Allowed,
            AccessPolicy.Decide(CallerKind.LocalIdentified, EndpointScope.Anywhere, tokenIsValid: false));

        Assert.False(AccessPolicy.MayEndProcesses(CallerKind.LocalIdentified, CallerElevation.No));
    }

    [Fact]
    public void AValidTokenDoesNotSaveAnUnidentifiableCaller()
    {
        // The impersonation level is chosen by the CLIENT: with Anonymous a caller unilaterally
        // makes itself unidentifiable while still being able to present a token. If the token
        // were enough, the rule "an unreadable identity is denied" would mean nothing.
        Assert.Equal(
            AccessDecision.Denied,
            AccessPolicy.Decide(CallerKind.Unidentified, EndpointScope.Anywhere, tokenIsValid: true));
    }

    [Fact]
    public void TheDefaultEnumValuesAreTheOnesThatDeny()
    {
        // A forgotten field, an uninitialized struct or a branch added by mistake must DENY. An
        // endpoint whose scope was forgotten becomes unreachable from the network, which is the
        // safe way for it to break.
        Assert.Equal(AccessDecision.Denied, default(AccessDecision));
        Assert.Equal(EndpointScope.LocalOnly, default(EndpointScope));
        Assert.Equal(CallerKind.Unidentified, default(CallerKind));

        Assert.Equal(
            AccessDecision.NotFound,
            AccessPolicy.Decide(default, default, tokenIsValid: false));
    }

    [Fact]
    public void EveryCombinationHasADecision()
    {
        // No case is left without an answer, and none falls into a default branch by accident.
        foreach (CallerKind caller in Enum.GetValues<CallerKind>())
        {
            foreach (EndpointScope scope in Enum.GetValues<EndpointScope>())
            {
                foreach (bool token in new[] { true, false })
                {
                    Assert.True(
                        Enum.IsDefined(AccessPolicy.Decide(caller, scope, token)),
                        $"{caller}/{scope}/{token} produced an undefined outcome");
                }
            }
        }
    }
}