using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace Observer.Service.LocalChannel;

/// <summary>
/// L'ascolto su named pipe e la lista di chi puo' aprirla.
/// </summary>
/// <remarks>
/// Classe a parte e annotata perche' CA1416, con TreatWarningsAsErrors, fa fallire la build su
/// ENTRAMBI i runner: e' analisi statica e non dipende dall'OS che compila. L'attributo su una
/// local function non viene onorato e non copre il corpo di una lambda, quindi questo codice
/// non puo' stare nei top-level statements di Program.cs.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsNamedPipe
{
    /// <summary>Apre l'ascolto sulla pipe e ne configura il trasporto.</summary>
    /// <param name="builder">Il builder dell'applicazione.</param>
    /// <param name="pipeName">Il nome della pipe, senza prefisso.</param>
    public static void Ascolta(WebApplicationBuilder builder, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        // UseNamedPipes NON serve per aprire la pipe: su Windows il trasporto e' gia'
        // registrato e ListenNamedPipe basta da solo. Serve unicamente per queste due opzioni.
        builder.WebHost.UseNamedPipes(ConfiguraTrasporto);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenNamedPipe(pipeName));
    }

    /// <summary>Imposta le due opzioni del trasporto. Insieme, mai una sola.</summary>
    /// <param name="opzioni">Le opzioni del trasporto named pipe.</param>
    public static void ConfiguraTrasporto(NamedPipeTransportOptions opzioni)
    {
        ArgumentNullException.ThrowIfNull(opzioni);

        // Le due righe seguenti vanno tenute ADIACENTI e non separate mai.
        // Impostare solo PipeSecurity fa lanciare all'avvio ArgumentException ("'pipeSecurity'
        // must be null when 'options' contains 'PipeOptions.CurrentUserOnly'"), ed e' il caso
        // innocuo perche' rumoroso. Impostare solo CurrentUserOnly = false e' quello
        // pericoloso: l'host parte normalmente e produce una pipe con DACL
        // (A;;FR;;;WD)(A;;FR;;;AN), cioe' leggibile da Everyone e da ANONYMOUS LOGON. Nessun
        // errore, nessun warning, nessun sintomo.
        opzioni.CurrentUserOnly = false;
        opzioni.PipeSecurity = Sicurezza();
    }

    /// <summary>La DACL della pipe: chi puo' aprirla.</summary>
    /// <returns>Il descrittore da applicare al trasporto.</returns>
    public static PipeSecurity Sicurezza()
    {
        PipeSecurity sicurezza = new();

        // FullControl e non il solo CreateNewInstance: la prima istanza si crea sempre, ed e'
        // dalla SECONDA che serve FILE_CREATE_PIPE_INSTANCE (0x4). Kestrel ne apre piu' d'una,
        // e senza quel bit il bind fallisce con UnauthorizedAccessException, che Kestrel
        // traduce nel fuorviante "address already in use".
        sicurezza.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        using WindowsIdentity corrente = WindowsIdentity.GetCurrent();

        if (corrente.User is { } account)
        {
            // Quando il servizio gira come LocalSystem questa ACE coincide con la precedente;
            // quando gira lanciato a mano da un terminale, e' l'unica che gli permette di
            // aprire la propria pipe.
            sicurezza.AddAccessRule(new PipeAccessRule(
                account,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        // INTERACTIVE e NON Authenticated Users: il secondo comprende ogni principal
        // autenticato capace di raggiungere la macchina, anche via SMB sulla porta 445, e una
        // named pipe e' esposta proprio li'.
        sicurezza.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        // Non serve ordinare le ACE a mano: PipeSecurity canonicalizza, e una DENY aggiunta per
        // ultima finisce comunque in testa (verificato confrontando le due SDDL, identiche
        // carattere per carattere). La garanzia e' pero' del tipo CommonAcl e NON della nostra
        // chiamata: importando un descrittore da SDDL o da forma binaria la DENY resterebbe
        // dove sta e diventerebbe inerte. Costruire sempre con AddAccessRule, mai importare.
        return sicurezza;
    }
}