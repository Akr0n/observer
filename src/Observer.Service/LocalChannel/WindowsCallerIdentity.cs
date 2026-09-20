using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Establishes whether the caller of a named pipe is genuinely local, and who it is.
/// </summary>
/// <remarks>
/// The question "am I local?" is NOT answered by looking at the transport: a named pipe is
/// reachable remotely over SMB on port 445. And it is not answered by looking at the token
/// either: towards the machine itself Windows returns the ORIGINAL interactive token, with the
/// same group SIDs as the local route, and the NETWORK SID absent in both cases.
/// <para>
/// It is answered with GetNamedPipeClientComputerName, which fails with ERROR_PIPE_LOCAL when
/// the connection is local and succeeds when it came through SMB. Measured on three routes:
/// local ".", remote network address, REMOTE "localhost". And it works even when the token is
/// not readable, that is to say precisely in the attack case.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class WindowsCallerIdentity
{
    /// <summary>ERROR_PIPE_LOCAL: the connection comes from the same machine, not from SMB.</summary>
    private const int ErrorPipeLocal = 229;

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientComputerName(nint pipe, ref byte name, uint sizeInBytes);

    /// <summary>Classifies the caller of the pipe.</summary>
    /// <param name="pipe">The stream of the connection in progress.</param>
    /// <returns>The caller's origin.</returns>
    public static CallerOrigin Classify(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        // A BYTE buffer and not a char one: char is not blittable and the [LibraryImport]
        // generator would demand DisableRuntimeMarshalling on the whole assembly. Here the
        // content is not needed, only knowing whether the call succeeds: 512 bytes are 256
        // UTF-16 characters, plenty for a machine name.
        Span<byte> buffer = stackalloc byte[512];

        bool succeeded = GetNamedPipeClientComputerName(
            pipe.SafePipeHandle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(buffer),
            (uint)buffer.Length);

        int win32Error = Marshal.GetLastWin32Error();

        if (succeeded || win32Error != ErrorPipeLocal)
        {
            // Succeeded: the connection came through SMB, and the buffer holds the caller's
            // name. Failed for a reason other than ERROR_PIPE_LOCAL: we cannot tell that it
            // is local, and when in doubt it is not.
            return new CallerOrigin(
                CallerKind.FromNetwork,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GetNamedPipeClientComputerName ok={succeeded} win32={win32Error}"));
        }

        return ReadIdentity(pipe);
    }

    private static CallerOrigin ReadIdentity(NamedPipeServerStream pipe)
    {
        SidCapture capture = new();

        try
        {
            pipe.RunAsClient(capture.Run);
        }
        catch (SecurityException ex)
        {
            // The ATTACK case: the client chose TokenImpersonationLevel.Anonymous and made
            // itself unilaterally unidentifiable. HRESULT 0x80070543,
            // ERROR_BAD_IMPERSONATION_LEVEL. Without this catch the service answers 500.
            return UnidentifiedOrigin(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return UnidentifiedOrigin(ex);
        }
        catch (IOException ex)
        {
            return UnidentifiedOrigin(ex);
        }

        if (capture.Sid is not { } sid)
        {
            return new CallerOrigin(CallerKind.Unidentified, null, "the caller token carried no user SID");
        }

        // The SID and the elevation go into the reason, and that is not decoration: the reason is
        // what every one of the kill's log lines already prints, so the audit trail for the only
        // action this service cannot undo arrives on all of them at once, instead of on whichever
        // one somebody remembers to change.
        return new CallerOrigin(
            CallerKind.LocalIdentified,
            sid,
            string.Create(
                CultureInfo.InvariantCulture,
                $"local caller {sid}, {(capture.Elevated ? "elevated" : "not elevated")}"),
            capture.Elevated ? CallerElevation.Yes : CallerElevation.No);
    }

    private static CallerOrigin UnidentifiedOrigin(Exception ex) =>
        new(
            CallerKind.Unidentified,
            null,
            string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}"));

    /// <summary>The body executed under impersonation.</summary>
    /// <remarks>
    /// An instance method of an annotated class, and NOT a lambda: [SupportedOSPlatform] does
    /// not cover the body of a lambda and CA1416 would fail the build. Passed to RunAsClient
    /// as a method group.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private sealed class SidCapture
    {
        public string? Sid { get; private set; }

        /// <summary>Whether the CALLER's token carries the administrators group.</summary>
        /// <remarks>
        /// Read here, inside the impersonated callback, and nowhere else: outside it
        /// <see cref="WindowsIdentity.GetCurrent()"/> is the SERVICE, which runs as LocalSystem
        /// and is therefore always an administrator - a check written one line lower would
        /// answer yes to everybody, silently and for ever.
        /// <para>
        /// <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/> asks what the token can
        /// DO, which is the question. A member of Administrators who has not elevated fails it,
        /// because UAC hands the process a token with the group filtered out, and that is
        /// intended: this service's one write is exactly the kind of thing that filter exists
        /// to withhold.
        /// </para>
        /// </remarks>
        public bool Elevated { get; private set; }

        public void Run()
        {
            using WindowsIdentity? caller = WindowsIdentity.GetCurrent(ifImpersonating: true);

            if (caller?.User?.Value is not { } sid)
            {
                return;
            }

            Sid = sid;
            Elevated = new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}