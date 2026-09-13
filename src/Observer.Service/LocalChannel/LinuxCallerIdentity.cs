using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Identifica il chiamante di un socket unix leggendo le credenziali del peer.</summary>
/// <remarks>
/// La mappatura .NET di SO_PEERCRED non esiste, quindi si passa da GetRawSocketOption con i
/// valori numerici. Sono valori di LINUX, non di POSIX: su altri unix cambiano.
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxCallerIdentity
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>struct ucred = { int32 pid; uint32 uid; uint32 gid; }, 12 byte.</summary>
    private const int UcredBytes = 12;

    /// <summary>Classify il chiamante del socket.</summary>
    /// <param name="socket">Il socket accettato.</param>
    /// <returns>L'origine del chiamante.</returns>
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

        // MemoryMarshal.Read e NON BinaryPrimitives.Read*LittleEndian: la struct e' in ordine
        // NATIVO, e forzare little-endian sarebbe sbagliato su una macchina big-endian.
        uint uid = MemoryMarshal.Read<uint>(buffer[4..]);

        return new CallerOrigin(
            CallerKind.LocalIdentified,
            uid.ToString(CultureInfo.InvariantCulture),
            "local caller identified by SO_PEERCRED");
    }
}