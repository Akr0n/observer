using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>The answers the <c>doctor</c> verb prints, one after another.</summary>
public static class Diagnosis
{
    /// <summary>How well protected the store is, in one sentence.</summary>
    /// <param name="storePath">The path of the store.</param>
    /// <returns>The verdict, with the reason inside it.</returns>
    public static string DescribeProtection(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        if (Path.GetDirectoryName(storePath) is not { Length: > 0 } directory)
        {
            return "UNKNOWN - the store path has no directory.";
        }

        if (!OperatingSystem.IsWindows())
        {
            return Directory.Exists(directory)
                ? "the directory exists; on Linux the mode is enforced at 0700 by the service."
                : "ABSENT - the service has not created it yet.";
        }

        DirectoryVerdict verdict = WindowsDirectoryTrust.VerdictFor(directory);

        if (verdict == DirectoryVerdict.Unknown && Directory.Exists(directory))
        {
            // This is what a WELL PROTECTED store shows to any account, and it is the most
            // common case of all: reading a directory's permissions requires a permission on
            // that directory, and a properly protected store grants none.
            return
                "UNREADABLE FROM HERE - the directory exists but this account can't read its " +
                "permissions. That is what a correctly protected store looks like from an " +
                "ordinary account, and it rules out the common failure: a directory that merely " +
                "inherits its parent's permissions is readable by every user on the machine. " +
                "It does NOT prove the owner is right. Run this from an elevated terminal for a " +
                "definitive verdict.";
        }

        return DescribeVerdict(verdict);
    }

    /// <summary>The machine certificate's fingerprint, in one sentence.</summary>
    /// <param name="storePath">The path of the token store.</param>
    /// <returns>The readable fingerprint, or the reason it cannot be seen.</returns>
    /// <remarks>
    /// It tells the three cases apart the way <see cref="DescribeProtection"/> does, and for the
    /// same reason: "I cannot read it from here" and "it does not exist" call for two different
    /// actions, and confusing them sends the reader hunting for a fault that is not there.
    /// </remarks>
    public static string DescribeCertificate(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        string certificatePath = MachineCertificate.PathNextTo(storePath);

        try
        {
            // LoadForInspection and not Load: what is needed here is the fingerprint, not a
            // certificate to serve with, and Load would leave the private key in the key store
            // of whoever ran the command.
            using X509Certificate2 certificate =
                MachineCertificate.LoadForInspection(File.ReadAllBytes(certificatePath));

            return CertificateFingerprint.ForHumans(MachineCertificate.Fingerprint(certificate));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // "I did not find it" is not "it is not there". On a directory this account cannot
            // even list - that is, on a correctly protected store - the two are
            // indistinguishable from the outside, and passing the first off as the second
            // would send the reader hunting for a fault that does not exist.
            return CanListDirectory(certificatePath)
                ? "not created yet - the service writes it the first time it starts."
                : "NOT VISIBLE FROM HERE - this account can't list the directory, so this is " +
                  "not proof that the certificate is missing. Run this from an elevated terminal.";
        }
        catch (UnauthorizedAccessException)
        {
            return "UNREADABLE FROM HERE - run this from an elevated terminal.";
        }
        catch (CryptographicException error)
        {
            return "DAMAGED - " + error.Message;
        }
    }

    /// <summary>Whether this account can even list the store's directory.</summary>
    private static bool CanListDirectory(string certificatePath)
    {
        try
        {
            string directory = Path.GetDirectoryName(certificatePath) ?? ".";

            _ = Directory.EnumerateFileSystemEntries(directory).GetEnumerator().MoveNext();

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Who is running this command.</summary>
    /// <returns>The account name.</returns>
    public static string CurrentAccountName() =>
        OperatingSystem.IsWindows() ? WindowsAccountName() : Environment.UserName;

    /// <summary>Whether the command is running with administrative privileges.</summary>
    /// <returns>"True" or "False": Administrators on Windows, root elsewhere.</returns>
    public static string ElevatedAsText() =>
        OperatingSystem.IsWindows()
            ? IsElevatedOnWindows().ToString()
            : (Environment.UserName == "root").ToString();

    /// <summary>The sentence that explains a verdict to whoever is reading the screen.</summary>
    /// <param name="verdict">The outcome of examining the directory.</param>
    /// <returns>The sentence shown on screen.</returns>
    public static string DescribeVerdict(DirectoryVerdict verdict) => verdict switch
    {
        DirectoryVerdict.Safe =>
            "PROTECTED - owned by SYSTEM or Administrators, and nobody else is granted access.",
        DirectoryVerdict.Missing =>
            "ABSENT - the service has not created it yet. Start it once.",
        DirectoryVerdict.OpenDacl =>
            "NOT PROTECTED - other accounts on this machine can read it. Anyone who reads it " +
            "gets permanent access to this machine FROM THE NETWORK.",
        DirectoryVerdict.UntrustedOwner =>
            "FALSELY PROTECTED - the permissions name only SYSTEM and Administrators, but the " +
            "OWNER is an ordinary account, and an owner can grant itself access again whenever " +
            "it likes. This looks safe and is not.",
        DirectoryVerdict.ReparsePoint =>
            "HIJACKED - the path is a junction or symbolic link, so the token would be written " +
            "wherever it points. A standard user can create one without any privilege. Remove it.",
        _ => "UNKNOWN - the directory can't be examined from here. Try an elevated terminal.",
    };

    [SupportedOSPlatform("windows")]
    private static string WindowsAccountName()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return identity.Name;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedOnWindows()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}