using System.Text;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// An endpoint URL written wrong does not fail: it fails WORSE.
/// </summary>
/// <remarks>
/// Measured: with "http://unix:C:\path\x.sock" Kestrel neither throws nor warns, it binds
/// [::]:80 on ALL interfaces and puts the machine's telemetry behind it. This function
/// exists to turn that silence into a refusal at start-up.
/// </remarks>
public class EndpointUrlTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5057")]
    [InlineData("https://0.0.0.0:7051")]
    [InlineData("http://localhost:5057")]
    [InlineData("http://unix:/run/observer/observer.sock")]
    [InlineData("http://pipe:/Observer")]
    public void ValidUrls_ReportNoProblem(string url) =>
        Assert.Null(EndpointUrl.Problem(url));

    [Theory]
    // The case that opened port 80 on all interfaces without saying anything.
    [InlineData(@"http://unix:C:\Users\user\AppData\Local\Temp\x.sock")]
    // A relative unix path: Kestrel rejects it at StartAsync, which is too late to work out why.
    [InlineData("http://unix:relative.sock")]
    // A pipe without the slash: the same trap as the Windows path.
    [InlineData("http://pipe:Observer")]
    [InlineData("http://pipe:/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void BrokenUrls_ExplainTheProblem(string url) =>
        Assert.False(string.IsNullOrWhiteSpace(EndpointUrl.Problem(url)));

    [Fact]
    public void SocketPathOf107Bytes_IsAccepted_108_IsNot()
    {
        // .NET's error message says "between 1 and 108 characters" and it LIES: it does not
        // count the NUL terminator. Measured by bisection: 107 passes, 108 throws
        // ArgumentOutOfRangeException. A guard written at 108 lets through exactly the
        // boundary case, which is the only one that matters.
        string a107 = "/" + new string('a', 106);
        string a108 = "/" + new string('a', 107);

        Assert.Equal(107, Encoding.UTF8.GetByteCount(a107));
        Assert.Equal(108, Encoding.UTF8.GetByteCount(a108));

        Assert.Null(EndpointUrl.Problem("http://unix:" + a107));
        Assert.NotNull(EndpointUrl.Problem("http://unix:" + a108));
    }

    [Fact]
    public void TheLimitIsCountedInBytesNotCharacters()
    {
        // A path of 81 characters, half of them accented, goes past 107 bytes in UTF-8.
        // Counting characters would let through a path the operating system rejects.
        string accented = "/" + new string('e', 40) + new string('\u00e8', 40);

        Assert.True(accented.Length <= 107);
        Assert.True(Encoding.UTF8.GetByteCount(accented) > 107);
        Assert.NotNull(EndpointUrl.Problem("http://unix:" + accented));
    }
}