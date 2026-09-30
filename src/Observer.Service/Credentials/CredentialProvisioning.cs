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
/// the directory genuinely safe, so ANY next start sees a spotless directory and adopts the planted
/// file. The next start needs no attacker: an operator's own <c>Restart-Service</c> is enough, and so,
/// through the same code, is the start at boot of a service set to start automatically (reasoned; the
/// probe did not reboot). Windows did NOT retry a refused start by itself in the probe, although the
/// recovery actions the package configures were in place (see below), so the loop this paragraph
/// used to lean on was never there. The repair is what destroys the only evidence, so nothing after
/// it can be trusted to decide; the refusal has to be the thing that prevents it.
/// </para>
/// <para>
/// A store is REFUSED and never overwritten or moved aside. Overwriting would destroy a real
/// operator's token together with the evidence of an attempt, and quarantining it would silently cut
/// off every paired dashboard in the case where the file is genuine. An EMPTY directory is a
/// different question: nothing can be adopted from one, so it is REPLACED with a protected directory
/// and the service starts.
/// </para>
/// <para>
/// THAT DOES NOT MAKE SQUATTING HARMLESS, and an earlier draft of this paragraph claimed it did. The
/// emptiness check counts subdirectories as well as files, so a standard user who creates
/// <c>C:\ProgramData\Observer</c> AND puts anything at all inside it produces a non-empty directory
/// nothing vouches for, which is refused at every start, forever, with no privilege and no running
/// process. Only an EMPTY squat that SYSTEM can still list and remove is absorbed: an empty folder
/// whose owner took SYSTEM out of its permissions is refused too (measured on 2026-09-30, verdict
/// Unknown: it cannot be listed, so it counts as not empty). That is not an oversight to be fixed
/// later: refusing is the whole point, and a monitor held down by a refusal it explains is the
/// accepted price of not serving a key an attacker chose. What must not happen is claiming the class
/// is closed.
/// </para>
/// <para>
/// WHAT THE RULE COSTS, and the trigger is the DIRECTORY, never the files in it. Prepare looks at who
/// owns the folder and what its permissions say; <c>CredentialStore.Read</c> never looks at a file's
/// owner. So a <c>credentials.json</c> copied into the folder the service made itself is adopted
/// exactly as before, and what trips the rule is a folder made by another hand that is not empty:
/// recreated and restored into, restored from a backup without its permissions, or left by the
/// service run by hand under any account but SYSTEM, elevated or not. That last one is subtler than
/// it looks:
/// the descriptor the service writes names the running account, and LocalSystem trusts only
/// <c>S-1-5-18</c> and <c>S-1-5-32-544</c>, so the verdict is OpenDacl even when the owner is right.
/// The same happens to a machine whose service ran under a domain account and is re-registered as
/// LocalSystem by an MSI upgrade. A recreated folder inherits from <c>ProgramData</c>, which is
/// open. Trusted owners are SYSTEM and the Administrators GROUP: an individual administrator's own
/// account is not among them, and <c>takeown</c> without <c>/A</c> hands ownership to that account.
/// </para>
/// <para>
/// WHAT THE OPERATOR SEES, measured on 2026-09-30 with <c>scripts/probe-credential-directory.ps1</c>:
/// the real service as a throwaway LocalSystem service (manual start) on ONE machine — Windows 11
/// build 26300, Italian — with the recovery actions the package configures set by hand on it
/// (restart after five seconds, failure flag at its default). Provision runs before the host is
/// built, so there is no logger yet and the refusal escapes as an unhandled exception, which Windows
/// records in the Application log (source <c>.NET Runtime</c>, event 1026, the message inside). The
/// service never connects to the Service Control Manager, so <c>sc start</c> waits about 30 seconds
/// and ends with error 1053, and the System log says only that the service did not respond in time
/// (events 7000 and 7009): the reason was found in the Application log and in no other place the
/// probe looked. NO recovery action ran: in the roughly 40 seconds observed after that timeout there
/// was no second 1026 event and no 7031 or 7034 (the sampling of process ids saw no process at all,
/// so it cannot tell), so a start refused before the service connects leaves it stopped until
/// somebody starts it again, not in a restart loop. A process that dies AFTER it connected was not
/// tried. What is NOT measured either: the MSI. It starts the service and waits for it, so an upgrade
/// onto such a machine may stop with error 1920, which does not name the cause, and roll back to the
/// version that adopts; that needs an installed previous version in a disposable machine. The
/// way out is TWO steps and not one command — the owner, and the permissions — and the README, section
/// Packages, has the exact <c>icacls</c> lines. It is a real operational cost, and it is the price
/// of not adopting a key whose author cannot be named. What does NOT regress, checked: an MSI upgrade
/// of a folder the service made itself (Safe, and the folder survives the upgrade), and a disk clone
/// or machine rename (both trusted SIDs are well-known).
/// </para>
/// <para>
/// THE RACE IN THE EMPTY CASE IS CLOSED, and it took replacing the container rather than repairing
/// it: <see cref="WindowsDirectoryTrust.Replace"/> says how, and why no interleaving adopts anything.
/// What remains open is written there and in the two paragraphs below, and none of it is an adoption
/// path. Do not read a green suite as proof of any of this — the guard's own call site can only be
/// tested through the two-argument <c>Prepare</c> overload, because no process can see a directory it
/// created itself as <see cref="DirectoryVerdict.UntrustedOwner"/>.
/// </para>
/// <para>
/// WHAT IS STILL OPEN, all of it availability and none of it adoption. A standard user can create a
/// FILE directly at <c>C:\ProgramData\Observer</c> — measured, because ProgramData grants
/// <c>BUILTIN\Users</c> add-file without an inherit-only flag — and a file there is not a directory,
/// so the service refuses at every start. That one predates all of this and is not closed by it. An
/// EMPTY directory whose owner took SYSTEM out of its permissions is refused as well — measured: the
/// verdict is Unknown, and a directory the service cannot list counts as not empty — with no
/// privilege and no running process. An
/// attacker who keeps a process alive holding the directory open, or who re-sets the read-only bit in
/// a race, can also keep the service from starting; the passive versions of both are closed. And
/// because <c>Observer:CredentialStorePath</c> can point anywhere, replacing the container means
/// LocalSystem will remove an EMPTY untrusted directory at whatever path is configured — a new
/// destructive act in a file whose rule is never to delete a store, bounded to a directory the
/// operating system itself proved empty — and reachable not only at start-up, because
/// <c>CredentialSource.Reload</c> calls <c>Prepare</c> on every reload, so a running service can take
/// that action too.
/// </para>
/// <para>
/// On Linux none of it is reachable: <c>/etc</c> can be written only by root and the package creates
/// <c>/etc/observer</c> for the <c>observer</c> account the service runs as, so planting takes root or
/// that account, and neither needs a planted token. That reasoning sits in
/// <see cref="CredentialDirectory.Prepare"/>, beside the silence which depends on it.
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