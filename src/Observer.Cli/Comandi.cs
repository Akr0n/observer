using System.Globalization;
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

            To watch THIS machine you need no token at all: the dashboard comes in through the
            local channel. The token exists only so another computer can query this one.
            """);

        return codice;
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
        // che la rotazione e' rotta.
        Console.WriteLine("The service keeps using the OLD key until it is restarted:");
        Console.WriteLine("    Restart-Service Observer");

        return 0;
    }

    private static int Doctor()
    {
        string percorso = CredentialDirectory.PercorsoPredefinito();

        Console.WriteLine("Credential store: " + percorso);
        Console.WriteLine("Protection      : " + Diagnosi.Protezione(percorso));
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