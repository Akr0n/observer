using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Identifies the caller of a unix socket by reading the peer's credentials.</summary>
/// <remarks>
/// There is no .NET mapping of SO_PEERCRED, so it goes through GetRawSocketOption with the
/// numeric values. They are LINUX values, not POSIX ones: on other unixes they change.
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxCallerIdentity
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>struct ucred = { int32 pid; uint32 uid; uint32 gid; }, 12 bytes.</summary>
    private const int UcredBytes = 12;

    /// <summary>Classifies the socket's caller.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <returns>The caller's origin.</returns>
    public static CallerOrigin Classify(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        Span<byte> buffer = stackalloc byte[UcredBytes];

        try
        {
            int bytesWritten = socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer);

            if (bytesWritten != UcredBytes)
            {
                return new CallerOrigin(
                    CallerKind.Unidentified,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"SO_PEERCRED returned {bytesWritten} bytes instead of {UcredBytes}"));
            }
        }
        catch (SocketException ex)
        {
            return new CallerOrigin(
                CallerKind.Unidentified,
                null,
                string.Create(CultureInfo.InvariantCulture, $"SO_PEERCRED failed: {ex.SocketErrorCode}"));
        }

        // MemoryMarshal.Read and NOT BinaryPrimitives.Read*LittleEndian: the struct is in
        // NATIVE order, and forcing little-endian would be wrong on a big-endian machine.
        uint uid = MemoryMarshal.Read<uint>(buffer[4..]);

        return new CallerOrigin(
            CallerKind.LocalIdentified,
            uid.ToString(CultureInfo.InvariantCulture),
            "local caller identified by SO_PEERCRED");
    }
}