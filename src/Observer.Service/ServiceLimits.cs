using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Observer.Service;

/// <summary>
/// What the service is willing to spend on a caller it has not authorized yet.
/// </summary>
/// <remarks>
/// Every limit here is spent BEFORE the access control runs: a connection is accepted, and a
/// request body received, by whoever can reach the port. The token decides what a caller may
/// READ; these three decide what a caller may COST. They are the only defence on the near side of
/// the 401, and this is a service whose whole job is to watch a machine - turning it into the
/// machine's problem is the one failure it must not have.
/// <para>
/// All three settings below were measured on this repository's own bench, and one of the three
/// measurements contradicted what its setting looked like it would do: the body limit does not
/// refuse a body at the door. Each measurement is recorded next to the value it justifies.
/// </para>
/// </remarks>
public static class ServiceLimits
{
    /// <summary>How many connections one endpoint accepts at once.</summary>
    /// <remarks>
    /// <para>
    /// IT IS A BUDGET PER ENDPOINT, NOT FOR THE WHOLE SERVICE, and that was measured rather than
    /// read: with the limit at 2 and two TCP listeners open, the third connection to the first
    /// listener is refused while the first connection to the second listener is served normally.
    /// The same holds across transports - a listener at its limit does not stop the named pipe.
    /// So the arithmetic that matters is this number TIMES the number of endpoints: 512 here means
    /// up to 1024 connections, because the service opens two, HTTPS and the local channel.
    /// </para>
    /// <para>
    /// That per-endpoint behaviour is what makes the limit worth setting: a flood on the port
    /// every other machine uses does not spend the local channel's budget, so it cannot lock the
    /// owner out of the machine they are sitting at. It does NOT make the local channel safe, and
    /// an earlier version of this remark claimed that it did. THE LOCAL CHANNEL HAS A BUDGET OF
    /// ITS OWN AND IT CAN BE SPENT BY WHOEVER CAN OPEN IT. On Windows that is every interactive
    /// user, because the pipe's DACL grants INTERACTIVE on purpose - so an ordinary standard user
    /// can hold 512 pipe connections and leave the administrator's elevated dashboard unable to
    /// connect, including for the kill that 0.23.0 exists to gate. Measured. Kestrel has no
    /// per-endpoint override of this limit, so the two endpoints cannot be given different
    /// budgets and there is nothing to tune. Accepted, because the alternative is no bound at all
    /// on the endpoint the whole LAN can reach, and because that same user could already exhaust
    /// memory instead - more slowly, and in plain view of the telemetry this service publishes.
    /// On Linux the exposure is far narrower: the socket is 0660 inside a 0750 directory owned by
    /// the service's user and group, so only that group can fill it.
    /// </para>
    /// <para>
    /// WHY 512, and the arithmetic is the one that was measured LAST, because the first two were
    /// wrong in the same direction. All figures are managed heap, taken with both ends of the
    /// connection inside the measuring process, so the server's own share is lower than each of
    /// them. An IDLE connection costs about 11 kB. A connection HOLDING AN UNFINISHED REQUEST -
    /// which is what a flood actually looks like, and what the first draft of this remark left
    /// out - costs about 31 kB with 29 kB of headers on the way in, roughly three times as much.
    /// So 512 bounds this endpoint at something under 16 MB, and the service's two endpoints at
    /// something under 32 MB. Bounded, and small next to the machine this service exists to
    /// watch. Note what the budget does NOT bound: it counts connections, not the buffers behind
    /// them. <c>MaxRequestBufferSize</c> is the knob for that and is deliberately left at its
    /// default here - lowering it was tried and measured, and
    /// <c>MaxRequestHeadersTotalSize = 8 kB</c> changed the cost by 1 %, because the bytes are
    /// buffered before they are parsed.
    /// </para>
    /// <para>
    /// The floor comes from the dashboard: see <see cref="DashboardConnectionsWhenBusy"/>. The
    /// number is deliberately not a knob: a limit that can be raised in a file is a limit that
    /// gets raised instead of understood, and there is no measured case that needs more.
    /// </para>
    /// <para>
    /// WHAT A REFUSAL LOOKS LIKE, at both ends, because neither is obvious. On the wire it is not
    /// an HTTP status at all - Kestrel accepts the connection and disposes it - so the dashboard
    /// reports <c>Unreachable</c>, which reads as "Service unreachable": a cable fault, with the
    /// wrong remedy. Nothing can be sent on a refused connection, so that cannot be improved from
    /// here. In the log it is one Warning per refusal from
    /// <c>Microsoft.AspNetCore.Server.Kestrel.Connections</c>, with no throttle of any kind, and
    /// that line is filtered OUT of the Windows event log in <c>appsettings.json</c> - see the
    /// comment there. The two facts belong together: the log line is the only signal that the
    /// budget was reached, and it is the one an attacker would otherwise use to evict the
    /// machine's event history.
    /// </para>
    /// </remarks>
    public const int MaxConcurrentConnectionsPerEndpoint = 512;

