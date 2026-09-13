using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Observer.Service.Credentials;

/// <summary>Creates the store's file on Windows, already with the right DACL.</summary>
/// <remarks>
/// A separate class, annotated for CA1416, which with TreatWarningsAsErrors fails the build on
/// both runners.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsCredentialFile
{
    /// <summary>Creates a new file with a protected DACL.</summary>
    /// <param name="path">The path of the file to create.</param>
    /// <returns>The stream to write to.</returns>
    public static Stream CreateProtected(string path) =>
        new FileInfo(path).Create(
            // CreateNew and not Create: on an already existing file, Create IGNORES the descriptor
            // passed in and leaves the one that was there in place. The call succeeds with no
            // error, and the secret ends up inside a DACL chosen by somebody else.
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.Synchronize,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            SecurityDescriptor());

    /// <summary>The store's DACL.</summary>
    /// <returns>The descriptor.</returns>
    /// <remarks>
    /// SYSTEM and the administrators, plus the account that RUNS this process. In production the
    /// service runs as LocalSystem and that third rule coincides with the first, so it grants
    /// nothing new; launched by hand during development it is what lets the service read its own
    /// store back instead of finding the door shut in its face.
    /// </remarks>
    public static FileSecurity SecurityDescriptor()
    {
        FileSecurity security = new();

        // Cuts inheritance: the system directory that hosts the store grants BUILTIN\Users
        // inheritable read access, and inheriting is enough to lose the secret.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        if (current.User is { } account)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }
}