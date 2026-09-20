using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Listening on the named pipe and the list of who may open it.
/// </summary>
/// <remarks>
/// A separate, annotated class because CA1416, with TreatWarningsAsErrors, fails the build on
/// BOTH runners: it is static analysis and does not depend on the OS that compiles. The
/// attribute on a local function is not honoured and does not cover the body of a lambda, so
/// this code cannot live in the top-level statements of Program.cs.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsNamedPipe
{
    /// <summary>Opens the listener on the pipe and configures its transport.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="pipeName">The name of the pipe, without prefix.</param>
    public static void Listen(WebApplicationBuilder builder, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        // UseNamedPipes is NOT needed to open the pipe: on Windows the transport is already
        // registered and ListenNamedPipe is enough on its own. It serves only these two options.
        builder.WebHost.UseNamedPipes(ConfigureTransport);

        // The protocol is named HERE and not left to the endpoint defaults, and the reason is on
        // ServiceLimits.Protocol: the defaults reach only endpoints declared after them, so
        // without this line the restriction would depend on where Program.cs happens to register
        // its callbacks. On THIS endpoint it changes nothing today - measured: a cleartext
        // endpoint refuses the HTTP/2 preface either way - so it is belt, kept because it costs
        // one argument and stops this endpoint depending on a default that could be narrowed.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenNamedPipe(pipeName, listen => listen.Protocols = ServiceLimits.Protocol));
    }

    /// <summary>Sets the transport's two options. Together, never just one.</summary>
    /// <param name="options">The named pipe transport options.</param>
    public static void ConfigureTransport(NamedPipeTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The two lines that follow must be kept ADJACENT and never separated.
        // Setting only PipeSecurity throws ArgumentException at start-up ("'pipeSecurity'
        // must be null when 'options' contains 'PipeOptions.CurrentUserOnly'"), and that is the
        // harmless case because it is noisy. Setting only CurrentUserOnly = false is the
        // dangerous one: the host starts normally and produces a pipe with DACL
        // (A;;FR;;;WD)(A;;FR;;;AN), that is readable by Everyone and by ANONYMOUS LOGON. No
        // error, no warning, no symptom.
        options.CurrentUserOnly = false;
        options.PipeSecurity = SecurityDescriptor();
    }

    /// <summary>The pipe's DACL: who may open it.</summary>
    /// <returns>The descriptor to apply to the transport.</returns>
    public static PipeSecurity SecurityDescriptor()
    {
        PipeSecurity security = new();

        // FullControl and not CreateNewInstance alone: the first instance is always created, and
        // it is from the SECOND that FILE_CREATE_PIPE_INSTANCE (0x4) is needed. Kestrel opens
        // more than one, and without that bit the bind fails with UnauthorizedAccessException,
        // which Kestrel translates into the misleading "address already in use".
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        if (current.User is { } account)
        {
            // When the service runs as LocalSystem this ACE coincides with the previous one;
            // when it runs launched by hand from a terminal, it is the only one that lets it
            // open its own pipe.
            security.AddAccessRule(new PipeAccessRule(
                account,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        // INTERACTIVE and NOT Authenticated Users: the latter includes every authenticated
        // principal able to reach the machine, even over SMB on port 445, and a named pipe is
        // exposed exactly there.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        // There is no need to order the ACEs by hand: PipeSecurity canonicalises, and a DENY
        // added last ends up at the head anyway (verified by comparing the two SDDLs, identical
        // character by character). The guarantee is however CommonAcl's and NOT our own
        // call's: importing a descriptor from SDDL or from binary form would leave the DENY
        // where it is and it would become inert. Always build with AddAccessRule, never import.
        return security;
    }
}