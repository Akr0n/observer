namespace Observer.Service.LocalChannel;

/// <summary>What to do with a request.</summary>
/// <remarks>
/// The ZERO value is <see cref="Denied"/>: a forgotten field or a branch added by
/// inattention denies instead of granting.
/// </remarks>
public enum AccessDecision
{
    /// <summary>401. The credential is missing or is not enough.</summary>
    Denied = 0,

    /// <summary>404. The endpoint must not even appear to exist to this caller.</summary>
    NotFound,

    /// <summary>The request goes through.</summary>
    Allowed,
}

/// <summary>Where an endpoint accepts being reached from.</summary>
/// <remarks>
/// The ZERO value is <see cref="LocalOnly"/>, that is, the most restrictive one: an endpoint
/// whose scope someone forgot to declare becomes unreachable from the network instead of
/// exposed, which is the right direction to break in.
/// </remarks>
public enum EndpointScope
{
    /// <summary>Only from the local channel. It does not exist, for anyone arriving from
    /// elsewhere.</summary>
    LocalOnly = 0,

    /// <summary>From the network too, with the token.</summary>
    Anywhere,
}

/// <summary>
/// Decides whether a request passes. PURE function: no state, no I/O.
/// </summary>
/// <remarks>
/// It replaces the middleware that demanded the bearer token on every request. Being pure it is
/// verified with an exhaustive table that runs identically on CI's two runners, while a local
/// channel cannot be: on ubuntu-latest the named pipe does not even exist.
/// <para>
/// WHICH local users are admitted is not decided by this function. The operating system decides
/// it: on Windows the pipe's DACL, which refuses at the connect already; on Linux the mode of the
/// socket's file. Only two things are checked here - that the caller really is local and that it
/// is identifiable. Adding a list of SIDs here would duplicate a decision the operating system
/// makes better.
/// </para>
/// </remarks>
public static class AccessPolicy
{
    /// <summary>The outcome for this combination.</summary>
    /// <param name="caller">How the one calling was classified.</param>
    /// <param name="scope">Where the endpoint accepts being reached from.</param>
    /// <param name="tokenIsValid">Whether the bearer token presented matches.</param>
    /// <returns>What to do with the request.</returns>
    public static AccessDecision Decide(CallerKind caller, EndpointScope scope, bool tokenIsValid)
    {
        // An identified local caller passes on everything, with no token. It is the project's
        // goal: on the machine the operating system already knows who is calling, and a shared
        // secret is the wrong tool.
        if (caller == CallerKind.LocalIdentified)
        {
            return AccessDecision.Allowed;
        }

        // From here down the caller is NOT an identified local one.
        // A local-only endpoint must not even appear to exist: the pairing endpoints rotate
        // the keys, and whoever stole the token must not be able to lock the owner out. A 403
        // would confirm the endpoint is there; a 404 does not.
        if (scope != EndpointScope.Anywhere)
        {
            return AccessDecision.NotFound;
        }

        // Identity not determinable: refusal EVEN with a valid token. The impersonation level
        // is chosen by the CLIENT, and with Anonymous a caller makes itself unilaterally
        // unidentifiable while still being able to present a token. If the token were enough,
        // the rule "an identity that cannot be determined refuses" would be empty.
        // Whoever has the token can always use the network channel.
        if (caller != CallerKind.FromNetwork)
        {
            return AccessDecision.Denied;
        }

        return tokenIsValid ? AccessDecision.Allowed : AccessDecision.Denied;
    }

    /// <summary>Whether this caller may perform the one thing that is not a read.</summary>
    /// <param name="caller">How the one calling was classified.</param>
    /// <param name="elevation">What the caller's own token can do.</param>
    /// <returns>True when the kill may go ahead.</returns>
    /// <remarks>
    /// A SECOND rule and not a change to <see cref="Decide"/>, because they answer different
    /// questions. That one asks who may reach an endpoint at all, and its remarks say - still
    /// rightly - that WHICH local users are admitted is the operating system's decision and not
    /// this file's. This one asks who may destroy state, and there the operating system's answer
    /// is the wrong one to accept: the pipe's DACL admits INTERACTIVE, every user with a session
    /// on the machine, which is exactly right for WATCHING and exactly wrong for a process that
    /// LocalSystem will then stop on their behalf.
    /// <para>
    /// From the NETWORK the token stays the only credential, by decision. There is no identity
    /// to read on that route - only the secret, and whoever holds it holds it - and refusing the
    /// kill there would remove the reason it is allowed from the network at all: seeing a
    /// runaway process on another machine and stopping it from here.
    /// </para>
    /// <para>
    /// <see cref="CallerKind.Unidentified"/> is already refused by <see cref="Decide"/> before a
    /// request reaches the endpoint. It is answered here as well, so that this function is total
    /// and so that the endpoint stays safe if it is ever called from somewhere with a different
    /// guard in front of it.
    /// </para>
    /// </remarks>
    public static bool MayEndProcesses(CallerKind caller, CallerElevation elevation) =>
        caller switch
        {
            CallerKind.FromNetwork => true,
            CallerKind.LocalIdentified =>
                elevation is CallerElevation.Yes or CallerElevation.NotApplicable,
            _ => false,
        };

    /// <summary>Whether this caller may make the service re-read its credential store.</summary>
    /// <param name="caller">Where the request came from.</param>
    /// <param name="elevation">What the caller's own token can do, where that question applies.</param>
    /// <returns>True if the reload may go ahead.</returns>
    /// <remarks>
    /// A THIRD rule beside the other two, and not a widening of either. <see cref="Decide"/> says
    /// who reaches an endpoint; <see cref="MayEndProcesses"/> says who may destroy a process;
    /// this says who may change what the service itself will accept from now on. They answer
    /// different questions about the same caller, and collapsing them would mean giving one of the
    /// three away.
    /// <para>
    /// FROM THE NETWORK: never, and this is the one row where it differs from the kill. The
    /// endpoint is marked local-only, so <see cref="Decide"/> has already answered 404 to a
    /// network caller before this is asked - the comment on that branch has said since the rule
    /// was written that whoever steals the token must not be able to rotate the keys and lock the
    /// owner out, and this is the endpoint it was written for. Answered here as well so the rule
    /// is total and the endpoint stays safe behind any other guard.
    /// </para>
    /// <para>
    /// LOCALLY: the same elevation the kill asks for, and not merely for consistency. The reload
    /// cannot be made to read a file the caller chose - the path comes from the service's own
    /// configuration - so the worst an unelevated caller could do is make the service re-read a
    /// file only an administrator can write, which changes nothing. But it is still a caller with
    /// no credential reaching in and touching what the service will accept, on a channel Windows
    /// opens to every interactive user; asking for the elevation costs the operator nothing,
    /// because writing the store already required it.
    /// </para>
    /// </remarks>
    public static bool MayReloadCredentials(CallerKind caller, CallerElevation elevation) =>
        caller is CallerKind.LocalIdentified
        && elevation is CallerElevation.Yes or CallerElevation.NotApplicable;
}