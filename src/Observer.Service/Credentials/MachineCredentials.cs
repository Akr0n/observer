using System.Security.Cryptography;
using System.Text;

namespace Observer.Service.Credentials;

/// <summary>
/// The machine token, with the previous key still valid for a window.
/// </summary>
/// <param name="Current">The current key.</param>
/// <param name="Previous">The key replaced by the last rotation, if there is one.</param>
/// <param name="PreviousExpiresAt">When <paramref name="Previous"/> stops being valid.</param>
/// <remarks>
/// This token is good FROM THE NETWORK and does not expire on its own: that is the reason why the
/// store that holds it must be protected like a secret and not like a preference.
/// <para>
/// ToString() is overridden because records generate one with ALL the properties in it: without
/// it, one careless log line would be enough to print the key.
/// </para>
/// </remarks>
public sealed record MachineCredentials(
    string Current,
    string? Previous,
    DateTimeOffset? PreviousExpiresAt)
{
    /// <summary>How long the previous key stays valid after a rotation.</summary>
    /// <remarks>
    /// Without this window, rotating would cut off every remote client INSTANTLY, and the
    /// rotation would become an operation nobody dares to perform — that is, a key that never
    /// gets changed.
    /// </remarks>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    /// <summary>Brand new credentials, with no previous key.</summary>
    /// <returns>The credentials.</returns>
    public static MachineCredentials Create() => new(TokenGenerator.Generate(), null, null);

    /// <summary>Whether the presented token is acceptable at this instant.</summary>
    /// <param name="presented">The token that arrived in the header.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>True if it matches the current key, or the previous one not yet expired.</returns>
    public bool Accepts(string presented, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(presented))
        {
            // The branch that a badly written comparison turns into a free pass.
            return false;
        }

        if (ConstantTimeEquals(presented, Current))
        {
            return true;
        }

        return Previous is { Length: > 0 } previous
            && PreviousExpiresAt is { } expiry
            && now <= expiry
            && ConstantTimeEquals(presented, previous);
    }

    /// <summary>Generates a new key, keeping the current one as previous.</summary>
    /// <param name="now">The instant of the rotation.</param>
    /// <param name="grace">How long the current key will go on being valid.</param>
    /// <returns>The rotated credentials.</returns>
    /// <remarks>
    /// ONE previous key only is kept. Keeping a chain of them would mean that a compromised
    /// key stays valid until someone rotates enough times, that is, that revocation is never
    /// immediate.
    /// </remarks>
    public MachineCredentials Rotate(DateTimeOffset now, TimeSpan grace) =>
        new(TokenGenerator.Generate(), Current, now + grace);

    /// <summary>Hides the keys. See the type's notes.</summary>
    /// <returns>A description with no secrets in it.</returns>
    public override string ToString() =>
        PreviousExpiresAt is { } expiry
            ? FormattableString.Invariant($"MachineCredentials {{ one current key, one previous key valid until {expiry:O} }}")
            : "MachineCredentials { one current key, no previous key }";

    /// <summary>
    /// Constant-time comparison: a normal comparison exits at the first differing byte, and that
    /// difference in time makes it possible to guess the token one character at a time.
    /// </summary>
    private static bool ConstantTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
}