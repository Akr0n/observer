namespace Observer.Service.Credentials;

/// <summary>What was observed of a directory that is a candidate to hold the machine token.</summary>
/// <param name="Exists">Whether the directory exists.</param>
/// <param name="IsReparsePoint">Whether it is a junction or a symbolic link.</param>
/// <param name="SecurityDescriptorReadable">Whether the security descriptor could be read.</param>
/// <param name="OwnerSid">The owner's SID, in textual form.</param>
/// <param name="DaclProtected">Whether the DACL is protected, that is, does NOT inherit from the parent.</param>
/// <param name="DaclSids">The SIDs that appear in the access rules.</param>
public sealed record DirectoryFacts(
    bool Exists,
    bool IsReparsePoint,
    bool SecurityDescriptorReadable,
    string? OwnerSid,
    bool DaclProtected,
    IReadOnlyList<string> DaclSids);

/// <summary>The outcome of the evaluation. The ZERO value is not the one that authorizes.</summary>
public enum DirectoryVerdict
{
    /// <summary>The descriptor could not even be read.</summary>
    Unknown = 0,

    /// <summary>It is a junction or a link: the data would end up somewhere else.</summary>
    ReparsePoint,

    /// <summary>The owner can rewrite the DACL whenever it wants.</summary>
    UntrustedOwner,

    /// <summary>The DACL inherits, or grants to someone who must not get in.</summary>
    OpenDacl,

    /// <summary>It does not exist: it can be created from scratch, which is the best case.</summary>
    Missing,

    /// <summary>Trusted owner, protected DACL, no outsiders.</summary>
    Safe,
}

/// <summary>A convenience so the negative cases need not be listed by hand.</summary>
public static class DirectoryVerdictExtensions
{
    /// <summary>Whether a directory in this state can already hold a secret.</summary>
    /// <param name="verdict">The outcome of the evaluation.</param>
    /// <returns>True only for <see cref="DirectoryVerdict.Safe"/>.</returns>
    /// <remarks>
    /// Written as "equal to Safe" and not as "different from these three": adding a negative case
    /// to the enum tomorrow must not turn it into a permission through inattention.
    /// </remarks>
    public static bool CanHoldSecret(this DirectoryVerdict verdict) =>
        verdict == DirectoryVerdict.Safe;
}

/// <summary>
/// Decides whether the directory that will hold the machine token can be trusted.
/// </summary>
/// <remarks>
/// A PURE function over the observed facts, because the cases that matter cannot all be built on
/// just any machine — a directory owned by SYSTEM requires an administrative session — and because
/// it is the load-bearing security decision of the store.
/// <para>
/// The order of the checks is binding and is not a matter of style. See the comments.
/// </para>
/// </remarks>
public static class DirectoryTrust
{
    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>The trusted owners when no others are given.</summary>
    public static readonly IReadOnlyList<string> DefaultTrustedSids = [SystemSid, AdministratorsSid];

    /// <summary>Evaluates the directory against SYSTEM and the administrators.</summary>
    /// <param name="facts">The facts gathered from the operating system.</param>
    /// <returns>The verdict.</returns>
    public static DirectoryVerdict Evaluate(DirectoryFacts facts) => Evaluate(facts, DefaultTrustedSids);

    /// <summary>Evaluates the directory against an explicit set of trusted principals.</summary>
    /// <param name="facts">The facts gathered from the operating system.</param>
    /// <param name="trustedSids">
    /// The SIDs that may own the directory and appear in its DACL. In production these are
    /// SYSTEM and the administrators, plus the account that RUNS the service - which in
    /// production coincides with SYSTEM and therefore grants nothing new. Launched by hand
    /// in development it is what lets the service trust the directory it created itself.
    /// A standard user cannot in any way create a directory owned by SYSTEM, verified:
    /// SetOwner fails. The extension opens no path for anyone.
    /// </param>
    /// <returns>The verdict.</returns>
    public static DirectoryVerdict Evaluate(DirectoryFacts facts, IReadOnlyList<string> trustedSids)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(trustedSids);

        if (!facts.Exists)
        {
            // The best case: it is created from scratch, already with the right owner and DACL,
            // with nothing to repair.
            return DirectoryVerdict.Missing;
        }

        if (facts.IsReparsePoint)
        {
            // FIRST, before reading any ACL. A junction is created by a standard user with no
            // privilege at all: if this check came later, the owner and the ACL of the ATTACKER's
            // directory would be fixed up and the token would be deposited inside it.
            return DirectoryVerdict.ReparsePoint;
        }

        if (!facts.SecurityDescriptorReadable)
        {
            return DirectoryVerdict.Unknown;
        }

        if (!IsTrusted(facts.OwnerSid, trustedSids))
        {
            // SECOND, and before the DACL. The owner has implicit WRITE_DAC: a perfect DACL on a
            // directory owned by a user is a "fake protected", and that user rewrites it with a
            // single call. Measured.
            return DirectoryVerdict.UntrustedOwner;
        }

        if (!facts.DaclProtected)
        {
            // Not protected means it inherits, and the system directory that hosts the store
            // grants BUILTIN\Users inheritable read access: inheriting is enough to lose the
            // secret, with no attacker needed at all.
            return DirectoryVerdict.OpenDacl;
        }

        return facts.DaclSids.All(sid => IsTrusted(sid, trustedSids))
            ? DirectoryVerdict.Safe
            : DirectoryVerdict.OpenDacl;
    }

    private static bool IsTrusted(string? sid, IReadOnlyList<string> trustedSids) =>
        sid is not null && trustedSids.Contains(sid, StringComparer.OrdinalIgnoreCase);
}