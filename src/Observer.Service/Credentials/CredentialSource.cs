using Microsoft.Extensions.Primitives;

namespace Observer.Service.Credentials;

/// <summary>What a reload did, or why it did nothing.</summary>
/// <remarks>
/// The zero value is the one that changes nothing, for the same reason the access decision's zero
/// is a refusal: a value nobody filled in must not be able to claim a revocation happened.
/// </remarks>
public enum ReloadResult
{
    /// <summary>Nothing was read and nothing was swapped.</summary>
    Failed = 0,

    /// <summary>The store was re-read and the credentials in force are now its contents.</summary>
    Applied,

    /// <summary>There is no store to re-read: the token came from configuration, or is ephemeral.</summary>
    NoStore,

    /// <summary>The store is configured but is not there.</summary>
    StoreMissing,

    /// <summary>The store is there but cannot be read or understood.</summary>
    StoreUnusable,

    /// <summary>The directory holding the store is no longer a place a secret may live.</summary>
    DirectoryNotTrusted,
}

/// <summary>The outcome of a reload, with what the operator needs in order to believe it.</summary>
/// <param name="Result">What happened.</param>
/// <param name="StorePath">
/// The file that was actually read, or null when none was. It comes from the source that did the
/// reading and from nowhere else - an earlier version let the endpoint resolve the path from
/// configuration instead, and CI caught it answering with the default path while the service had
/// read a different file. That is not a cosmetic mismatch: the whole answer exists to identify
/// WHICH file the running service adopted, so a path from a second source of truth undermines the
/// one claim being made.
/// </param>
/// <param name="StoreWrittenAt">
/// When that file had last been written, or null if nothing was read. It is the point of the
/// whole answer: whoever wrote the store can compare this with the stamp of the file they wrote
/// and know the running service read those exact bytes - rather than trusting a service that says
/// "reloaded" about a file nobody has identified.
/// </param>
/// <param name="Detail">A sentence for the operator. It never contains a key.</param>
public sealed record ReloadOutcome(
    ReloadResult Result, string? StorePath, DateTimeOffset? StoreWrittenAt, string Detail);

/// <summary>
/// The machine credentials the service is serving RIGHT NOW, and the one way to replace them.
/// </summary>
/// <remarks>
/// Until 0.23.1 the credentials were a snapshot taken at start-up, and rotating from the command
/// line only rewrote the file: the running service went on accepting the old key until somebody
/// restarted it. For a planned rotation that is tolerable, because the previous key stays valid
/// for a day anyway. For a LEAKED key it is not a revocation at all - it is one that needs a
/// second manual step, which is to say one that half the time does not happen.
/// <para>
/// THE CURRENT CREDENTIALS ARE NOT EXPOSED, and that is deliberate rather than tidy. The only
/// thing callers may do is ask whether a header is acceptable, so the mutable reference cannot be
/// hoisted into a local, a field or a captured variable that would go on answering with a key the
/// machine has revoked. A convention saying "do not capture it" would have been one comment away
/// from being broken; this is a shape the compiler enforces.
/// </para>
/// <para>
/// The swap is an immutable reference published with <see cref="Volatile.Write{T}"/> and read
/// with <see cref="Volatile.Read{T}"/> - the same idiom <c>MetricSnapshotCache</c> uses here, for
/// the same reason. <see cref="MachineCredentials"/> is a record built before it is published and
/// never mutated, so the pair of keys travels inside ONE reference and cannot be read half old
/// and half new. There is no lock: two reloads read the same file and publish equal content, and
/// a request whose check ran microseconds before a swap is decided on the old credentials - one
/// request, not one connection. A lock across every authorization check would put contention on
/// the hottest path in the service to close a window no attacker can aim at.
/// </para>
/// </remarks>
public sealed class CredentialSource
{
    private readonly string? storePath;
    private MachineCredentials current;

