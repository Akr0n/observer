using System.Net;
using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Who asks for compression and who does not, which is the client half of the decision.
/// </summary>
/// <remarks>
/// The encoding is NEGOTIATED per request: a service that compresses and a client that does not
/// ask for it exchange exactly the bytes they did before. So the two halves stand together or
/// not at all, and this test exists because the client half is invisible - no screen changes, no
/// number moves, and deleting it would make nothing else fail.
/// </remarks>
public class ClientCompressionTests
{
    private const string FakeFingerprint =
        "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    [Fact]
    public void OnTheWireCompressionIsRequested()
    {
        // This is the path that pays for the bytes: the raw history tail weighs 76 kB at one
        // hour and 114 at twenty-four, once per gauge.
        using SocketsHttpHandler handler = new CertificatePinning(FakeFingerprint).Handler();

        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }

    [Fact]
    public void OnTheLocalChannelItIsNotAskedForOnPurpose()
    {
        // On the pipe (or on the unix socket) the bytes cross nothing. Asking for it there would
        // mean making the machine this program IS MEASURING compress and decompress, that is,
        // paying CPU that ends up in the number on screen to save bytes that do not exist. And
        // the exclusion needs no branch in the service: the service compresses only what it is
        // asked for, so it is enough not to ask.
        using SocketsHttpHandler handler = LocalChannelHandler.Create();

        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }
}