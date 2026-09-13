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
}