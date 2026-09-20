using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Observer.Service;

/// <summary>
/// What the service is willing to spend on a caller it has not authorized yet.
/// </summary>
/// <remarks>
/// Every limit here is spent BEFORE the access control runs: a connection is accepted, and a
/// request body is received, by whoever can reach the port. The token decides what a caller may
/// READ; these three decide what a caller may COST. They are the only defence on this side of the
/// 401, and this is a service whose whole job is to watch a machine - turning it into the machine's
/// problem is the one failure it must not have.
/// <para>
/// All three numbers below were measured on this repository's own bench, and one of the three
/// measurements contradicted the setting it was meant to confirm. They are recorded next to the
/// value they justify.
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
    /// WHY 512. One idle connection was measured at about 13 kB of managed memory in plain HTTP and
    /// about 30 kB over TLS with both ends of the connection living in the measuring process, so
    /// the server's own share of a flood at this limit is in the region of 7 to 15 MB per endpoint:
    /// bounded, and small next to the service it protects. The floor comes from the dashboard,
    /// which holds ONE connection per request in flight and caps nothing: a history refresh starts
    /// every strip at once - one request per gauge row, so six on this machine and more on a host
    /// with many disks - on top of the once-a-second reading, the process list and the "while you
    /// were away" summary. Call it twenty connections for a dashboard that is working hard, which
    /// leaves room for far more dashboards than anyone will point at one machine. The number is
    /// deliberately not a knob: a limit that can be raised in a file is a limit that gets raised
    /// instead of understood, and there is no measured case that needs more.
    /// </para>
    /// </remarks>
    public const int MaxConcurrentConnectionsPerEndpoint = 512;

    /// <summary>How many bytes of request body the service will accept. None.</summary>
    /// <remarks>
    /// <para>
    /// No endpoint reads a body. The one write the service has takes its arguments in the route and
    /// the query string - <c>POST /processes/{pid}/kill?name=NAME</c> - precisely so that it does
    /// not need one, and the dashboard sends that request with no content at all.
    /// </para>
    /// <para>
    /// WHAT THIS DOES AND WHAT IT DOES NOT. The limit is enforced when the body is READ, not when
    /// it is announced, and both halves of that were measured. Against an endpoint that reads the
    /// body, one byte draws a clean <c>413</c>. Against an endpoint that ignores it - which is every
    /// endpoint here, and also every request the access control short-circuits with a 401 - nothing
    /// is refused up front: what happens instead is that Kestrel tears the connection down when it
    /// would otherwise have drained the body. That is the behaviour this limit is set FOR, and it
    /// is measured from the server's side: announcing a megabyte and sending a hundred bytes of it
    /// got the connection closed after 115 ms, where the default limit held it for 6 s before the
    /// minimum-data-rate guard fired; a 64 kB body sent in full got 2 ms, against a connection the
    /// default kept alive for the 30 s the measurement was willing to wait.
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

    /// <summary>Applies the three limits. Must run BEFORE anything opens an endpoint.</summary>
    /// <param name="kestrel">The server options.</param>
    /// <remarks>
    /// The ordering constraint is real and belongs to
    /// <see cref="KestrelServerOptions.ConfigureEndpointDefaults"/>: it applies to endpoints
    /// declared AFTER it, so a <c>Listen</c> call registered earlier keeps the framework's default
    /// of HTTP/1.1 and HTTP/2. Registering this first in <c>Program.cs</c> is what makes it cover
    /// the local channel as well as HTTPS, and the measurement confirms it reaches both: over TCP
    /// and over the named pipe the served protocol comes back HTTP/1.1, and a caller that demands
    /// HTTP/2 with prior knowledge is refused with <c>HTTP_1_1_REQUIRED</c> rather than left
    /// hanging.
    /// <para>
    /// WHY HTTP/1.1 ONLY. Nothing here wants HTTP/2: the dashboard never asks for it - .NET's
    /// HttpClient offers 1.1 unless told otherwise - and the one thing multiplexing would buy,
    /// many requests over one connection, is exactly what the connection budget above already
    /// pays for cheaply. What it costs is a second protocol implementation reachable before the
    /// token is checked: its own framing, its own header compression, its own stream bookkeeping,
    /// and a history of denial-of-service defects that live in precisely that bookkeeping. On the
    /// HTTPS endpoint this also drops "h2" from what the service advertises in the TLS handshake,
    /// so the protocol is not merely unused, it is not offered.
    /// </para>
    /// </remarks>
    public static void Apply(KestrelServerOptions kestrel)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        kestrel.Limits.MaxConcurrentConnections = MaxConcurrentConnectionsPerEndpoint;
        kestrel.Limits.MaxRequestBodySize = MaxRequestBodySizeInBytes;
        kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = HttpProtocols.Http1);
    }
}