    /// <summary>Starts from the credentials provisioning chose.</summary>
    /// <param name="provisioned">What <see cref="CredentialProvisioning.Provision"/> returned.</param>
    public CredentialSource(ProvisionedCredentials provisioned)
    {
        ArgumentNullException.ThrowIfNull(provisioned);

        current = provisioned.Credentials;

        // Null for the Configuration and Ephemeral origins, and that is how a reload knows there
        // is nothing on disk to go back to. A service told its token explicitly must not have it
        // replaced by a file it was told to ignore.
        storePath = provisioned.Path;
    }

    /// <summary>Whether the Authorization header carries a key the service accepts right now.</summary>
    /// <param name="header">The header's value, possibly absent.</param>
    /// <param name="now">The current instant, for the expiry of the previous key.</param>
    /// <returns>True if it matches the current key, or the previous one not yet expired.</returns>
    public bool IsTokenValid(StringValues header, DateTimeOffset now)
    {
        string? value = header.Count == 1 ? header[0] : null;

        return value is not null
            && value.StartsWith("Bearer ", StringComparison.Ordinal)
            && Volatile.Read(ref current).Accepts(value["Bearer ".Length..], now);
    }

    /// <summary>The keys in force, described without any of them in it.</summary>
    /// <returns>A sentence naming how many keys there are and until when.</returns>
    /// <remarks>
    /// <see cref="MachineCredentials.ToString"/> is overridden precisely so this is safe, and it
    /// already says in words whether a previous key survives - which is what tells an operator
    /// a graceful rotation apart from an immediate one.
    /// </remarks>
    public string Describe() => Volatile.Read(ref current).ToString();

    /// <summary>Re-reads the store and serves what it finds there from the next request on.</summary>
    /// <returns>What happened, and the stamp of the file that was read.</returns>
    /// <remarks>
    /// ON EVERY FAILURE THE CREDENTIALS IN FORCE ARE LEFT ALONE. A service that loses sight of its
    /// store keeps serving the key it has: wiping it would lock the owner out of a machine that is
    /// otherwise healthy, which is the opposite of what an immediate rotation asked for.
    /// <para>
    /// The perimeter is re-checked before the file is read, and not only at start-up. The store's
    /// protection is a property of its DIRECTORY, and a directory can be made writable by someone
    /// else long after the service started; adopting a file whose perimeter was last validated
    /// weeks ago would make the runtime path weaker than the start-up path it stands in for.
    /// </para>
    /// <para>
    /// The file is STATTED BEFORE IT IS READ, and the order matters in one direction only. A write
    /// landing between the two makes this report an OLDER stamp than the bytes it actually read,
    /// so an operator comparing stamps concludes "not applied" when it was, and runs the command
    /// again. The other order produces the opposite mistake, which is a false "applied".
    /// </para>
    /// </remarks>
    public ReloadOutcome Reload()
    {
        if (storePath is not { Length: > 0 } path)
        {
            return new ReloadOutcome(
                ReloadResult.NoStore,
                null,
                null,
                "this service is not serving a stored token: it was given one in its configuration, " +
                "or it is running on a throwaway one. Rewriting the store changes nothing here.");
        }

        try
        {
            CredentialDirectory.Prepare(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ReloadOutcome(
                ReloadResult.DirectoryNotTrusted,
                path,
                null,
                "the directory holding the credential store can no longer be secured, so its " +
                "contents were not adopted: " + error.Message);
        }

        DateTimeOffset writtenAt = File.GetLastWriteTimeUtc(path);

        MachineCredentials loaded;

        try
        {
            if (CredentialStore.Read(path) is not { } read)
            {
                return new ReloadOutcome(
                    ReloadResult.StoreMissing,
                    path,
                    null,
                    "there is no credential store at " + path + ", so nothing was adopted.");
            }

            loaded = read;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ReloadOutcome(ReloadResult.StoreUnusable, path, null, error.Message);
        }

        Volatile.Write(ref current, loaded);

        return new ReloadOutcome(ReloadResult.Applied, path, writtenAt, Describe());
    }
}
