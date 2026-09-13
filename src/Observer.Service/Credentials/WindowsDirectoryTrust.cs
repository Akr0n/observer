using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Observer.Service.Credentials;

/// <summary>
/// Raccoglie da Windows i fatti su una cartella, e la mette in security.
/// </summary>
/// <remarks>
/// Classe a parte e annotata perche' CA1416, con TreatWarningsAsErrors, fa fallire la build su
/// ENTRAMBI i runner: e' analisi statica e non dipende dall'OS che compila.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsDirectoryTrust
{
    private static readonly DirectoryFacts MissingDirectory = new(false, false, true, null, false, []);

    /// <summary>SYSTEM, gli amministratori, e l'account che esegue questo processo.</summary>
    /// <returns>I SID di cui fidarsi come proprietari e dentro la DACL.</returns>
    /// <remarks>
    /// In produzione il servizio gira come LocalSystem, quindi il terzo coincide col primo
    /// e non allarga niente. Lanciato a mano in sviluppo e' cio' che gli permette di
    /// fidarsi della cartella che ha creato lui.
    /// </remarks>
    public static IReadOnlyList<string> TrustedSids()
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        return current.User is { } account
            ? [DirectoryTrust.SystemSid, DirectoryTrust.AdministratorsSid, account.Value]
            : DirectoryTrust.DefaultTrustedSids;
    }

    /// <summary>Il verdict su questa cartella, coi principal fidati di questo processo.</summary>
    /// <param name="path">Il path da esaminare.</param>
    /// <returns>Il verdict.</returns>
    public static DirectoryVerdict VerdictFor(string path) =>
        DirectoryTrust.Evaluate(Observe(path), TrustedSids());

    /// <summary>Observe la cartella senza giudicarla.</summary>
    /// <param name="path">Il path da esaminare.</param>
    /// <returns>I fatti, da passare a <see cref="DirectoryTrust.Evaluate"/>.</returns>
    public static DirectoryFacts Observe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        DirectoryInfo info = new(path);

        if (!info.Exists)
        {
            return MissingDirectory;
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
            DirectorySecurity security =
                info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

            string? owner =
                (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

            List<string> sids = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DirectoryFacts(true, false, true, owner, security.AreAccessRulesProtected, sids);
        }
        catch (UnauthorizedAccessException)
        {
            // Comprende PrivilegeNotHeldException, che ne deriva.
            return Unreadable();
        }
        catch (IOException)
        {
            return Unreadable();
        }
    }

    /// <summary>La security da applicare: protetta, solo SYSTEM e amministratori.</summary>
    /// <returns>Il descrittore.</returns>
    public static DirectorySecurity SecurityDescriptor()
    {
        DirectorySecurity security = new();

        // Taglia l'ereditarieta'. Senza, la cartella eredita da C:\ProgramData l'ACE che
        // concede a BUILTIN\Users la lettura, e il segreto e' leggibile da ogni utente della
        // macchina senza che ci sia alcun attaccante.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (WellKnownSidType sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        if (current.User is { } account && !account.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>Porta la cartella in uno stato in cui puo' ospitare un segreto.</summary>
    /// <param name="path">Il path da preparare.</param>
    /// <exception cref="InvalidOperationException">Se non e' possibile, con il motivo dentro.</exception>
    /// <remarks>
    /// Una giunzione NON viene riparata: e' un incidente di security e non un intoppo, e
    /// "sistemarla" significherebbe applicare le correzioni alla cartella di chi l'ha piazzata.
    /// </remarks>
    public static void Prepare(string path)
    {
        DirectoryVerdict verdict = VerdictFor(path);

        if (verdict.CanHoldSecret())
        {
            return;
        }

        if (verdict == DirectoryVerdict.Missing)
        {
            // Creata GIA' protetta, in un colpo solo: creare e poi applicare lascerebbe una
            // finestra in cui la cartella eredita. L'estensione sul descrittore e' l'unica
            // forma che lo fa; Directory.CreateDirectory(path, modo) e' il gemello Unix e
            // non c'entra.
            SecurityDescriptor().CreateDirectory(path);
            ConfirmSafe(path);
            return;
        }

        if (verdict == DirectoryVerdict.ReparsePoint)
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' is a junction or symbolic link. " +
                "Observer will not follow it: a standard user can create one without any " +
                "privilege, which would place the machine token wherever they choose. " +
                "Remove it and restart the service.");
        }

        Repair(path, verdict);
        ConfirmSafe(path);
    }

    /// <summary>Riguarda la cartella dopo averla toccata, e si rifiuta se non e' sicura.</summary>
    /// <param name="path">Il path appena creato o riparato.</param>
    /// <remarks>
    /// Chiude una condizione di gara reale. Fra l'osservazione e la creazione un utente
    /// standard puo' infilarsi e creare lui la cartella; a quel punto la creazione con
    /// descrittore NON fallisce, e' un no-op silenzioso, e senza questa riverifica si
    /// proseguirebbe depositando il token in una cartella ostile credendola appena creata.
    /// </remarks>
    private static void ConfirmSafe(string path)
    {
        DirectoryVerdict verdict = VerdictFor(path);

        if (!verdict.CanHoldSecret())
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' is still not safe after being prepared " +
                $"({verdict}). Another process may have created it first. The machine token " +
                "will not be written.");
        }
    }

    private static void Repair(string path, DirectoryVerdict verdict)
    {
        DirectoryInfo info = new(path);

        try
        {
            if (verdict == DirectoryVerdict.UntrustedOwner)
            {
                // La PROPRIETA' per prima. Correggere la DACL lasciando il owner
                // com'e' non serve a niente: ha WRITE_DAC implicito e la disfa subito.
                DirectorySecurity ownership = new();
                ownership.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                info.SetAccessControl(ownership);
            }

            info.SetAccessControl(SecurityDescriptor());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The credential directory '{path}' can't hold a secret ({verdict}), and " +
                "this process lacks the rights to repair it. The machine token would be " +
                "readable by other accounts on this machine. Run the service as LocalSystem, " +
                $"or delete '{path}' and let the service recreate it.",
                error);
        }
    }

    private static DirectoryFacts Unreadable() => new(true, false, false, null, false, []);
}