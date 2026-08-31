using System.Globalization;
using System.Text;
using Observer.Core.Security;
using Observer.Service.Credentials;

namespace Observer.Cli;

/// <summary>I verbi della riga di comando.</summary>
/// <remarks>
/// Tre verbi e nessun parser: <c>System.CommandLine</c> e' in beta, e un pacchetto in beta sotto
/// TreatWarningsAsErrors e' un rischio che tre verbi non giustificano.
/// <para>
/// Nessuno dei tre prende un segreto come ARGOMENTO. Non e' un caso: la cronologia di PowerShell
/// registra la riga digitata, non l'output, quindi un verbo del tipo <c>set-key &lt;segreto&gt;</c>
/// lascerebbe la chiave in un file di cronologia. Non aggiungerne uno.
/// </para>
/// </remarks>
public static class Comandi
{
    /// <summary>Esegue il verbo richiesto.</summary>
    /// <param name="args">Gli argomenti della riga di comando.</param>
    /// <returns>Il codice di uscita.</returns>
    public static int Esegui(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string verbo = args.Length > 0 ? args[0] : "help";

        return verbo switch
        {
            "share" => Share(args.Contains("--stdout", StringComparer.Ordinal)),
            "rotate-key" => RotateKey(),
            "token" => Token(args),
            "doctor" => Doctor(),
            "help" or "--help" or "-h" => Aiuto(0),
            _ => Aiuto(2),
        };
    }

    private static int Aiuto(int codice)
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

