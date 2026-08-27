using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Observer.Service.Credentials;

/// <summary>
/// Raccoglie da Windows i fatti su una cartella, e la mette in sicurezza.
/// </summary>
/// <remarks>
/// Classe a parte e annotata perche' CA1416, con TreatWarningsAsErrors, fa fallire la build su
/// ENTRAMBI i runner: e' analisi statica e non dipende dall'OS che compila.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsDirectoryTrust
{
    private static readonly DirectoryFacts Assente = new(false, false, true, null, false, []);

    /// <summary>SYSTEM, gli amministratori, e l'account che esegue questo processo.</summary>
    /// <returns>I SID di cui fidarsi come proprietari e dentro la DACL.</returns>
    /// <remarks>
    /// In produzione il servizio gira come LocalSystem, quindi il terzo coincide col primo
    /// e non allarga niente. Lanciato a mano in sviluppo e' cio' che gli permette di
    /// fidarsi della cartella che ha creato lui.
    /// </remarks>
    public static IReadOnlyList<string> Fidati()
    {
        using WindowsIdentity corrente = WindowsIdentity.GetCurrent();

        return corrente.User is { } account
            ? [DirectoryTrust.SidSistema, DirectoryTrust.SidAmministratori, account.Value]
            : DirectoryTrust.FidatiPredefiniti;
    }

    /// <summary>Il verdetto su questa cartella, coi principal fidati di questo processo.</summary>
    /// <param name="percorso">Il percorso da esaminare.</param>
    /// <returns>Il verdetto.</returns>
    public static DirectoryVerdict Verdetto(string percorso) =>
        DirectoryTrust.Valuta(Osserva(percorso), Fidati());

    /// <summary>Osserva la cartella senza giudicarla.</summary>
    /// <param name="percorso">Il percorso da esaminare.</param>
    /// <returns>I fatti, da passare a <see cref="DirectoryTrust.Valuta"/>.</returns>
    public static DirectoryFacts Osserva(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        DirectoryInfo info = new(percorso);

        if (!info.Exists)
        {
            return Assente;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Rilevato PRIMA di leggere qualunque ACL: .NET non segue il punto di reparse per
            // leggere gli attributi, quindi qui si sta guardando il collegamento e non il suo
            // bersaglio. Verificato anche col bersaglio inesistente: Exists resta true.
            return new DirectoryFacts(true, true, true, null, false, []);
        }

        try
        {
            DirectorySecurity sicurezza =
                info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

            string? proprietario =
                (sicurezza.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

            List<string> sid = sicurezza
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(regola => ((SecurityIdentifier)regola.IdentityReference).Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DirectoryFacts(true, false, true, proprietario, sicurezza.AreAccessRulesProtected, sid);
        }
        catch (UnauthorizedAccessException)
        {
            // Comprende PrivilegeNotHeldException, che ne deriva.
            return Illeggibile();
        }
        catch (IOException)
        {
            return Illeggibile();
        }
    }

    /// <summary>La sicurezza da applicare: protetta, solo SYSTEM e amministratori.</summary>
    /// <returns>Il descrittore.</returns>
    public static DirectorySecurity Sicurezza()
    {
        DirectorySecurity sicurezza = new();

        // Taglia l'ereditarieta'. Senza, la cartella eredita da C:\ProgramData l'ACE che
        // concede a BUILTIN\Users la lettura, e il segreto e' leggibile da ogni utente della
        // macchina senza che ci sia alcun attaccante.
        sicurezza.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (WellKnownSidType tipo in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            sicurezza.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(tipo, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        using WindowsIdentity corrente = WindowsIdentity.GetCurrent();

        if (corrente.User is { } account && !account.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            sicurezza.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return sicurezza;
    }

    /// <summary>Porta la cartella in uno stato in cui puo' ospitare un segreto.</summary>
    /// <param name="percorso">Il percorso da preparare.</param>
    /// <exception cref="InvalidOperationException">Se non e' possibile, con il motivo dentro.</exception>
    /// <remarks>
    /// Una giunzione NON viene riparata: e' un incidente di sicurezza e non un intoppo, e
    /// "sistemarla" significherebbe applicare le correzioni alla cartella di chi l'ha piazzata.
    /// </remarks>
    public static void Prepara(string percorso)
    {
        DirectoryVerdict verdetto = Verdetto(percorso);

        if (verdetto.PuoOspitareUnSegreto())
        {
            return;
        }

        if (verdetto == DirectoryVerdict.Assente)
        {
            // Creata GIA' protetta, in un colpo solo: creare e poi applicare lascerebbe una
            // finestra in cui la cartella eredita. L'estensione sul descrittore e' l'unica
            // forma che lo fa; Directory.CreateDirectory(percorso, modo) e' il gemello Unix e
            // non c'entra.
            Sicurezza().CreateDirectory(percorso);
            Conferma(percorso);
            return;
        }

        if (verdetto == DirectoryVerdict.PuntoDiReparse)
        {
            throw new InvalidOperationException(
                $"The credential directory '{percorso}' is a junction or symbolic link. " +
                "Observer will not follow it: a standard user can create one without any " +
                "privilege, which would place the machine token wherever they choose. " +
                "Remove it and restart the service.");
        }

        Ripara(percorso, verdetto);
        Conferma(percorso);
    }

    /// <summary>Riguarda la cartella dopo averla toccata, e si rifiuta se non e' sicura.</summary>
    /// <param name="percorso">Il percorso appena creato o riparato.</param>
    /// <remarks>
    /// Chiude una condizione di gara reale. Fra l'osservazione e la creazione un utente
    /// standard puo' infilarsi e creare lui la cartella; a quel punto la creazione con
    /// descrittore NON fallisce, e' un no-op silenzioso, e senza questa riverifica si
    /// proseguirebbe depositando il token in una cartella ostile credendola appena creata.
    /// </remarks>
    private static void Conferma(string percorso)
    {
        DirectoryVerdict verdetto = Verdetto(percorso);

        if (!verdetto.PuoOspitareUnSegreto())
        {
            throw new InvalidOperationException(
                $"The credential directory '{percorso}' is still not safe after being prepared " +
                $"({verdetto}). Another process may have created it first. The machine token " +
                "will not be written.");
        }
    }

    private static void Ripara(string percorso, DirectoryVerdict verdetto)
    {
        DirectoryInfo info = new(percorso);

        try
        {
            if (verdetto == DirectoryVerdict.ProprietarioNonFidato)
            {
                // La PROPRIETA' per prima. Correggere la DACL lasciando il proprietario
                // com'e' non serve a niente: ha WRITE_DAC implicito e la disfa subito.
                DirectorySecurity proprieta = new();
                proprieta.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                info.SetAccessControl(proprieta);
            }

            info.SetAccessControl(Sicurezza());
        }
        catch (Exception errore) when (errore is UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The credential directory '{percorso}' can't hold a secret ({verdetto}), and " +
                "this process lacks the rights to repair it. The machine token would be " +
                "readable by other accounts on this machine. Run the service as LocalSystem, " +
                $"or delete '{percorso}' and let the service recreate it.",
                errore);
        }
    }

    private static DirectoryFacts Illeggibile() => new(true, false, false, null, false, []);
}