    /// <summary>What one busy dashboard is expected to hold against one machine.</summary>
    /// <remarks>
    /// It is enforced nowhere - it is the floor the budget above has to clear, written down so the
    /// test guarding that budget has something to compare against instead of a number somebody
    /// picked. The dashboard holds ONE connection per request in flight and caps nothing, so the
    /// figure is a PEAK CONCURRENCY and not a sum of everything it asks for: the main loop awaits
    /// the reading, then the history, then the process list, one after another, so those never
    /// overlap and the same pooled connection serves them in turn. What can genuinely be in
    /// flight together is the history fan-out - every strip at once, one connection per gauge
    /// row, six on a plain desktop and more on a host with many disks - plus the two things the
    /// loop does not await: the "while you were away" summary and a process read started by a
    /// click. Twenty is that, rounded up for a machine with a lot of disks.
    /// </remarks>
    public const int DashboardConnectionsWhenBusy = 20;

    /// <summary>How many bytes of request body the service will read. None.</summary>
    /// <remarks>
    /// <para>
    /// No endpoint reads a body. The one write the service has takes its arguments in the route and
    /// the query string - <c>POST /processes/{pid}/kill?name=NAME</c> - precisely so that it does
    /// not need one, and the dashboard sends that request with no content at all.
    /// </para>
    /// <para>
    /// WHAT THIS DOES AND WHAT IT DOES NOT. The limit is enforced when the body is READ, not when
    /// it is announced, and both halves of that were measured. Against an endpoint that reads the
    /// body, one byte draws a clean <c>413</c>. Against an endpoint that ignores it - which is
    /// every endpoint here - nothing is refused up front, and the same holds for every request the
    /// access control short-circuits with a 401, because that path never reads a body either. What
    /// happens instead is that Kestrel tears the connection down where it would otherwise have
    /// drained the body. That is the behaviour this limit is set FOR, and it is measured from the
    /// server's side: announcing a megabyte and sending a hundred bytes of it got the connection
    /// closed after 115 ms, where the default limit held it for 6 s before the minimum-data-rate
    /// guard fired; a 64 kB body sent in full got 2 ms, against a connection the default kept alive
    /// for the 30 s the measurement was willing to wait.
    /// </para>
    /// <para>
    /// The price, stated because it is real: a caller that sends a large body may never read the
    /// answer - it is still writing into a socket the service has stopped reading, and it sees a
    /// timeout rather than a status. That is acceptable HERE, and only here, because no endpoint
    /// takes a body and the only client is this project's own dashboard, which sends none. An
    /// endpoint that ever needs one must raise this per route, not remove the line.
    /// </para>
    /// </remarks>
    public const long MaxRequestBodySizeInBytes = 0;