        return codice;
    }

    /// <summary>Custodisce, o dimentica, il token di un'ALTRA macchina.</summary>
    /// <remarks>
    /// Esiste perche' senza un comando il token si deposita a mano dentro un file, che e'
    /// esattamente cio' che si sta togliendo. Il segreto arriva da standard input e non
    /// dagli argomenti, per la stessa ragione scritta in cima a questa classe: la riga
    /// digitata finisce nella cronologia della shell, e su Unix anche in "ps".
    /// </remarks>
    private static int Token(string[] args)
    {
        if (args.Length < 3)
        {
            return Aiuto(2);
        }

        ISecretStore deposito = SecretStores.PerQuestaMacchina();

        try
        {
            return args[1] switch
            {
                "set" => Deposita(deposito, args[2]),
                "forget" => Dimentica(deposito, args[2]),
                _ => Aiuto(2),
            };
        }
        catch (SecretStoreException errore)
        {
            Console.Error.WriteLine(errore.Message);

            return 1;
        }
    }

    private static int Deposita(ISecretStore deposito, string macchina)
    {
        string segreto = LeggiSegreto();

        if (segreto.Length == 0)
        {
            Console.Error.WriteLine("No token was given, so nothing was stored.");

            return 1;
        }

        deposito.Write(macchina, segreto);

        Console.WriteLine($"The token for {macchina} is now kept in {deposito.Descrizione}.");
        Console.WriteLine(
            $"If machines.json still has an \"apiToken\" line for {macchina}, delete it: " +
            "Observer refuses to use a token from that file.");

        return 0;
    }

    private static int Dimentica(ISecretStore deposito, string macchina)
    {
        Console.WriteLine(deposito.Delete(macchina)
            ? $"The token for {macchina} is gone from this computer."
            : $"There was no token for {macchina} here.");

        return 0;
    }

    /// <summary>Legge il segreto senza mostrarlo, quando c'e' qualcuno che lo digita.</summary>
    /// <returns>Il segreto, senza spazi ai bordi.</returns>
    /// <remarks>
    /// Senza eco non e' teatro: il terminale conserva cio' che ha stampato, quindi un token
    /// mostrato mentre lo si incolla resta nella cronologia della finestra e in ogni copia di
    /// quello che c'era a schermo. Con l'input rediretto non c'e' nessuno da proteggere e si
    /// legge la riga e basta.
    /// </remarks>
    private static string LeggiSegreto()
    {
        if (Console.IsInputRedirected)
        {
            return (Console.In.ReadLine() ?? string.Empty).Trim();
        }

        Console.Write("Paste that machine's token (it will not be shown): ");

        StringBuilder costruito = new();

        while (true)
        {
            ConsoleKeyInfo tasto = Console.ReadKey(intercept: true);

            if (tasto.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (tasto.Key == ConsoleKey.Backspace)
            {
                if (costruito.Length > 0)
                {
                    costruito.Length--;
                }

                continue;
            }

            if (!char.IsControl(tasto.KeyChar))
            {
                costruito.Append(tasto.KeyChar);
            }
        }

        Console.WriteLine();

        return costruito.ToString().Trim();
    }

    private static int Share(bool soloIlValore)
    {
        string percorso = CredentialDirectory.PercorsoPredefinito();

        if (Leggi(percorso) is not { } credenziali)
        {
            return 1;
        }

        if (soloIlValore)
        {
            // Write e non WriteLine, di proposito: catturando l'uscita in una variabile di
            // shell, un ritorno a capo finale entrerebbe nel valore, e il confronto a tempo
            // costante lo rifiuterebbe byte a byte.
            Console.Out.Write(credenziali.Current);
            return 0;
        }

        Console.WriteLine("Machine token for this computer:");
        Console.WriteLine();
        Console.WriteLine("    " + credenziali.Current);
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("Certificate fingerprint of this computer:");
        Console.WriteLine();
        Console.WriteLine("    " + Diagnosi.Certificato(percorso));
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
        string percorso = CredentialDirectory.PercorsoPredefinito();

        if (Leggi(percorso) is not { } credenziali)
        {
            return 1;
        }

        MachineCredentials ruotate = credenziali.Ruota(DateTimeOffset.UtcNow, MachineCredentials.FinestraDiGrazia);

        try
        {
            CredentialStore.Scrivi(percorso, ruotate);
        }
        catch (Exception errore) when (errore is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the credential store: " + errore.Message);
            return 1;
        }

        Console.WriteLine("A new machine token has been written.");
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"The previous one keeps working until {DateTimeOffset.UtcNow + MachineCredentials.FinestraDiGrazia:u}, so remote"));
        Console.WriteLine("clients are not cut off at once. Update them before then.");
        Console.WriteLine();

        // Va detto, perche' altrimenti si prova la chiave nuova, non funziona, e si conclude
        // che la rotazione e' rotta. E il comando dipende dal sistema: stamparne uno che qui
        // non esiste manderebbe l'utente a cercare perche' non funziona.
        Console.WriteLine("The service keeps using the OLD key until it is restarted:");
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "    Restart-Service Observer"
            : "    sudo systemctl restart observer");

        return 0;
    }

    private static int Doctor()
    {
        string percorso = CredentialDirectory.PercorsoPredefinito();

        Console.WriteLine("Credential store: " + percorso);
        Console.WriteLine("Protection      : " + Diagnosi.Protezione(percorso));
        Console.WriteLine("Local channel   : " + CanaleLocale.Prova(CanaleLocale.NomePredefinito, TimeSpan.FromSeconds(3)));
        Console.WriteLine("Certificate     : " + Diagnosi.Certificato(percorso));
        Console.WriteLine("Running as      : " + Diagnosi.ChiSono());
        Console.WriteLine("Elevated        : " + Diagnosi.Elevato());
        Console.WriteLine();
        Console.WriteLine("To watch THIS machine you need no token: the dashboard comes in through");
        Console.WriteLine("the local channel. The token exists only so another computer can query this one.");

        return 0;
    }

    private static MachineCredentials? Leggi(string percorso)
    {
        try
        {
            if (CredentialStore.Leggi(percorso) is { } credenziali)
            {
                return credenziali;
            }

            Console.Error.WriteLine("There is no credential store at " + percorso + ".");
            Console.Error.WriteLine("Start the Observer service once: it creates one on first run.");

            return null;
        }
        catch (Exception errore) when (errore is InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Can't read the machine token: an elevated terminal is required.");
            Console.Error.WriteLine("Store : " + percorso);
            Console.Error.WriteLine("Why   : the file grants access only to SYSTEM and to local");
            Console.Error.WriteLine("        administrators. That is deliberate — this token is");
            Console.Error.WriteLine("        valid FROM THE NETWORK and does not expire.");
            Console.Error.WriteLine("You   : " + Diagnosi.ChiSono() + ", elevated: " + Diagnosi.Elevato());
            Console.Error.WriteLine("Fix   : reopen the terminal with 'Run as administrator'.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Note  : to watch THIS machine you need no token at all.");
            Console.Error.WriteLine("        The dashboard comes in through the local channel.");
            Console.Error.WriteLine("Detail: " + errore.Message);

            return null;
        }
    }
}