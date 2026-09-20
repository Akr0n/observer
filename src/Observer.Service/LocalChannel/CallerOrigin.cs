namespace Observer.Service.LocalChannel;

/// <summary>How the service classified whoever is calling.</summary>
/// <remarks>
/// The ZERO value is <see cref="Unidentified"/>, that is, the case that DENIES. This way a
/// forgotten field, an uninitialised struct or a branch added by carelessness refuse
/// instead of granting.
/// </remarks>
public enum CallerKind
{
    /// <summary>It was not possible to establish who this is. Refusal.</summary>
    Unidentified = 0,

    /// <summary>Arrived from the network: on Windows via SMB too, not from the machine.</summary>
    FromNetwork,

    /// <summary>Local, and with a readable identity.</summary>
    LocalIdentified,
}

/// <summary>Whether the caller's token really carries administrative rights.</summary>
/// <remarks>
/// The ZERO value is <see cref="No"/>, so a forgotten field refuses, like everything else here.
/// <para>
/// It is about the TOKEN and not about the account, and on Windows those are two different
/// things: a member of Administrators who has not elevated holds a token that does not carry the
/// group at all, because UAC filtered it out. Reading the account's membership instead would
/// hand back a privilege the operating system had deliberately removed from that process.
/// </para>
/// </remarks>
public enum CallerElevation
{
    /// <summary>Not elevated, or it could not be established. Refusal.</summary>
    No = 0,

    /// <summary>The caller's own token carries the administrators group.</summary>
    Yes,

    /// <summary>
    /// The question does not arise on this platform, because the channel itself is the gate.
    /// </summary>
    /// <remarks>
    /// Linux. The socket is 0660 and its directory 0750, both owned by the service's user and
    /// group, so the kernel has already refused everyone who is not the owner or in that group
    /// before a single byte arrives. Windows cannot say the same: its pipe admits INTERACTIVE,
    /// every user with a session on the machine, on purpose - so that the person at the console
    /// needs no group set up to WATCH. Watching is not stopping, and that is where they part.
    /// </remarks>
    NotApplicable,
}

/// <summary>The caller's origin, with the diagnosis that produced it.</summary>
/// <param name="Kind">The classification.</param>
/// <param name="Sid">The SID on Windows or the uid on Linux, when readable.</param>
/// <param name="Reason">Why it was decided this way. In English: it ends up in the logs.</param>
/// <param name="Elevation">
/// Whether the caller may be allowed the one thing that is not a read. Defaults to
/// <see cref="CallerElevation.No"/> so that a classifier which never sets it refuses.
/// </param>
public sealed record CallerOrigin(
    CallerKind Kind,
    string? Sid,
    string Reason,
    CallerElevation Elevation = CallerElevation.No);