    /// <summary>The only protocol any endpoint of this service speaks.</summary>
    /// <remarks>
    /// <para>
    /// Nothing here wants HTTP/2: the dashboard never asks for it - .NET's HttpClient offers 1.1
    /// unless told otherwise - and the one thing multiplexing would buy, many requests over one
    /// connection, is exactly what the connection budget above already pays for cheaply. What it
    /// costs is a second protocol implementation reachable before the token is checked: its own
    /// framing, its own header compression, its own stream bookkeeping, and a history of
    /// denial-of-service defects that live in precisely that bookkeeping.
    /// </para>
    /// <para>
    /// IT IS NAMED AT EVERY ENDPOINT THAT IS OPENED, and not left to <see cref="Apply"/>'s
    /// endpoint defaults alone. That is not belt and braces for its own sake: the defaults reach
    /// only endpoints declared AFTER them, so with the defaults alone the restriction would be an
    /// invariant of the ORDER in which <c>Program.cs</c> registers its callbacks - and moving one
    /// line, which is the most ordinary edit there is, silently handed both endpoints HTTP/2 back
    /// with the whole suite green. Measured, by doing it. Position must not carry security, so
    /// each <c>Listen</c> call names the protocol itself.
    /// </para>
    /// <para>
    /// WHAT IT ACTUALLY CHANGES IS NARROWER THAN IT LOOKS, and the narrowing was measured after
    /// the first draft of this comment claimed otherwise. On the HTTPS endpoint it drops "h2"
    /// from what the service advertises in the TLS handshake, so the protocol is not merely
    /// unused, it is not offered - and ALPN is the only place that is observable. On the local
    /// channel it changes NOTHING on the wire: the instinct is that a cleartext endpoint is where
    /// this matters, because a caller can reach HTTP/2 there by prior knowledge with no handshake
    /// to decline it in - but a control measurement says Kestrel already refuses that preface
    /// with GOAWAY and HTTP_1_1_REQUIRED, restriction or no restriction. The mixed default means
    /// "HTTP/2 if ALPN chooses it", and cleartext has no ALPN; h2c has to be asked for by name.
    /// So on the pipe and the socket this line is belt: it costs nothing, and it keeps those
    /// endpoints from depending on a framework default that could be narrowed later.
    /// </para>
    /// </remarks>
    public const HttpProtocols Protocol = HttpProtocols.Http1;

    /// <summary>Why a configured endpoint's protocol is not acceptable, or null if it is.</summary>
    /// <param name="protocols">The <c>Kestrel:Endpoints:NAME:Protocols</c> value, if there is one.</param>
    /// <returns>The sentence to refuse it with, or null.</returns>
    /// <remarks>
    /// A pure rule beside the endpoint URL's, tested the same way and for the same reason: an
    /// endpoint declared in configuration is the FOURTH way one can be opened here, and the only
    /// one that does not pass through a <c>Listen</c> call in this repository - so
    /// <see cref="Protocol"/> does not reach it. Measured: such an endpoint does inherit
    /// <see cref="Apply"/>'s endpoint default when it names no protocol, but its own key is
    /// applied AFTER that default and wins; and on a cleartext endpoint an explicit
    /// <c>Http2</c> IS then served, answering the connection preface with SETTINGS where
    /// <c>Http1</c> and the mixed default both answer GOAWAY. So this is the one place where
    /// configuration can put the second protocol implementation back within reach of a caller who
    /// has shown no token, and the service refuses to start instead.
    /// </remarks>
    public static string? ProblemWithConfiguredProtocol(string? protocols)
    {
        if (protocols is not { Length: > 0 }
            || string.Equals(protocols, nameof(HttpProtocols.Http1), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"it asks for Protocols '{protocols}'. This service speaks HTTP/1.1 only: HTTP/2 " +
            "brings a second protocol implementation within reach of callers the access control " +
            "has not admitted yet. Remove the line, or set it to " + nameof(HttpProtocols.Http1) + ".";
    }

    /// <summary>Applies the limits that belong to the server as a whole.</summary>
    /// <param name="kestrel">The server options.</param>
    /// <remarks>
    /// The endpoint default set here is the SECOND line of defence for <see cref="Protocol"/>: it
    /// covers an endpoint that forgets to name it, as long as that endpoint is declared after this
    /// runs. The first line is at each <c>Listen</c> call, and the reason there are two is written
    /// on <see cref="Protocol"/>. The two connection and body limits have no such hazard - they
    /// live on the server and apply from wherever they are set.
    /// </remarks>
    public static void Apply(KestrelServerOptions kestrel)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        kestrel.Limits.MaxConcurrentConnections = MaxConcurrentConnectionsPerEndpoint;
        kestrel.Limits.MaxRequestBodySize = MaxRequestBodySizeInBytes;
        kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = Protocol);
    }
}
