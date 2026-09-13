namespace Observer.Service.Credentials;

/// <summary>Where the store lives, and how its directory is secured.</summary>
public static class CredentialDirectory
{
    /// <summary>The name of the store's file.</summary>
    public const string FileName = "credentials.json";

    // 0700: the owner only, which in production is root because the service runs as root.
    // .NET offers no chown, but none is needed: the owner is right by construction.
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