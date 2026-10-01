using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli.Tests;

/// <summary>What the operator reads when root refuses a file in the credential folder.</summary>
/// <remarks>
/// The words are code in every respect: they are what an administrator finds on the screen when a
/// command they ran with sudo does not do what they asked. The worst version of them blames the
/// terminal for something that is not the terminal's fault.
/// </remarks>
[Collection(StrictStoreReads.Name)]
public sealed class RefusedReadTests : IDisposable
{
    private const string Token = "GOODTOKEN";

    private readonly string directory;

    public RefusedReadTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "obs-refused-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
    }

    private string StorePath => Path.Combine(directory, CredentialDirectory.FileName);

    [Fact]
    public void ARefusalNamesTheFileTheReasonAndHowToLookAtIt()
    {
        StoreNotSafeToReadException refusal =
            new("/etc/observer/credentials.json", "is a FIFO, not a regular file");

        string sentence = Diagnosis.DescribeRefusal(refusal);

        Assert.Contains("The file '/etc/observer/credentials.json' is a FIFO, not a regular file.", sentence, StringComparison.Ordinal);
        Assert.Contains("sudo ls -l /etc/observer/credentials.json", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [OnLinuxFact]
    public void ReadCredentialsRefusesALinkAndDoesNotBlameTheTerminal()
    {
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        using StrictScope strict = new();

        TextWriter original = Console.Error;
        using StringWriter captured = new();
        Console.SetError(captured);

        MachineCredentials? result;

        try
        {
            result = Commands.ReadCredentials(StorePath);
        }
        finally
        {
            Console.SetError(original);
        }

        string text = captured.ToString();

        Assert.Null(result);
        Assert.Contains("Refusing to read the machine token.", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed.", text, StringComparison.Ordinal);
        Assert.Contains("sudo cat", text, StringComparison.Ordinal);
        Assert.Contains("sudo systemctl stop observer", text, StringComparison.Ordinal);
        Assert.Contains("NEW token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elevated terminal", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    [OnLinuxFact]
    public void ACertificateThroughALinkIsReportedNotFingerprinted()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using X509Certificate2 foreign = MachineCertificate.Create("foreign", now);
        string foreignFile = Path.Combine(directory, "foreign.pfx");
        File.WriteAllBytes(foreignFile, MachineCertificate.Export(foreign));
        File.CreateSymbolicLink(MachineCertificate.PathNextTo(StorePath), foreignFile);

        // The control, measured before the change: share and doctor printed the fingerprint of
        // whatever the link pointed at, as if it were this machine's certificate.
        Assert.Equal(
            CertificateFingerprint.ForHumans(MachineCertificate.Fingerprint(foreign)),
            Diagnosis.DescribeCertificate(StorePath));

        using StrictScope strict = new();

        string described = Diagnosis.DescribeCertificate(StorePath);

        Assert.StartsWith("NOT READ - ", described, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link", described, StringComparison.Ordinal);
    }

    [OnLinuxFact]
    public void RotatingAtOnceDoesNotNeedTheOldStoreAndSaysItWillBeReplaced()
    {
        // --now writes a store that has nothing of the old one in it (Replacement ignores it), so
        // a link or a FIFO in its place is exactly what the operator wants gone. Refusing to read
        // it there would leave a leaked key in force for the sake of a file nobody needs.
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        using StrictScope strict = new();

        (MachineCredentials? credentials, string output, string error) =
            Captured(() => Commands.ReadForRotation(StorePath, immediately: true));

        Assert.NotNull(credentials);
        Assert.DoesNotContain(Token, credentials.Current, StringComparison.Ordinal);
        Assert.Contains("Note: the old store was not read, and will be replaced.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, output + error, StringComparison.Ordinal);
    }

    [OnLinuxFact]
    public void RotatingGracefullyStillRefusesWhatItWouldHaveToCarryOver()
    {
        string real = Path.Combine(directory, "real.json");
        CredentialStore.Write(real, new MachineCredentials(Token, null, null));
        File.CreateSymbolicLink(StorePath, real);

        using StrictScope strict = new();

        (MachineCredentials? credentials, _, string error) =
            Captured(() => Commands.ReadForRotation(StorePath, immediately: false));

        Assert.Null(credentials);
        Assert.Contains("Refusing to read the machine token.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RotatingAtOnceReadsAnOrdinaryStoreAsItAlwaysDid()
    {
        CredentialStore.Write(StorePath, new MachineCredentials(Token, null, null));

        (MachineCredentials? credentials, string output, _) =
            Captured(() => Commands.ReadForRotation(StorePath, immediately: true));

        Assert.NotNull(credentials);
        Assert.Equal(Token, credentials.Current);
        Assert.DoesNotContain("Note:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this is not JSON {{{")]
    [InlineData("{\"current\":\"tok\\u001b[2Jen\",\"previous\":null}")]
    public void RotatingAtOnceDoesNotNeedWhatTheOldStoreHoldsEither(string content)
    {
        // The same rule as for a link: the new store has nothing of the old one in it, so a file
        // that cannot be parsed, or holds something that is not a token, is no reason to leave a
        // leaked key in force. Each of them said it needed no old store and then stopped on it.
        File.WriteAllText(StorePath, content);

        (MachineCredentials? credentials, string output, string error) =
            Captured(() => Commands.ReadForRotation(StorePath, immediately: true));

        Assert.NotNull(credentials);
        Assert.Contains("Note: the old store was not read, and will be replaced.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(output + error, letter => char.IsControl(letter) && letter != '\n' && letter != '\r');

        // The graceful form carries the old key over, so it still stops on both.
        (MachineCredentials? graceful, _, _) = Captured(() => Commands.ReadForRotation(StorePath, immediately: false));

        Assert.Null(graceful);
    }

    [UnprivilegedFact]
    [SupportedOSPlatform("linux")]
    public void RotatingAtOnceStillStopsOnAnAccessDeniedAndSaysToUseSudo()
    {
        // The reader reports a denied access as an InvalidOperationException that carries it, as
        // it does a store it cannot parse. The first is for the operator to fix, and the write
        // would fail for the same reason; the second is what --now replaces. The two must not be
        // confused, and neither may the advice: an account without the right is told to use sudo,
        // and not to delete a store that is perfectly fine.
        string locked = Path.Combine(directory, "locked");
        Directory.CreateDirectory(locked);
        string store = Path.Combine(locked, CredentialDirectory.FileName);
        File.WriteAllText(store, "{}");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            (MachineCredentials? credentials, string output, string error) =
                Captured(() => Commands.ReadForRotation(store, immediately: true));

            Assert.Null(credentials);
            Assert.DoesNotContain("Note:", output, StringComparison.Ordinal);
            Assert.Contains("run the same command with sudo", error, StringComparison.Ordinal);
            Assert.DoesNotContain("NEW token", error, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void WhenNothingWasRotatedTheTextSaysWhichKeyIsStillInForce()
    {
        string graceful = string.Join('\n', Commands.DescribeNothingRotated(immediately: false));
        string now = string.Join('\n', Commands.DescribeNothingRotated(immediately: true));

        Assert.Contains("Nothing was rotated", graceful, StringComparison.Ordinal);
        Assert.Contains("whichever key was in force still is", graceful, StringComparison.Ordinal);
        Assert.DoesNotContain("running service", graceful, StringComparison.Ordinal);

        // With --now the operator's reason was a leak, and "unchanged" must not read as "safe".
        Assert.Contains("the running service still accepts the key you meant to revoke", now, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tok\u001b[2Jen", null)]
    [InlineData("GOODTOKEN", "old\u001b]0;pwned\u0007")]
    public void AStoreHoldingAnEscapeSequenceIsRefusedAndNeverPrinted(string current, string? previous)
    {
        // A regular file of the right size and owner can still hold bytes that a terminal acts on,
        // and "share" prints the token to the terminal of whoever is root.
        CredentialStore.Write(
            StorePath,
            new MachineCredentials(current, previous, previous is null ? null : DateTimeOffset.UtcNow.AddHours(1)));

        (MachineCredentials? credentials, string output, string error) =
            Captured(() => Commands.ReadCredentials(StorePath));

        Assert.Null(credentials);
        Assert.Contains("holds something that is not a token", error, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed.", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output + error);
        Assert.DoesNotContain('\u0007', output + error);

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("sudo cat -v " + StorePath, error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"current\":null,\"previous\":null}")]
    public void AStoreWithNoKeyInItIsReadAsItAlwaysWasAndNeverCrashesTheGuard(string content)
    {
        // The JSON is valid and the record has no required member, so Current can be null. The
        // check for control characters must not be the thing that turns that into a stack trace.
        File.WriteAllText(StorePath, content);

        (MachineCredentials? credentials, _, string error) = Captured(() => Commands.ReadCredentials(StorePath));

        Assert.NotNull(credentials);
        Assert.DoesNotContain("not a token", error, StringComparison.Ordinal);
    }

    [OnLinuxFact]
    public async Task AFifoInTheCertificatesPlaceIsReportedAndNothingWaitsOnIt()
    {
        string certificate = MachineCertificate.PathNextTo(StorePath);
        Tool.Run("mkfifo", certificate);

        using StrictScope strict = new();

        string described = await Patience.Patiently(() => Diagnosis.DescribeCertificate(StorePath), certificate);

        Assert.StartsWith("NOT READ - ", described, StringComparison.Ordinal);
        Assert.Contains("FIFO", described, StringComparison.Ordinal);
    }

    private static (T Result, string Output, string Error) Captured<T>(Func<T> run)
    {
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();
        Console.SetOut(output);
        Console.SetError(error);

        try
        {
            T result = run();

            return (result, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}