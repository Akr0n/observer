using System.Text;

namespace Observer.Service.Credentials;

/// <summary>Reads the files of the credential folder, strictly where the reader is root.</summary>
/// <remarks>
/// The strict read (<see cref="UnixSafeRead"/>) exists for one situation: root reading inside a
/// folder that ANOTHER account owns, which is what <c>sudo observer share</c> does in /etc/observer.
/// That account can put anything there, and root has to be the one that does not follow it. Every
/// other reader reads exactly as it always did, and that is the point of the scope rather than a
/// convenience:
/// <list type="bullet">
/// <item>the service is not root, reads its own folder, and is not the victim;</item>
/// <item>root in a folder that root owns has nobody to be protected from, and a store that is a link
/// (a secrets mount, say) works there today and has to go on working;</item>
/// <item>an owner nobody can name is not a mismatch, for the reason <see cref="UnixOwnership"/> gives;</item>
/// <item>Windows has its own rules for the folder and never gets here.</item>
/// </list>
/// The owner that counts is the DIRECTORY's and never the file's: the file is reached through a link
/// the attacker plants, and asking who owns THAT would answer "root" and switch the protection off
/// at the moment it is needed.
/// </remarks>
public static class StoreFile
{
    /// <summary>
    /// The <see cref="AppContext"/> switch that turns the strict read on for everyone. Nothing in the
    /// package sets it: it is for the tests, and for anyone who wants the protection without the
    /// scope. It can only make a read stricter.
    /// </summary>
    public const string StrictSwitch = "Observer.StrictStoreReads";

    /// <summary>Whether a reader with these properties must read strictly.</summary>
    /// <param name="privileged">Whether the reader is root.</param>
    /// <param name="directoryOwner">The uid that owns the file's DIRECTORY, or null if unknown.</param>
    /// <returns>True for root in a folder that somebody else owns.</returns>
    public static bool IsStrict(bool privileged, uint? directoryOwner) =>
        privileged && directoryOwner is { } owner && owner != 0;

    /// <summary>Whether reading this path must be strict, for the process as it is.</summary>
    /// <param name="path">The file about to be read.</param>
    /// <returns>True if the read must not follow a link or wait on the file.</returns>
    public static bool MustBeStrict(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return (AppContext.TryGetSwitch(StrictSwitch, out bool enabled) && enabled)
            || MustBeStrict(path, Environment.IsPrivilegedProcess);
    }

    /// <summary>Whether reading this path must be strict for a reader that is or is not root.</summary>
    /// <param name="path">The file about to be read.</param>
    /// <param name="privileged">Whether the reader is root.</param>
    /// <returns>True if the read must not follow a link or wait on the file.</returns>
    public static bool MustBeStrict(string path, bool privileged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Checked in this order so the service and Windows pay for nothing: no privilege, no question.
        if (!OperatingSystem.IsLinux() || !privileged)
        {
            return false;
        }

        // GetFullPath, so a relative path in the configuration is not silently left unprotected.
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        return directory is not null && IsStrict(privileged, UnixOwnership.OwnerOfPath(directory)?.Uid);
    }

    /// <summary>Reads a whole file, strictly where <see cref="MustBeStrict(string)"/> says so.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] ReadAllBytes(string path) =>
        OperatingSystem.IsLinux() && MustBeStrict(path)
            ? UnixSafeRead.ReadAllBytes(path)
            : File.ReadAllBytes(path);

    /// <summary>Reads a whole text file, strictly where <see cref="MustBeStrict(string)"/> says so.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, decoded the way <see cref="File.ReadAllText(string)"/> decodes it.</returns>
    public static string ReadAllText(string path)
    {
        if (!OperatingSystem.IsLinux() || !MustBeStrict(path))
        {
            return File.ReadAllText(path);
        }

        using MemoryStream stream = new(UnixSafeRead.ReadAllBytes(path));
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    /// <summary>Text that came from a file or a socket, with nothing in it that a terminal acts on.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, each control character shown as a question mark.</returns>
    /// <remarks>
    /// What the service account writes can end up in a message: the JSON parser quotes the name of
    /// a key unescaped, and the local channel says whatever it likes. An escape sequence can clear
    /// the screen, retitle the window or rewrite the lines above, in the terminal of whoever is
    /// root.
    /// </remarks>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Concat(text.Select(letter => char.IsControl(letter) ? '?' : letter));
    }
}