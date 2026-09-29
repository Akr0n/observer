namespace Observer.Service.Credentials;

/// <summary>Where the credentials in use came from.</summary>
public enum CredentialOrigin
{
    /// <summary>Ephemeral: generated in memory and never stored. They hold for this run only.</summary>
    Ephemeral = 0,

    /// <summary>From an explicit token in configuration.</summary>
    Configuration,

    /// <summary>Read from the store on disk.</summary>
    Stored,

    /// <summary>Generated now and stored on disk.</summary>
    CreatedAndStored,
}

/// <summary>The credentials in use, with where they came from.</summary>
/// <param name="Credentials">The credentials.</param>
/// <param name="Origin">Where they come from.</param>
/// <param name="Path">The store used, or null if there is none.</param>
public sealed record ProvisionedCredentials(
    MachineCredentials Credentials,
    CredentialOrigin Origin,
    string? Path);

/// <summary>
/// Provides the service with its own machine token.
/// </summary>
/// <remarks>
/// This is the piece that makes an installer possible. As long as the service demands a token in
/// configuration, whoever installs it has to generate one — that is, know it, record it in their
/// own log, and leave it behind if they fail halfway.
/// <para>
/// THE FIRST START WILL ADOPT A STORE THAT IS ALREADY THERE, and that is the intended behaviour —
/// it is what makes the second start reuse the key instead of cutting off every remote client. What
/// keeps it safe is not anything in this file: it is that
/// <see cref="WindowsDirectoryTrust.Prepare"/> refuses a directory owned by an untrusted account
/// when it is not empty, and refuses it BEFORE repairing anything. Do not weaken that guard on the
/// grounds that this class checks the store afterwards. It does not, and it cannot.
/// </para>
/// <para>
/// What it is there for, measured on Windows 11 from an UNELEVATED session: a standard user creates
/// <c>C:\ProgramData\Observer</c>, OWNS it, writes <c>credentials.json</c> into it, and the file
/// inherits <c>SYSTEM: FullControl</c> from <c>ProgramData</c> — so the service, as LocalSystem,
/// can read it. The installer never creates that directory (the service does, at its first start)
/// and removes it again on uninstall, so the window reopens at every reinstall. No race is needed:
/// the name is public and can be planted months ahead. <c>Prepare</c> then saw
/// <see cref="DirectoryVerdict.UntrustedOwner"/>, took ownership, rewrote the DACL, confirmed the
/// directory safe — and the file still lying in it was read back and served as the machine token.
/// The payoff was not only telemetry: <c>AccessPolicy.MayEndProcesses</c> answers true for
/// <c>CallerKind.FromNetwork</c>, so an unprivileged local user chose a key and with it terminated
/// any process on the machine, from the LAN, carried out by LocalSystem.
/// </para>
/// <para>
/// WHY THE REFUSAL HAS TO COME BEFORE THE REPAIR, which is the part that is easy to get wrong and
/// was got wrong once here. Repairing first and refusing afterwards — on the verdict observed
/// before the repair — reads correctly and does not hold for one restart: the first attempt leaves
/// the directory genuinely safe, and the package configures Windows to restart the service five
/// seconds after a failed start (<c>util:ServiceConfig</c> in <c>Observer.wxs</c>, restart on the
/// first, second and every later failure). The second attempt therefore sees a spotless directory
/// and adopts the planted file, automatically, five seconds later. The repair is what destroys the
/// only evidence, so nothing after it can be trusted to decide; the refusal has to be the thing
/// that prevents it.
/// </para>
/// <para>
/// A store is REFUSED and never overwritten or moved aside. Overwriting would destroy a real
/// operator's token together with the evidence of an attempt, and quarantining it would silently
/// cut off every paired dashboard in the case where the file is genuine. And the refusal is narrow
/// on both sides: an untrusted-owner directory that is EMPTY is repaired and used, so squatting a
/// folder name cannot keep the monitor from ever starting, while a directory owned by SYSTEM or the
/// administrators whose DACL merely drifted is repaired with its store intact, so an
/// <c>icacls /reset</c> does not take the monitoring down.
/// </para>
/// <para>
/// WHAT THE RULE COSTS, stated properly because the obvious guess is wrong. It is not the drifted
/// DACL — that case is deliberately exempt. It is a store that an ADMINISTRATOR put here by hand:
/// only two SIDs are trusted, <c>S-1-5-18</c> and <c>S-1-5-32-544</c>, and on Windows the default
/// owner of a new object is its CREATOR, not the Administrators group. So a
/// <c>credentials.json</c> restored from a backup, re-copied with <c>robocopy</c> without
/// <c>/copyall</c>, or dragged in through Explorer is owned by that admin's own account, and the
/// service will refuse to start — in a five-second restart loop, since nothing self-heals by
/// design. The way out is one command and it is in the refusal message and in
/// <c>observer diagnose</c>; it is still a real operational cost, and it is the price of not
/// adopting a key whose author cannot be named. What does NOT regress, checked: an MSI upgrade
/// (the folder survives it untouched and stays SYSTEM-owned), a disk clone or machine rename
/// (both SIDs are well-known), and moving the service from LocalSystem to a domain account (the
/// folder is still owned by <c>S-1-5-18</c>, which is trusted unconditionally).
/// </para>
/// <para>
/// AND WHAT IT STILL DOES NOT CLOSE. The empty-directory exemption leaves a race: see the note at
/// the <c>Repair</c> call in <see cref="WindowsDirectoryTrust.Prepare"/>. Patience is no longer
/// enough for an attacker, a race still is, and closing it takes a change to <c>Repair</c> that is
/// not this one. Do not read a green suite as proof otherwise — the refusal's own call site could
/// only be tested through the two-argument <c>Prepare</c> overload, because no process can see a
/// directory it created itself as <see cref="DirectoryVerdict.UntrustedOwner"/>.
/// </para>
/// <para>
/// On Linux none of it is reachable: <c>/etc</c> is root-only, so planting takes root, and root
/// needs no planted token. That reasoning sits in <see cref="CredentialDirectory.Prepare"/>, beside
/// the silence which depends on it.
/// </para>
/// </remarks>
public static class CredentialProvisioning
{
    /// <summary>Provides the credentials following the precedence that was decided.</summary>
    /// <param name="configuredToken">The explicit token, if configured.</param>
    /// <param name="storePath">The path of the store.</param>
    /// <param name="runningAsService">Whether the process is registered as a system service.</param>
    /// <returns>The credentials and where they came from.</returns>
    /// <exception cref="InvalidOperationException">
    /// When it runs as a service and the store cannot be secured.
    /// </exception>
    public static ProvisionedCredentials Provision(
        string? configuredToken,
        string storePath,
        bool runningAsService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            // Explicit configuration wins over everything: it is the backwards compatibility, and
            // it is what keeps the tests and CI working.
            return new ProvisionedCredentials(
                new MachineCredentials(configuredToken.Trim(), null, null),
                CredentialOrigin.Configuration,
                null);
        }

