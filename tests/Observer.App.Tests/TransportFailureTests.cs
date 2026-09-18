using System.Net.Sockets;
using System.Security.Authentication;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Why the connection did not succeed, when it does not.
/// </summary>
/// <remarks>
/// Two faults that look alike on the wire and nothing alike in the room. A <b>refused</b>
/// connection comes back at once, and says something precise: the machine is there and it is
/// reachable, it is the service that is not listening on that port. A <b>timeout</b> says the
/// opposite: nobody answered, and the commonest cause is something dropping the packets
/// without saying so.
/// <para>
/// The remedies are opposite — starting a service versus opening a port — and as long as the
/// dashboard called them both "Service unreachable" whoever was looking had to guess. It cost
/// a real afternoon on a domain-joined machine, where the home network was classified as
/// public and the firewall rule applied to another profile.
/// </para>
/// </remarks>
public class TransportFailureTests
{
    [Fact]
    public void ARefusedConnectionIsRecognized()
    {
        SocketException socket = new((int)SocketError.ConnectionRefused);

        // Guard: if this line ever failed, the rest of the test would be measuring something
        // else and would pass or fail for the wrong reason.
        Assert.Equal(SocketError.ConnectionRefused, socket.SocketErrorCode);

        Assert.Equal(
            ServiceOutcome.ConnectionRefused,
            TransportFailure.Classify(new HttpRequestException("refused", socket)));
    }

    [Fact]
    public void ATimeoutOnTheSocketIsRecognized()
    {
        HttpRequestException failure = new("timed out", new SocketException((int)SocketError.TimedOut));

        Assert.Equal(ServiceOutcome.TimedOut, TransportFailure.Classify(failure));
    }

    [Fact]
    public void TheClientTimeoutArrivesAsACancellationAndIsStillATimeout()
    {
        // When HttpClient.Timeout expires no SocketException arrives: HttpClient cancels its
        // own request, and what you see is an OperationCanceledException with a
        // TimeoutException inside. Anything that looked only at the socket would never find it.
        TaskCanceledException expired = new("canceled", new TimeoutException());

        Assert.Equal(ServiceOutcome.TimedOut, TransportFailure.Classify(expired));
    }

    [Fact]
    public void TheSocketIsFoundDeepInTheChain()
    {
        // .NET does not deliver the SocketException at the first level: it wraps it in an
        // IOException and that in an HttpRequestException. Looking only at InnerException would
        // be enough today and would stop being enough at the first runtime change.
        HttpRequestException deep = new(
            "refused",
            new IOException(
                "connection reset",
                new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(ServiceOutcome.ConnectionRefused, TransportFailure.Classify(deep));
    }

    [Fact]
    public void ANameThatDoesNotResolveDoesNotBecomeARefusal()
    {
        // A wrong name is neither a service that is down nor a firewall: saying "the service is
        // not running" would send you looking on a machine that does not exist.
        HttpRequestException unresolved = new("unknown name", new SocketException((int)SocketError.HostNotFound));

        Assert.Equal(ServiceOutcome.Unreachable, TransportFailure.Classify(unresolved));
    }

    [Fact]
    public void ATlsFailureDoesNotBecomeARefusal()
    {
        // A fingerprint that does not match has an outcome of its own, decided before reaching
        // here. If this classifier took it over, a changed certificate — that is, a
        // reinstallation or somebody in the middle — would read as "service down".
        HttpRequestException tls = new("handshake", new AuthenticationException("certificate"));

        Assert.Equal(ServiceOutcome.Unreachable, TransportFailure.Classify(tls));
    }

    [Fact]
    public void AFailureWithNoSocketStaysGeneric()
    {
        Assert.Equal(
            ServiceOutcome.Unreachable,
            TransportFailure.Classify(new HttpRequestException("something went wrong")));
    }

    [Fact]
    public void AChainWithNoSocketDoesNotHangTheClassifier()
    {
        // Defensive, but the cost of getting it wrong is an interface that hangs instead of
        // showing an error: the walk down the chain must have a bottom in any case.
        InvalidOperationException inner = new("inner");
        HttpRequestException outer = new("outer", inner);

        Assert.Equal(ServiceOutcome.Unreachable, TransportFailure.Classify(outer));
    }
}