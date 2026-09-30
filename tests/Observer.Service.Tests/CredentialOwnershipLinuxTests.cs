using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Observer.Service.Credentials;

namespace Observer.Service.Tests;

/// <summary>Who owns the credential store after it is written, on Linux.</summary>
/// <remarks>
/// The .deb runs the service as its own account and creates /etc/observer for it, but
/// <c>sudo observer rotate-key</c> writes the store as ROOT, and a new file belongs to whoever
/// creates it. Measured under systemd: the rotation left a credentials.json owned by root inside
/// a directory owned by the service account, the service could not read it, and its next start
/// died on an unhandled exception, in a restart loop.
/// <para>
/// The owner is read with stat(1) and changed with chown(1), never with the code under test, so a
/// wrong assumption inside it cannot vouch for itself.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public class CredentialOwnershipLinuxTests : IDisposable
{
    private readonly string directory;

    public CredentialOwnershipLinuxTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-owner-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    private string StorePath => Path.Combine(directory, "credentials.json");

    [HandsOverOwnershipFact]
    public void TheStoreBelongsToTheOwnerOfItsDirectoryNotToWhoeverWroteIt()
    {
        string owner = Owners.Foreign ?? throw new InvalidOperationException(Owners.Unavailable);
        Run("chown", owner, directory);

        // What makes the assertions below mean something: left alone, a new file in here would NOT
        // get that owner (a setgid directory would hand it the group, and the test would pass
        // whether or not the code does anything).
        string probe = Path.Combine(directory, "probe");
        File.WriteAllText(probe, string.Empty);
        Assert.NotEqual(owner, OwnerOf(probe));
        File.Delete(probe);

        CredentialStore.Write(StorePath, MachineCredentials.Create());

        Assert.Equal(owner, OwnerOf(StorePath));

        // The case that broke is the REPLACEMENT of a store that is already there.
        CredentialStore.Write(StorePath, MachineCredentials.Create());

        Assert.Equal(owner, OwnerOf(StorePath));
    }

    [HandsOverOwnershipFact]
    public void ItActsOnTheOpenFileAndNeverFollowsAPath()
    {
        // The directory belongs to the service account and root works inside it, so a link put
        // where the file was just created must not be followed: a chown by path would hand
        // whatever it points at to the directory's owner, and that owner is the one attacking.
        string owner = Owners.Foreign ?? throw new InvalidOperationException(Owners.Unavailable);
        Run("chown", owner, directory);

        string decoy = Path.Combine(directory, "decoy");
        File.WriteAllText(decoy, "not the store");
        string before = OwnerOf(decoy);

        string path = Path.Combine(directory, "store.tmp");
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);

        // The swap the attack needs: the name now leads somewhere else, the open file is unlinked.
        File.Delete(path);
        File.CreateSymbolicLink(path, decoy);

        UnixOwnership.FollowDirectory(file);

        Assert.Equal(before, OwnerOf(decoy));

        // And the open file, which is what it acted on, did change hands. Without this the test
        // would pass just as well if FollowDirectory did nothing at all.
        string held = "/proc/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + "/fd/" + ((int)file.SafeFileHandle.DangerousGetHandle()).ToString(CultureInfo.InvariantCulture);

        Assert.Equal(owner, Run("stat", "-L", "-c", "%u:%g", held));
    }

    [NonRootFact]
    public void APlainAccountThatCannotHandTheStoreOverIsNotRefusedForIt()
    {
        // /tmp belongs to root and anyone can create files in it: a directory a plain account can
        // write to and cannot hand a file to the owner of. That account writes for ITSELF, and
        // refusing it would stop a write that works today. The store is left as its own, and the
        // temporary file is not left behind.
        string path = Path.Combine("/tmp", "obs-owner-" + Guid.NewGuid().ToString("N")[..10] + ".json");

        try
        {
            CredentialStore.Write(path, MachineCredentials.Create());

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".new"));
            Assert.StartsWith(Run("id", "-u") + ":", OwnerOf(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".new");
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string OwnerOf(string path) => Run("stat", "-c", "%u:%g", path);

    internal static string Run(string tool, params string[] arguments)
    {
        ProcessStartInfo start = new(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(tool + " did not start.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            tool + " " + string.Join(' ', arguments) + " exited with "
                + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + error);

        return output.Trim();
    }
}

/// <summary>The accounts a test process can hand a file to.</summary>
internal static class Owners
{
    /// <summary>
    /// A "uid:gid" different from what a new file would get, that this process is ALLOWED to give
    /// a file, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Root can give a file to anyone. Anyone else can only keep the uid and pick one of the
    /// groups it belongs to, which is enough: a store that follows its directory has to follow
    /// the group as well.
    /// </remarks>
    public static string? Foreign { get; } = FindForeign();

    /// <summary>Why a test that needs <see cref="Foreign"/> cannot run where there is none.</summary>
    public const string Unavailable =
        "This account can neither give a file to another owner (it is not root) nor to another "
        + "group (it belongs to only one): the ownership tests cannot run here.";

    private static string? FindForeign()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (Environment.IsPrivilegedProcess)
        {
            return "4242:4242";
        }

        string uid = CredentialOwnershipLinuxTests.Run("id", "-u");
        string primary = CredentialOwnershipLinuxTests.Run("id", "-g");

        string? other = CredentialOwnershipLinuxTests.Run("id", "-G")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(group => group != primary);

        return other is null ? null : uid + ":" + other;
    }
}

/// <summary>A fact that runs only where this process can hand a file to another owner.</summary>
public sealed class HandsOverOwnershipFactAttribute : FactAttribute
{
    /// <summary>Skips off Linux, and on a machine of ours whose account has a single group.</summary>
    /// <remarks>
    /// On GitHub Actions it does NOT skip for the second reason. A runner whose account can no
    /// longer hand a file to a group is a runner that changed, and a skipped test would say nothing
    /// while the fix went unexercised: it fails there instead, and says why.
    /// </remarks>
    public HandsOverOwnershipFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Ownership of files on Linux: run only on ubuntu-latest.";
        }
        else if (Owners.Foreign is null && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            Skip = Owners.Unavailable;
        }
    }
}

/// <summary>A fact that runs only for an unprivileged account on a Linux whose /tmp is not its own.</summary>
public sealed class NonRootFactAttribute : FactAttribute
{
    /// <summary>Skips as root, off Linux, or where /tmp already belongs to the running account.</summary>
    public NonRootFactAttribute()
    {
        if (!OperatingSystem.IsLinux()
            || Environment.IsPrivilegedProcess
            || CredentialOwnershipLinuxTests.Run("stat", "-c", "%u", "/tmp")
                == CredentialOwnershipLinuxTests.Run("id", "-u"))
        {
            Skip = "Needs an unprivileged Linux account and a /tmp owned by someone else.";
        }
    }
}