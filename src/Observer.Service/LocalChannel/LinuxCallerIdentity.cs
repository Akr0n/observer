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
    private const int ByteDiUcred = 12;

    /// <summary>Classifica il chiamante del socket.</summary>
    /// <param name="presa">Il socket accettato.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(Socket presa)
    {
        ArgumentNullException.ThrowIfNull(presa);

        Span<byte> buffer = stackalloc byte[ByteDiUcred];

        try
        {
            int scritti = presa.GetRawSocketOption(SolSocket, SoPeerCred, buffer);

            if (scritti != ByteDiUcred)
            {
                return new CallerOrigin(
                    CallerKind.NonIdentificabile,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"SO_PEERCRED returned {scritti} bytes instead of {ByteDiUcred}"));
            }
        }
        catch (SocketException ex)
        {
            return new CallerOrigin(
                CallerKind.NonIdentificabile,
                null,
                string.Create(CultureInfo.InvariantCulture, $"SO_PEERCRED failed: {ex.SocketErrorCode}"));
        }

        // MemoryMarshal.Read e NON BinaryPrimitives.Read*LittleEndian: la struct e' in ordine
        // NATIVO, e forzare little-endian sarebbe sbagliato su una macchina big-endian.
        uint uid = MemoryMarshal.Read<uint>(buffer[4..]);

        return new CallerOrigin(
            CallerKind.LocaleIdentificato,
            uid.ToString(CultureInfo.InvariantCulture),
            "local caller identified by SO_PEERCRED");
    }
}