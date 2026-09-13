using System.Runtime.Versioning;
using System.Text;

namespace Observer.Core.Security;

/// <summary>
/// The tokens of remote machines, in files readable only by their owner.
/// </summary>
/// <remarks>
/// On Linux there is no system store that is always there: the GNOME or the KDE keyring is on a
/// graphical session and not on a machine reached over SSH, and making it a dependency would
/// mean the dashboard does not start where that service is not running. A <c>0600</c> file is
/// the same level of protection an SSH private key lives with, and it is the one the service
/// already uses for its own token.
/// <para>
/// The difference that counts against <c>machines.json</c> is not only the file mode: it is
/// that here the permissions are <b>checked on read</b>, and a file someone else can read makes
/// the read fail instead of working in silence. A wrong permission that breaks nothing is a
/// wrong permission that stays there for ever.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class UnixSecretStore : ISecretStore
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode FolderOwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>What nobody else must be able to do on a secret's file.</summary>
    private const UnixFileMode Others =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string folder;

    /// <summary>Creates the store in the given folder.</summary>
    /// <param name="folder">Where to keep the secrets, or null for the default path.</param>
    public UnixSecretStore(string? folder = null) =>
        this.folder = folder ?? DefaultPath();

    /// <summary>The secrets folder under the user's profile.</summary>
    /// <returns>The path.</returns>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Observer",
        "secrets");

    /// <inheritdoc />
    public string Description => "files readable only by their owner, under " + folder;

    /// <inheritdoc />
    public bool TryRead(string name, out string secret)
    {
        secret = string.Empty;

        string path = PathFor(name);

        if (!File.Exists(path))
        {
            return false;
        }

        Verify(path);

        secret = File.ReadAllText(path, Encoding.UTF8).Trim();

        return secret.Length > 0;
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        string path = PathFor(name);

        Directory.CreateDirectory(folder, FolderOwnerOnly);

        // The folder may already exist from before, with wider inherited permissions:
        // CreateDirectory does not fix them, and a secret inside a folder others can traverse is
        // protected only until someone tries.
        File.SetUnixFileMode(folder, FolderOwnerOnly);

        // Write to a temporary file and then move it: overwriting in place would leave the old
        // secret truncated halfway if the process dies, and recreating it would leave a window
        // with no secret. The mode is passed at CREATION, so the file never exists with wider
        // permissions.
        string temporary = path + ".nuovo";

        using (FileStream stream = new(temporary, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = OwnerOnly,
        }))
        {
            stream.Write(Encoding.UTF8.GetBytes(secret));
            stream.Flush(flushToDisk: true);
        }

        File.SetUnixFileMode(temporary, OwnerOnly);
        File.Move(temporary, path, overwrite: true);
    }

    /// <inheritdoc />
    public bool Delete(string name)
    {
        string path = PathFor(name);

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);

        return true;
    }

    private static void Verify(string path)
    {
        UnixFileMode mode = File.GetUnixFileMode(path);

        if ((mode & Others) != 0)
        {
            throw new SecretStoreException(
                $"The token file {path} is readable by someone other than you ({mode}). " +
                $"Observer will not use it. Fix it with: chmod 600 \"{path}\"");
        }

        string? parent = Path.GetDirectoryName(path);

        if (parent is null)
        {
            return;
        }

        UnixFileMode parentMode = File.GetUnixFileMode(parent);

        // The folder counts as much as the file: whoever can write inside it can replace the
        // file with one of their own, and from that moment the dashboard would present remote
        // machines a token chosen by someone else.
        if ((parentMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new SecretStoreException(
                $"The folder {parent} is writable by others ({parentMode}), so the token " +
                $"inside it can be replaced. Observer will not use it. Fix it with: " +
                $"chmod 700 \"{parent}\"");
        }
    }

    private string PathFor(string name) => Path.Combine(folder, SecretName.Validate(name));
}