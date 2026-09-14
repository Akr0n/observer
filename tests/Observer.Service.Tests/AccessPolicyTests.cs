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

    [Fact]
    public void AValidTokenDoesNotSaveAnUnidentifiableCaller()
    {
        // The impersonation level is chosen by the CLIENT: with Anonymous a caller unilaterally
        // makes itself unidentifiable while still being able to present a token. If the token
        // were enough, the rule "an identity that cannot be read refuses" would mean nothing.
        Assert.Equal(
            AccessDecision.Denied,
            AccessPolicy.Decide(CallerKind.Unidentified, EndpointScope.Anywhere, tokenIsValid: true));
    }

    [Fact]
    public void TheDefaultEnumValuesAreTheOnesThatDeny()
    {
        // A forgotten field, an uninitialized struct or a branch added by mistake must DENY. An
        // endpoint whose scope was forgotten becomes unreachable from the network, which is the
        // right direction to break in.
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