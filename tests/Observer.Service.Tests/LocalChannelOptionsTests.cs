using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// The local channel options refuse to start with unusable values.
/// </summary>
/// <remarks>
/// The pipe name and the socket path are CONFIGURABLE, and that is not a convenience: an
/// endpoint that fails to bind brings down the WHOLE host, the TCP endpoint included. With fixed
/// values, launching the service by hand on a machine where the installed one is running would
/// no longer fail "only on the port": it would not start at all.
/// </remarks>
public class LocalChannelOptionsTests
{
    [Fact]
    public void TheDefaultValuesAreValid()
    {
        LocalChannelOptions options = new();

        options.Validate();

        Assert.True(options.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.PipeName));
        Assert.False(string.IsNullOrWhiteSpace(options.SocketPath));
    }

    [Fact]
    public void ASocketPathThatIsTooLongIsRejected()
    {
        // The limit is 107 bytes. Validation must fire at start-up and not in StartAsync, where
        // it would take the TCP endpoint down with it.
        LocalChannelOptions options = new()
        {
            SocketPath = "/" + new string('a', 200) + "/observer.sock",
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("107", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeSocketPathIsRejected()
    {
        LocalChannelOptions options = new() { SocketPath = "observer.sock" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AnEmptyPipeNameIsRejected()
    {
        LocalChannelOptions options = new() { PipeName = "   " };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void NothingIsValidatedWhenTheChannelIsDisabled()
    {
        // A machine that does not want the local channel must not have to invent a valid path
        // just to be able to start.
        LocalChannelOptions options = new()
        {
            Enabled = false,
            PipeName = string.Empty,
            SocketPath = "not-absolute",
        };

        options.Validate();
    }
}