namespace Observer.Service.Credentials;

/// <summary>Cosa si e' osservato di una cartella candidata a ospitare il token di macchina.</summary>
/// <param name="Exists">Se la cartella esiste.</param>
/// <param name="IsReparsePoint">Se e' una giunzione o un collegamento simbolico.</param>
/// <param name="SecurityDescriptorReadable">Se si e' riusciti a leggere il descrittore di sicurezza.</param>
/// <param name="OwnerSid">Il SID del proprietario, in forma testuale.</param>
/// <param name="DaclProtected">Se la DACL e' protetta, cioe' NON eredita dal padre.</param>
/// <param name="DaclSids">I SID che compaiono nelle regole di accesso.</param>
public sealed record DirectoryFacts(
    bool Exists,
    bool IsReparsePoint,
    bool SecurityDescriptorReadable,
    string? OwnerSid,
    bool DaclProtected,
    IReadOnlyList<string> DaclSids);

/// <summary>L'esito della valutazione. Il valore ZERO non e' quello che autorizza.</summary>
public enum DirectoryVerdict
{
    /// <summary>Non si e' potuto nemmeno leggere il descrittore.</summary>
    Unknown = 0,

    /// <summary>E' una giunzione o un collegamento: i dati finirebbero altrove.</summary>
    ReparsePoint,

    /// <summary>Il proprietario puo' riscrivere la DACL quando vuole.</summary>
    UntrustedOwner,

    /// <summary>La DACL eredita, oppure concede a qualcuno che non deve entrare.</summary>
    OpenDacl,

    /// <summary>Non esiste: si puo' creare da zero, che e' il caso migliore.</summary>
    Missing,

    /// <summary>Proprietario fidato, DACL protetta, nessun estraneo.</summary>
    Safe,
}

/// <summary>Comodita' per non elencare a mano i casi negativi.</summary>
public static class DirectoryVerdictExtensions
{
    /// <summary>Se una cartella in questo stato puo' gia' ospitare un segreto.</summary>
    /// <param name="verdict">L'esito della valutazione.</param>
    /// <returns>Vero solo per <see cref="DirectoryVerdict.Safe"/>.</returns>
    /// <remarks>
    /// Scritto come "uguale a Safe" e non come "diverso da questi tre": aggiungere domani un
    /// caso negativo all'enum non deve trasformarlo in un permesso per distrazione.
    /// </remarks>
    public static bool CanHoldSecret(this DirectoryVerdict verdict) =>
        verdict == DirectoryVerdict.Safe;
}

/// <summary>
/// Decide se ci si puo' fidare della cartella che ospitera' il token di macchina.
/// </summary>
/// <remarks>
/// Funzione PURA sui facts osservati, perche' i casi che contano non si possono costruire tutti
/// su una macchina qualsiasi — una cartella posseduta da SYSTEM richiede una sessione
/// amministrativa — e perche' e' la decisione di sicurezza portante del deposito.
/// <para>
/// L'ordine dei controlli e' vincolato e non e' un dettaglio di stile. Vedi i commenti.
/// </para>
/// </remarks>
public static class DirectoryTrust
{
    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>I proprietari trustedSids quando non se ne indicano altri.</summary>
    public static readonly IReadOnlyList<string> DefaultTrustedSids = [SystemSid, AdministratorsSid];

    /// <summary>Evaluate la cartella contro SYSTEM e gli amministratori.</summary>
    /// <param name="facts">I facts raccolti dal sistema operativo.</param>
    /// <returns>Il verdict.</returns>
    public static DirectoryVerdict Evaluate(DirectoryFacts facts) => Evaluate(facts, DefaultTrustedSids);

    /// <summary>Evaluate la cartella contro un insieme esplicito di principal trustedSids.</summary>
    /// <param name="facts">I facts raccolti dal sistema operativo.</param>
    /// <param name="trustedSids">
    /// I SID che possono possedere la cartella e comparire nella sua DACL. In produzione
    /// sono SYSTEM e gli amministratori, piu' l'account che ESEGUE il servizio - il quale
    /// in produzione coincide con SYSTEM e quindi non concede nulla di nuovo. Lanciato a
    /// mano in sviluppo e' cio' che permette al servizio di fidarsi della cartella che ha
    /// creato lui. Un utente standard non puo' in alcun modo creare una cartella posseduta
    /// da SYSTEM, verificato: SetOwner fallisce. L'estensione non apre strade a nessuno.
    /// </param>
    /// <returns>Il verdict.</returns>
    public static DirectoryVerdict Evaluate(DirectoryFacts facts, IReadOnlyList<string> trustedSids)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(trustedSids);

        if (!facts.Exists)
        {
            // Il caso migliore: si crea da zero, gia' con proprietario e DACL giusti, senza
            // dover riparare niente.
            return DirectoryVerdict.Missing;
        }

        if (facts.IsReparsePoint)
        {
            // PRIMO, prima di leggere qualunque ACL. Una giunzione la crea un utente standard
            // senza alcun privilegio: se questo controllo venisse dopo, si correggerebbero
            // proprietario e ACL della cartella dell'ATTACCANTE e ci si depositerebbe dentro
            // il token.
            return DirectoryVerdict.ReparsePoint;
        }

        if (!facts.SecurityDescriptorReadable)
        {
            return DirectoryVerdict.Unknown;
        }

        if (!IsTrusted(facts.OwnerSid, trustedSids))
        {
            // SECONDO, e prima della DACL. Il proprietario ha WRITE_DAC implicito: una DACL
            // perfetta su una cartella posseduta da un utente e' un "finto protetto", e quel
            // l'utente se la riscrive con una sola chiamata. Misurato.
            return DirectoryVerdict.UntrustedOwner;
        }

        if (!facts.DaclProtected)
        {
            // Non protetta significa che eredita, e la cartella di sistema che ospita il
            // deposito concede a BUILTIN\Users la lettura ereditabile: ereditare basta a
            // perdere il segreto, senza bisogno di alcun attaccante.
            return DirectoryVerdict.OpenDacl;
        }

        return facts.DaclSids.All(sid => IsTrusted(sid, trustedSids))
            ? DirectoryVerdict.Safe
            : DirectoryVerdict.OpenDacl;
    }

    private static bool IsTrusted(string? sid, IReadOnlyList<string> trustedSids) =>
        sid is not null && trustedSids.Contains(sid, StringComparer.OrdinalIgnoreCase);
}