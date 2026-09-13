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

/// <summary>The caller's origin, with the diagnosis that produced it.</summary>
/// <param name="Kind">The classification.</param>
/// <param name="Sid">The SID on Windows or the uid on Linux, when readable.</param>
/// <param name="Reason">Why it was decided this way. In English: it ends up in the logs.</param>
public sealed record CallerOrigin(CallerKind Kind, string? Sid, string Reason);