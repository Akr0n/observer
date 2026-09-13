using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Preparation and cleanup of the unix socket.</summary>
/// <remarks>
/// The order is constrained: validation, directory and cleanup BEFORE building the host; the
/// mode of the socket file AFTER start-up, because before that the file does not exist.
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxUnixSocket
{
    // 0750: the owner enters and administers, the group traverses. Not 0700, which would shut
    // the GUI out; not 0755, which would open it to anyone with an account on the machine.
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    // 0660: connect(2) on AF_UNIX requires the WRITE bit, not the read one. A mode granting
    // the group read only would shut out exactly whoever has to get in.
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>Creates the socket directory and forces the right mode on it.</summary>
    /// <param name="path">The full path of the socket.</param>
    public static void PreparePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directoryPath = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directoryPath))
        {
            return;
        }

        Directory.CreateDirectory(directoryPath, DirectoryMode);

        // The previous line does NOT apply the mode to a directory that already exists:
        // measured, it is a silent no-op. Without this second line the protection does not
        // exist from the second start onwards, nor on a /run/observer created by systemd with
        // its own 0755.
        File.SetUnixFileMode(directoryPath, DirectoryMode);
    }

    /// <summary>Deletes the socket file ONLY if nobody is listening.</summary>
    /// <param name="path">The path of the socket.</param>
    /// <param name="timeout">How long to wait for the probe's answer.</param>
    /// <returns>True if the file was removed.</returns>
    public static async Task<bool> RemoveStaleSocketAsync(string path, TimeSpan timeout)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource deadline = new(timeout);

        try
        {
            // ConnectAsync with a timeout and NOT Connect(): against a live listener whose
            // accept queue is full, connect(2) on AF_UNIX does not refuse, it waits. Measured:
            // over twenty seconds hanging without deciding either alive or dead, which under
            // systemd becomes a start-up timeout with no diagnosis at all.
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token)
                .ConfigureAwait(false);

            // Someone answered: the socket is alive, and deleting it would snatch it away
            // from a healthy instance.
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            // The probe timed out: we do not know if it is alive. When in doubt, do NOT delete.
            return false;
        }
    }

    /// <summary>Forces the mode of the socket file. To be called AFTER the host starts.</summary>
    /// <param name="path">The path of the socket.</param>
    public static void RestrictToOwner(string path) =>
        File.SetUnixFileMode(path, SocketMode);

    /// <summary>Registers the mode restriction for when the host has started.</summary>
    /// <param name="lifetime">The application's lifetime.</param>
    /// <param name="path">The path of the socket.</param>
    /// <remarks>
    /// It lives HERE and not in Program.cs because the lambda's body must sit inside an
    /// annotated class: [SupportedOSPlatform] does not cover a lambda's body, and CA1416 with
    /// TreatWarningsAsErrors would fail the build on both runners.
    /// Before start-up the socket file does not exist yet, so the chmod cannot sit next to the
    /// creation of the directory.
    /// </remarks>
    public static void RestrictAfterStart(IHostApplicationLifetime lifetime, string path)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        lifetime.ApplicationStarted.Register(() => RestrictToOwner(path));
    }
}