using System.Globalization;
using System.Text;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>The command-line verbs.</summary>
/// <remarks>
/// Three verbs and no parser: <c>System.CommandLine</c> is in beta, and a beta package under
/// TreatWarningsAsErrors is a risk that three verbs do not justify.
/// <para>
/// None of the three takes a secret as an ARGUMENT. That is not an accident: PowerShell's history
/// records the typed line, not the output, so a verb like <c>set-key &lt;secret&gt;</c> would
/// leave the key in a history file. Do not add one.
/// </para>
/// </remarks>
public static class Commands
{
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
            "rotate-key" => RotateKey(),
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

              observer doctor             Explain where the credential store is, how well it is
                                          protected, and what a client would see. Needs nothing.

              observer token set NAME     Keep ANOTHER machine's token here, so it stays out of
                                          machines.json. The token is read from standard input,
                                          never from the command line, and is not echoed when
                                          you type it.

              observer token forget NAME  Remove a machine's token from this computer.

            To watch THIS machine you need no token at all: the dashboard comes in through the
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
        Console.WriteLine("On the OTHER computer, put it in the Observer__ApiToken environment");
        Console.WriteLine("variable, or in the apiToken field of the dashboard's client.json.");
        Console.WriteLine();
        Console.WriteLine("You do NOT need this to watch the machine you are sitting at.");

        return 0;
    }

    private static int RotateKey()
    {
        string storePath = CredentialDirectory.DefaultPath();

        if (ReadCredentials(storePath) is not { } credentials)
        {
            return 1;
        }

        MachineCredentials rotated = credentials.Rotate(DateTimeOffset.UtcNow, MachineCredentials.GracePeriod);

        try
        {
            CredentialStore.Write(storePath, rotated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the credential store: " + error.Message);
            return 1;
        }

        Console.WriteLine("A new machine token has been written.");
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"The previous one keeps working until {DateTimeOffset.UtcNow + MachineCredentials.GracePeriod:u}, so remote"));
        Console.WriteLine("clients are not cut off at once. Update them before then.");
        Console.WriteLine();

        // The restart has to be spelled out, because otherwise you try the new key, it does not work, and you
        // conclude that rotation is broken. And the command depends on the system: printing one
        // that does not exist here would send the user looking for why it does not work.
        Console.WriteLine("The service keeps using the OLD key until it is restarted:");
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "    Restart-Service Observer"
            : "    sudo systemctl restart observer");

        // This verb does NOT print the token, and that is not an oversight: that way it stays
        // harmless to run where the output ends up in a log. But without the lines below whoever
        // runs it is left with a new key and no way to know where to read it, and that really
        // happened - to the person who had written the command.
        Console.WriteLine();
        Console.WriteLine("Then read the new token with \"observer share\", and hand it to the");
        Console.WriteLine("machines that watch this one with \"observer token set NAME\".");

        return 0;
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
        Console.WriteLine("To watch THIS machine you need no token: the dashboard comes in through");
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
            Console.Error.WriteLine("        The dashboard comes in through the local channel.");
            Console.Error.WriteLine("Detail: " + error.Message);

            return null;
        }
    }
}