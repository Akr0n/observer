using System.Globalization;
using System.Text;
using Observer.Core.Security;
using Observer.Service;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>The command-line verbs.</summary>
/// <remarks>
/// Four verbs, two flags and no parser: <c>System.CommandLine</c> is in beta, and a beta package
/// under TreatWarningsAsErrors is a risk this surface does not justify. The flags are matched by
/// presence rather than position, which is why <c>--now</c> and <c>--stdout</c> are looked for in
/// the whole argument list.
/// <para>
/// NONE of them takes a secret as an ARGUMENT. That is not an accident: PowerShell's history
/// records the typed line, not the output, so a verb like <c>set-key &lt;secret&gt;</c> would
/// leave the key in a history file. Do not add one.
/// </para>
/// </remarks>
public static class Commands
{
    /// <summary>The port the service listens on by default, taken from its own options.</summary>
    /// <remarks>
    /// Read from <see cref="NetworkOptions"/> rather than written again here. It is used for one
    /// thing only - telling a stopped service apart from one whose local channel is switched off
    /// - and a number that drifted from the service's would turn that into the wrong sentence,
    /// which is the one failure this whole path exists to avoid.
    /// </remarks>
    private const int DefaultHttpsPort = NetworkOptions.DefaultPort;

    /// <summary>Runs the requested verb.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string verb = args.Length > 0 ? args[0] : "help";

