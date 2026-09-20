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
    /// That per-endpoint behaviour is not a detail, it is the reason this limit is safe to set at
    /// all. A flood on the port every other machine uses cannot lock the owner out of the local
    /// channel, which is how they watch the machine they are sitting at and, since 0.23.0, how they
    /// stop a process on it. Had the budget been shared, the cheapest denial of service would have
    /// been to make the machine's own dashboard unreachable.
    /// </para>
    /// <para>
    /// WHY 512. One idle plain-HTTP connection was measured at about 13 kB of managed memory on
    /// the server. The TLS figure that was isolated is weaker and is quoted as what it is: about
    /// 30 kB for a connection whose BOTH ends lived in the measuring process, so the server's own
    /// share is somewhere below that, and a flood at this limit costs at most about 15 MB per
    /// endpoint - an upper bound that includes the attacker's half. Bounded either way, and small
    /// next to the service it protects. The floor comes from the dashboard: see
    /// <see cref="DashboardConnectionsWhenBusy"/>.
    /// </para>
    /// <para>
    /// The number is deliberately not a knob: a limit that can be raised in a file is a limit that
    /// gets raised instead of understood, and there is no measured case that needs more.
    /// </para>
    /// </remarks>
    public const int MaxConcurrentConnectionsPerEndpoint = 512;

    /// <summary>What one busy dashboard is expected to hold against one machine.</summary>
    /// <remarks>
    /// It is enforced nowhere - it is the floor the budget above has to clear, written down so the
    /// test guarding that budget has something to compare against instead of a number somebody
    /// picked. The dashboard holds ONE connection per request in flight and caps nothing: a
    /// history refresh starts every strip at once, one request per gauge row - six on a plain
    /// desktop, more on a host with many disks - and on top of that sit the once-a-second reading,
    /// the process list while its panel is open, and the "while you were away" summary. Twenty is
    /// that, rounded up.
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
