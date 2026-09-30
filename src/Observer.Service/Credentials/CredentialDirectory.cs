namespace Observer.Service.Credentials;

/// <summary>Where the store lives, and how its directory is secured.</summary>
public static class CredentialDirectory
{
    /// <summary>The name of the store's file.</summary>
    public const string FileName = "credentials.json";

    // 0700: the owner only, which in production is the "observer" account the service runs as -
    // the package creates the folder for it in postinst. .NET offers no chown, but none is
    // needed: whoever creates the folder owns it.
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The default path of the store on this system.</summary>
    /// <returns>The full path of the file.</returns>
    public static string DefaultPath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Observer",
                FileName)
            : Path.Combine("/etc", "observer", FileName);

    /// <summary>Brings the store's directory into a state where it can hold a secret.</summary>
    /// <param name="filePath">The path of the store's file.</param>
    /// <exception cref="InvalidOperationException">If that is not possible.</exception>
    /// <remarks>
    /// The two branches do not answer the same number of questions, and that asymmetry is the
    /// point rather than an omission. On Windows the directory's trust has to be established
    /// (<see cref="WindowsDirectoryTrust.Prepare"/>), because any standard user can create a
    /// subdirectory of <c>C:\ProgramData</c> and owns it. On Linux nothing is asked, because
    /// <c>/etc</c> is owned by root and mode 0755: nobody but root can create <c>/etc/observer</c>,
    /// the package creates it in a postinst already running as root, and hands it to the
    /// <c>observer</c> account, a <c>--system</c> user with <c>nologin</c> and no home, which is
    /// also the account the service runs as. A file found in there was put there by root or by
    /// that account, and neither needs to plant a token: root owns the machine already, and the
    /// account IS the service.
    /// <para>
    /// The ceiling of that reasoning, named because the silence on this branch hides it: it is a
    /// claim about <c>/etc</c>, not about any directory. Pointed by
    /// <c>Observer:CredentialStorePath</c> at a directory other accounts can write, the Linux
    /// branch checks no owner that would notice. Configuring the secret store somewhere
    /// world-writable is an operator error the shipped path does not have.
    /// </para>
    /// </remarks>
    public static void Prepare(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? directory = Path.GetDirectoryName(filePath);

        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsDirectoryTrust.Prepare(directory);
            return;
        }

        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsLinux())
        {
            // Creation does NOT apply the mode to a directory that already exists: verified, it
            // is a silent no-op. Without this second line the protection would not exist from the
            // second start on, nor on a directory prepared by an installer.
            File.SetUnixFileMode(directory, DirectoryMode);
        }
    }
}