using Observer.Core.Platform;

namespace Observer.Core.Security;

/// <summary>
/// Where the CLIENT keeps the tokens of the remote machines.
/// </summary>
/// <remarks>
/// It comes from a real defect: the tokens sat in cleartext inside <c>machines.json</c>, a file
/// written by hand and meant to be looked at. As long as that token only allowed reading someone
/// else's CPU the cost of losing it was contained; since it also authorizes killing processes,
/// the same file is worth far more.
/// <para>
/// Not to be confused with the SERVICE's store, under
/// <c>Observer.Service/Credentials/</c>: that one keeps the token a machine demands, is one per
/// machine and lives in a system directory. This one keeps the tokens a user presents to OTHER
/// machines, is per user, and does not try to defend itself from the administrators of its own
/// machine, who can read everything anyway.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>Where the secrets are kept, in one sentence to show to whoever is looking.</summary>
    string Description { get; }

    /// <summary>Reads a secret. False if it is not there.</summary>
    /// <param name="name">The name it was stored under.</param>
    /// <param name="secret">The secret that was read.</param>
    /// <returns>True if it was there.</returns>
    /// <exception cref="SecretStoreException">If it is there but reading it is not safe.</exception>
    bool TryRead(string name, out string secret);

    /// <summary>Stores a secret, replacing the one that was there.</summary>
    /// <param name="name">The name to store it under.</param>
    /// <param name="secret">The secret.</param>
    void Write(string name, string secret);

    /// <summary>Deletes a secret. False if it was not there.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <returns>True if it was there and has been removed.</returns>
    bool Delete(string name);
}

/// <summary>
/// The store is there but cannot be trusted, or it refused to answer.
/// </summary>
/// <remarks>
/// An exception and not a <c>false</c>: "the secret is not there" and "the secret is there but
/// the file is readable by anyone" are two different things, and the second must not be
/// mistakable for the first and end up in a branch that invites storing it again.
/// </remarks>
public sealed class SecretStoreException : Exception
{
    /// <summary>Creates the exception with the reason to show.</summary>
    /// <param name="message">The reason, already written for whoever reads it.</param>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the reason and the cause.</summary>
    /// <param name="message">The reason.</param>
    /// <param name="innerException">The cause.</param>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no reason. It exists only for the analyzer.</summary>
    public SecretStoreException()
    {
    }
}

/// <summary>What a secret is called, and what it cannot be called.</summary>
/// <remarks>
/// It lives here and not inside the Unix store because the name comes from <c>machines.json</c>,
/// which a person writes, and ends up composing a file path: if it were not checked, an entry
/// called <c>../../id_rsa</c> would read and overwrite a file outside the secrets directory.
/// Platform-neutral also so that the rule is tested by both runners, and not only where the file
/// store really exists.
/// </remarks>
public static class SecretName
{
    /// <summary>The cleaned-up name, or an exception if it cannot be used.</summary>
    /// <param name="name">The name as it arrives from configuration.</param>
    /// <returns>The name without spaces at its edges.</returns>
    /// <exception cref="SecretStoreException">If the name cannot be used.</exception>
    public static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (char letter in name)
        {
            if (!char.IsAsciiLetterOrDigit(letter) && letter is not ('-' or '_' or '.' or ' '))
            {
                throw new SecretStoreException(
                    $"\"{name}\" cannot be used as a machine name here: only letters, digits, " +
                    "spaces, dots, dashes and underscores are allowed.");
            }
        }

        string cleaned = name.Trim();

        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            throw new SecretStoreException($"\"{name}\" cannot be used as a machine name here.");
        }

        return cleaned;
    }
}

/// <summary>Picks the right store for a platform.</summary>
/// <remarks>
/// The platform is a PARAMETER, as it is for the collectors: that is what makes the choice
/// testable from the Windows runner as well as from the Linux one, instead of having a branch
/// that neither of them ever runs.
/// </remarks>
public static class SecretStores
{
    /// <summary>The store for the given platform.</summary>
    /// <param name="platform">Which operating system.</param>
    /// <returns>The store.</returns>
    public static ISecretStore For(HostPlatform platform) => platform switch
    {
        HostPlatform.Windows when OperatingSystem.IsWindows() => new WindowsSecretStore(),
        HostPlatform.Linux when OperatingSystem.IsLinux() => new UnixSecretStore(),
        _ => new UnsupportedSecretStore(),
    };

    /// <summary>The store of this machine.</summary>
    /// <returns>The store.</returns>
    public static ISecretStore ForThisMachine() => For(HostPlatformDetector.Current);
}

/// <summary>Store for a platform where nothing is known to be kept safely.</summary>
/// <remarks>
/// It exists for the same reason as the other "Unsupported" providers: an unknown platform must
/// SAY that it cannot keep a secret, not fake an empty store and make whoever is looking
/// conclude that they forgot to store it.
/// </remarks>
public sealed class UnsupportedSecretStore : ISecretStore
{
    private const string Reason =
        "This platform has no supported place to keep machine tokens. Observer knows the " +
        "Windows Credential Manager and, on Linux, a file readable only by its owner.";

    /// <inheritdoc />
    public string Description => "no secret store is available on this platform";

    /// <inheritdoc />
    public bool TryRead(string name, out string secret) => throw new SecretStoreException(Reason);

    /// <inheritdoc />
    public void Write(string name, string secret) => throw new SecretStoreException(Reason);

    /// <inheritdoc />
    public bool Delete(string name) => throw new SecretStoreException(Reason);
}