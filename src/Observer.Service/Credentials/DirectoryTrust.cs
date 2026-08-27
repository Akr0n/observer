namespace Observer.Service.Credentials;

/// <summary>Cosa si e' osservato di una cartella candidata a ospitare il token di macchina.</summary>
/// <param name="Esiste">Se la cartella esiste.</param>
/// <param name="PuntoDiReparse">Se e' una giunzione o un collegamento simbolico.</param>
/// <param name="DescrittoreLeggibile">Se si e' riusciti a leggere il descrittore di sicurezza.</param>
/// <param name="ProprietarioSid">Il SID del proprietario, in forma testuale.</param>
/// <param name="DaclProtetta">Se la DACL e' protetta, cioe' NON eredita dal padre.</param>
/// <param name="SidNellaDacl">I SID che compaiono nelle regole di accesso.</param>
public sealed record DirectoryFacts(
    bool Esiste,
    bool PuntoDiReparse,
    bool DescrittoreLeggibile,
    string? ProprietarioSid,
    bool DaclProtetta,
    IReadOnlyList<string> SidNellaDacl);

/// <summary>L'esito della valutazione. Il valore ZERO non e' quello che autorizza.</summary>
public enum DirectoryVerdict
{
    /// <summary>Non si e' potuto nemmeno leggere il descrittore.</summary>
    Sconosciuto = 0,

    /// <summary>E' una giunzione o un collegamento: i dati finirebbero altrove.</summary>
    PuntoDiReparse,

    /// <summary>Il proprietario puo' riscrivere la DACL quando vuole.</summary>
    ProprietarioNonFidato,

    /// <summary>La DACL eredita, oppure concede a qualcuno che non deve entrare.</summary>
    DaclAperta,

    /// <summary>Non esiste: si puo' creare da zero, che e' il caso migliore.</summary>
    Assente,

    /// <summary>Proprietario fidato, DACL protetta, nessun estraneo.</summary>
    Sicura,
}

/// <summary>Comodita' per non elencare a mano i casi negativi.</summary>
public static class DirectoryVerdictExtensions
{
    /// <summary>Se una cartella in questo stato puo' gia' ospitare un segreto.</summary>
    /// <param name="verdetto">L'esito della valutazione.</param>
    /// <returns>Vero solo per <see cref="DirectoryVerdict.Sicura"/>.</returns>
    /// <remarks>
    /// Scritto come "uguale a Sicura" e non come "diverso da questi tre": aggiungere domani un
    /// caso negativo all'enum non deve trasformarlo in un permesso per distrazione.
    /// </remarks>
    public static bool PuoOspitareUnSegreto(this DirectoryVerdict verdetto) =>
        verdetto == DirectoryVerdict.Sicura;
}

/// <summary>
/// Decide se ci si puo' fidare della cartella che ospitera' il token di macchina.
/// </summary>
/// <remarks>
/// Funzione PURA sui fatti osservati, perche' i casi che contano non si possono costruire tutti
/// su una macchina qualsiasi — una cartella posseduta da SYSTEM richiede una sessione
/// amministrativa — e perche' e' la decisione di sicurezza portante del deposito.
/// <para>
/// L'ordine dei controlli e' vincolato e non e' un dettaglio di stile. Vedi i commenti.
/// </para>
/// </remarks>
public static class DirectoryTrust
{
    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string SidSistema = "S-1-5-18";

    /// <summary>BUILTIN\Administrators.</summary>
    public const string SidAmministratori = "S-1-5-32-544";

    /// <summary>I proprietari fidati quando non se ne indicano altri.</summary>
    public static readonly IReadOnlyList<string> FidatiPredefiniti = [SidSistema, SidAmministratori];

    /// <summary>Valuta la cartella contro SYSTEM e gli amministratori.</summary>
    /// <param name="fatti">I fatti raccolti dal sistema operativo.</param>
    /// <returns>Il verdetto.</returns>
    public static DirectoryVerdict Valuta(DirectoryFacts fatti) => Valuta(fatti, FidatiPredefiniti);

    /// <summary>Valuta la cartella contro un insieme esplicito di principal fidati.</summary>
    /// <param name="fatti">I fatti raccolti dal sistema operativo.</param>
    /// <param name="fidati">
    /// I SID che possono possedere la cartella e comparire nella sua DACL. In produzione
    /// sono SYSTEM e gli amministratori, piu' l'account che ESEGUE il servizio - il quale
    /// in produzione coincide con SYSTEM e quindi non concede nulla di nuovo. Lanciato a
    /// mano in sviluppo e' cio' che permette al servizio di fidarsi della cartella che ha
    /// creato lui. Un utente standard non puo' in alcun modo creare una cartella posseduta
    /// da SYSTEM, verificato: SetOwner fallisce. L'estensione non apre strade a nessuno.
    /// </param>
    /// <returns>Il verdetto.</returns>
    public static DirectoryVerdict Valuta(DirectoryFacts fatti, IReadOnlyList<string> fidati)
    {
        ArgumentNullException.ThrowIfNull(fatti);
        ArgumentNullException.ThrowIfNull(fidati);

        if (!fatti.Esiste)
        {
            // Il caso migliore: si crea da zero, gia' con proprietario e DACL giusti, senza
            // dover riparare niente.
            return DirectoryVerdict.Assente;
        }

        if (fatti.PuntoDiReparse)
        {
            // PRIMO, prima di leggere qualunque ACL. Una giunzione la crea un utente standard
            // senza alcun privilegio: se questo controllo venisse dopo, si correggerebbero
            // proprietario e ACL della cartella dell'ATTACCANTE e ci si depositerebbe dentro
            // il token.
            return DirectoryVerdict.PuntoDiReparse;
        }

        if (!fatti.DescrittoreLeggibile)
        {
            return DirectoryVerdict.Sconosciuto;
        }

        if (!Fidato(fatti.ProprietarioSid, fidati))
        {
            // SECONDO, e prima della DACL. Il proprietario ha WRITE_DAC implicito: una DACL
            // perfetta su una cartella posseduta da un utente e' un "finto protetto", e quel
            // l'utente se la riscrive con una sola chiamata. Misurato.
            return DirectoryVerdict.ProprietarioNonFidato;
        }

        if (!fatti.DaclProtetta)
        {
            // Non protetta significa che eredita, e la cartella di sistema che ospita il
            // deposito concede a BUILTIN\Users la lettura ereditabile: ereditare basta a
            // perdere il segreto, senza bisogno di alcun attaccante.
            return DirectoryVerdict.DaclAperta;
        }

        return fatti.SidNellaDacl.All(sid => Fidato(sid, fidati))
            ? DirectoryVerdict.Sicura
            : DirectoryVerdict.DaclAperta;
    }

    private static bool Fidato(string? sid, IReadOnlyList<string> fidati) =>
        sid is not null && fidati.Contains(sid, StringComparer.OrdinalIgnoreCase);
}