        try
        {
            CredentialDirectory.Prepare(storePath);

            if (CredentialStore.Read(storePath) is { } stored)
            {
                return new ProvisionedCredentials(stored, CredentialOrigin.Stored, storePath);
            }

            MachineCredentials created = MachineCredentials.Create();
            CredentialStore.Write(storePath, created);

            return new ProvisionedCredentials(created, CredentialOrigin.CreatedAndStored, storePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (runningAsService)
            {
                throw new InvalidOperationException(RefusalMessage(storePath), error);
            }

            // Launched by hand. EPHEMERAL token, in memory, never written: never a per-user
            // fallback on disk, which would move the secret to a less protected place while
            // making it look like it had been put somewhere safe.
            return new ProvisionedCredentials(MachineCredentials.Create(), CredentialOrigin.Ephemeral, null);
        }
        catch (InvalidOperationException) when (!runningAsService && !IsStoreDamaged(storePath))
        {
            return new ProvisionedCredentials(MachineCredentials.Create(), CredentialOrigin.Ephemeral, null);
        }
    }

    /// <summary>A store that exists but cannot be interpreted must never be overwritten.</summary>
    private static bool IsStoreDamaged(string path)
    {
        try
        {
            return File.ReadAllText(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RefusalMessage(string path) =>
        $"Observer runs as a system service and can't secure its credential store at '{path}'. " +
        "It will not start: writing a machine token where other accounts can read it would be " +
        "worse than not starting at all, because nothing would report it. Check that the " +
        "directory is not a junction, that it is owned by SYSTEM or Administrators, and that " +
        "no other account is granted access.";
}