        return verb switch
        {
            "share" => Share(args.Contains("--stdout", StringComparer.Ordinal)),
            "rotate-key" => RotateKey(args.Contains("--now", StringComparer.Ordinal)),
            "token" => Token(args),
            "doctor" => Doctor(),
            "help" or "--help" or "-h" => PrintHelp(0),
            _ => PrintHelp(2),
        };
    }

    private static int PrintHelp(int exitCode)
    {
        Console.WriteLine("""
            observer — manage this machine's Observer service.

              observer share [--stdout]   Show the machine token, so ANOTHER computer can query
                                          this one. Needs an elevated terminal.
                                          --stdout prints only the token, with no trailing
                                          newline, for use in scripts.

              observer rotate-key         Replace the machine token. The previous one keeps
                                          working for 24 hours so remote clients are not cut off
                                          at once. Needs an elevated terminal.

              observer rotate-key --now   The same, for a token that has LEAKED: the old one
                                          stops working immediately and is not left on disk.
                                          Every machine watching this one is cut off until you
                                          run "observer token set NAME" there.

              observer doctor             Explain where the credential store is, how well it is
                                          protected, and what a client would see. Needs nothing.

              observer token set NAME     Keep ANOTHER machine's token here, so it stays out of
                                          machines.json. The token is read from standard input,
                                          never from the command line, and is not echoed when
                                          you type it.

              observer token forget NAME  Remove a machine's token from this computer.

            To watch THIS machine you need no token at all: the dashboard connects through the
            local channel. The token exists only so another computer can query this one.
            """);

        return exitCode;
    }

    /// <summary>Keeps, or forgets, ANOTHER machine's token.</summary>
    /// <remarks>
    /// It exists because without a command the token has to be written into a file by hand,
    /// which is exactly the thing being removed. The token comes from standard input and not
    /// from the arguments, for the same reason given at the top of this class: the typed line
    /// ends up in the shell's history, and on Unix in "ps" too.
    /// </remarks>
    private static int Token(string[] args)
    {
        if (args.Length < 3)
        {
            return PrintHelp(2);
        }

        if (args.Length > 3 && args[1] is "set" or "forget")
        {
            // A name with a space in it arrives as two arguments. Taking the first one kept the
            // token under the wrong name without a word, and the dashboard then reported the
            // real name's token missing - suggesting the very same command.
            Console.Error.WriteLine(
                "Too many words after \"" + args[1] + "\": a machine name with a space in it must " +
                "be quoted, for example: observer token " + args[1] + " \"My Laptop\"");

            return 2;
        }

        ISecretStore store = SecretStores.ForThisMachine();

        try
        {
            return args[1] switch
            {
                "set" => StoreToken(store, args[2]),
                "forget" => ForgetToken(store, args[2]),
                _ => PrintHelp(2),
            };
        }
        catch (SecretStoreException error)
        {
            Console.Error.WriteLine(error.Message);

            return 1;
        }
    }

    private static int StoreToken(ISecretStore store, string machineName)
    {
        string token = ReadTokenFromConsole();

        if (token.Length == 0)
        {
            Console.Error.WriteLine("No token was given, so nothing was stored.");

            return 1;
        }

        store.Write(machineName, token);

        Console.WriteLine($"The token for {machineName} is now kept in {store.Description}.");
        Console.WriteLine(
            $"If machines.json still has an \"apiToken\" line for {machineName}, delete it: " +
            "Observer refuses to use a token from that file.");

        return 0;
    }

    private static int ForgetToken(ISecretStore store, string machineName)
    {
        Console.WriteLine(store.Delete(machineName)
            ? $"The token for {machineName} is gone from this computer."
            : $"There was no token for {machineName} here.");

        return 0;
    }

    /// <summary>Reads the token without showing it, when there is someone typing it.</summary>
    /// <returns>The token, trimmed of leading and trailing whitespace.</returns>
    /// <remarks>
    /// Hiding the echo is not for show: a terminal keeps what it printed, so a token shown while
    /// it is pasted stays in the window's scrollback and in every copy of what was on screen.
    /// With the input redirected there is nobody to protect and the line is just read, nothing
    /// more.
    /// </remarks>
    private static string ReadTokenFromConsole()
    {
        if (Console.IsInputRedirected)
        {
            return (Console.In.ReadLine() ?? string.Empty).Trim();
        }

        Console.Write("Paste that machine's token (it will not be shown): ");

        StringBuilder typedToken = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typedToken.Length > 0)
                {
                    typedToken.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typedToken.Append(key.KeyChar);
            }
        }

        Console.WriteLine();

        return typedToken.ToString().Trim();
    }

    private static int Share(bool tokenOnly)
    {
        string storePath = CredentialDirectory.DefaultPath();

        if (ReadCredentials(storePath) is not { } credentials)
        {
            return 1;
        }

        if (tokenOnly)
        {
            // Write and not WriteLine, on purpose: if the output is captured into a shell
            // variable, a trailing newline would become part of the value, and the constant-time
            // comparison would reject it byte by byte.
            Console.Out.Write(credentials.Current);
            return 0;
        }

        Console.WriteLine("Machine token for this computer:");
        Console.WriteLine();
        Console.WriteLine("    " + credentials.Current);
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("Certificate fingerprint of this computer:");
        Console.WriteLine();
        Console.WriteLine("    " + Diagnosis.DescribeCertificate(storePath));
        Console.WriteLine();
        Console.WriteLine("Both values are needed, and they do different jobs: the token says the");
        Console.WriteLine("caller is allowed in, the fingerprint says this machine is the one it");
        Console.WriteLine("claims to be. Without the second, anyone able to stand in the middle");
        Console.WriteLine("presents their own certificate and collects the token.");
        Console.WriteLine();
        Console.WriteLine("On the OTHER computer, add this machine to the dashboard's machines.json:");
        Console.WriteLine("a name of your choice, its address as https://HOST:5058/ and the");
        Console.WriteLine("fingerprint above - never the token. Then run \"observer token set NAME\"");
        Console.WriteLine("there with that same name, paste the token when it asks, and reopen the");
        Console.WriteLine("dashboard. machines.json is in %LOCALAPPDATA%\\Observer on Windows and in");
        Console.WriteLine("~/.local/share/Observer on Linux.");
        Console.WriteLine();
        Console.WriteLine("You do NOT need this to watch the machine you are sitting at.");

        return 0;
    }

    /// <summary>Replaces the machine token, gracefully or immediately.</summary>
    /// <param name="immediately">True for <c>--now</c>: the old key stops working at once.</param>
    /// <returns>The exit code. Zero only when the old key is provably accepted nowhere here.</returns>
    /// <remarks>
    /// The two forms answer two different situations and it is worth being exact about which.
    /// The graceful one is for a key you are tired of: the previous key stays valid for a day so
    /// the machines watching this one are not cut off while nobody is looking. <c>--now</c> is
    /// for a key that has LEAKED, and it does two things the other does not - it leaves NO
    /// previous key, so the leaked secret is not even written back to disk, and it makes the
    /// RUNNING service adopt the new store instead of leaving it to a restart somebody has to
    /// remember during an incident.
    /// </remarks>
    private static int RotateKey(bool immediately)
    {
        string storePath = CredentialDirectory.DefaultPath();

        if (ReadCredentials(storePath) is not { } credentials)
        {
            return 1;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MachineCredentials replacement = Replacement(credentials, immediately, now);

        try
        {
            CredentialStore.Write(storePath, replacement);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the credential store: " + error.Message);
            return 1;
        }

        Console.WriteLine(immediately
            ? "A new machine token has been written, and the previous one is gone."
            : "A new machine token has been written.");
        Console.WriteLine();

        if (immediately)
        {
            Console.WriteLine("Every machine that watches this one is cut off until you run");
            Console.WriteLine("\"observer token set NAME\" there with the new token.");
        }
        else
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"The previous one keeps working until {now + MachineCredentials.GracePeriod:u}, so remote"));
            Console.WriteLine("clients are not cut off at once. Update them before then.");
        }

        Console.WriteLine();

        int exitCode = immediately ? TellTheRunningService(storePath) : DescribeTheRestart();

        // This verb does NOT print the token, and that is not an oversight: that way it stays
        // harmless to run where the output ends up in a log. But without the lines below whoever
        // runs it is left with a new key and no way to know where to read it, and that really
        // happened - to the person who had written the command.
        Console.WriteLine();
        Console.WriteLine("Then read the new token with \"observer share\", and hand it to the");
        Console.WriteLine("machines that watch this one with \"observer token set NAME\".");

        return exitCode;
    }

    /// <summary>What replaces the credentials, for each of the two kinds of rotation.</summary>
    /// <param name="current">What is in the store now.</param>
    /// <param name="immediately">True for <c>--now</c>.</param>
    /// <param name="now">The instant of the rotation.</param>
    /// <returns>The credentials to write.</returns>
    /// <remarks>
    /// Public, and pure, so the one line that decides whether a LEAKED secret stays on disk can
    /// be read off a table instead of inferred from a verb that also writes files and talks to a
    /// service.
    /// <para>
    /// <see cref="MachineCredentials.Create"/> and NOT <c>Rotate(now, TimeSpan.Zero)</c>, which
    /// is the shape that looks equivalent. A zero grace leaves the leaked key in the file with an
    /// expiry in the past: a compromised secret written back to disk for no benefit at all, and
    /// one that is still accepted at the exact instant of its expiry, because the comparison that
    /// admits the previous key is inclusive.
    /// </para>
    /// </remarks>
    public static MachineCredentials Replacement(
        MachineCredentials current, bool immediately, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        return immediately
            ? MachineCredentials.Create()
            : current.Rotate(now, MachineCredentials.GracePeriod);
    }

    /// <summary>The graceful path: the service picks the new key up when it is restarted.</summary>
    /// <returns>Always zero: nothing was promised that has not happened.</returns>
    /// <remarks>
    /// The restart has to be spelled out, because otherwise you try the new key, it does not
    /// work, and you conclude that rotation is broken. The command depends on the system:
    /// printing one that does not exist here would send the reader looking for why.
    /// </remarks>
    private static int DescribeTheRestart()
    {
        Console.WriteLine("The service keeps using the OLD key until it is restarted:");
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "    Restart-Service Observer"
            : "    sudo systemctl restart observer");

        return 0;
    }

    /// <summary>The immediate path: make the RUNNING service adopt the store, and prove it did.</summary>
    /// <param name="storePath">The store that was just written.</param>
    /// <returns>Zero only if the old key is provably accepted nowhere on this machine.</returns>
    /// <remarks>
    /// The proof is the stamp. The service reports when the file it read had last been written,
    /// and this compares it with the stamp of the file it just wrote: equal means the running
    /// process read those exact bytes. "The service said OK" would not be the same claim - it
    /// would not rule out a second service, a different store path, or a token that came from
    /// configuration and was never going to change.
    /// </remarks>
    private static int TellTheRunningService(string storePath)
    {
        DateTimeOffset written = File.GetLastWriteTimeUtc(storePath);

        LocalChannelAnswer answer = LocalChannelRequest.Post(
            "credentials/reload",
            LocalChannelProbe.DefaultPipeName,
            LocalChannelProbe.DefaultSocketPath,
            TimeSpan.FromSeconds(10));

        // Only asked when the local channel said nothing, because that is the only case where it
        // changes the answer - and it costs a connection attempt nobody needs otherwise.
        bool listening = answer.Silent
            && LocalChannelRequest.SomethingIsListeningOn(DefaultHttpsPort, TimeSpan.FromSeconds(1));

        RevocationVerdict verdict = Revocation.Judge(
            answer, storePath, written, Revocation.Read(answer.Body), listening);

        Console.WriteLine("Store   : " + storePath);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Written : {written:u}"));
        Console.WriteLine("Service : " + verdict.Headline);

        if (verdict.Advice.Length > 0)
        {
            Console.WriteLine("          " + verdict.Advice);
        }

        return verdict.ExitCode;
    }

    private static int Doctor()
    {
        string storePath = CredentialDirectory.DefaultPath();

        Console.WriteLine("Credential store: " + storePath);
        Console.WriteLine("Protection      : " + Diagnosis.DescribeProtection(storePath));
        Console.WriteLine("Local channel   : " + LocalChannelProbe.Probe(LocalChannelProbe.DefaultPipeName, TimeSpan.FromSeconds(3)));
        Console.WriteLine("Certificate     : " + Diagnosis.DescribeCertificate(storePath));
        Console.WriteLine("Running as      : " + Diagnosis.CurrentAccountName());
        Console.WriteLine("Elevated        : " + Diagnosis.ElevatedAsText());
        Console.WriteLine();
        Console.WriteLine("To watch THIS machine you need no token: the dashboard connects through");
        Console.WriteLine("the local channel. The token exists only so another computer can query this one.");

        return 0;
    }

    private static MachineCredentials? ReadCredentials(string storePath)
    {
        try
        {
            if (CredentialStore.Read(storePath) is { } credentials)
            {
                return credentials;
            }

            Console.Error.WriteLine("There is no credential store at " + storePath + ".");
            Console.Error.WriteLine("Start the Observer service once: it creates one on first run.");

            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Can't read the machine token: an elevated terminal is required.");
            Console.Error.WriteLine("Store : " + storePath);
            Console.Error.WriteLine("Why   : the file grants access only to SYSTEM and to local");
            Console.Error.WriteLine("        administrators. That is deliberate — this token is");
            Console.Error.WriteLine("        valid FROM THE NETWORK and does not expire.");
            Console.Error.WriteLine("You   : " + Diagnosis.CurrentAccountName() + ", elevated: " + Diagnosis.ElevatedAsText());
            Console.Error.WriteLine("Fix   : reopen the terminal with 'Run as administrator'.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Note  : to watch THIS machine you need no token at all.");
            Console.Error.WriteLine("        The dashboard connects through the local channel.");
            Console.Error.WriteLine("Detail: " + error.Message);

            return null;
        }
    }
}