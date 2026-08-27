using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.Service.Credentials;

/// <summary>
/// Il certificato con cui il servizio si presenta alle ALTRE macchine.
/// </summary>
/// <remarks>
/// Autofirmato, uno per macchina, generato al primo avvio e custodito nello stesso perimetro
/// del token. Nessuna autorita' lo garantisce: cio' che lega un collegamento a questa macchina
/// e' la sua impronta, che si prende a mano con <c>observer share</c>.
/// <para>
/// <b>La validita' e' lunga di proposito, e non e' pigrizia.</b> Con l'impronta fissata dal
/// client, sostituire il certificato significa far fallire OGNI client finche' qualcuno non
/// riscrive l'impronta a mano su ognuno. Una scadenza breve non aggiungerebbe sicurezza — la
/// fiducia qui non viene ne' dalla scadenza ne' da una catena — e trasformerebbe un rinnovo
/// automatico in un guasto simultaneo di tutte le dashboard remote.
/// </para>
/// </remarks>
public static class MachineCertificate
{
    /// <summary>Il nome del file del certificato, accanto al deposito del token.</summary>
    public const string NomeFile = "certificate.pfx";

    /// <summary>Quanto vale il certificato. Vedi le note del tipo: e' lunga di proposito.</summary>
    public static readonly TimeSpan Validita = TimeSpan.FromDays(3653);

    /// <summary>Quanto indietro parte la validita', per tollerare orologi non allineati.</summary>
    /// <remarks>
    /// Un certificato che comincia a valere "adesso" viene rifiutato da una macchina il cui
    /// orologio e' indietro di qualche minuto, e il sintomo — un errore TLS all'avvio che
    /// sparisce da solo poco dopo — non nomina la propria causa.
    /// </remarks>
    public static readonly TimeSpan Anticipo = TimeSpan.FromDays(1);

    /// <summary>Genera un certificato nuovo per questa macchina.</summary>
    /// <param name="nomeMacchina">Il nome da mettere nel soggetto e fra i nomi alternativi.</param>
    /// <param name="adesso">L'istante da cui contare la validita'.</param>
    /// <returns>Il certificato, con la sua chiave privata.</returns>
    public static X509Certificate2 Genera(string nomeMacchina, DateTimeOffset adesso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeMacchina);

        using RSA chiave = RSA.Create(3072);

        CertificateRequest richiesta = new(
            "CN=" + nomeMacchina,
            chiave,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        richiesta.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        richiesta.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        // Autenticazione del SERVER e basta. Un certificato senza uso dichiarato e' un
        // certificato che vale per tutto, e questo non deve valere per nient'altro.
        richiesta.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        // I nomi alternativi non servono a noi — il client confronta l'impronta, non il nome —
        // ma servono a chiunque punti un browser o "openssl s_client" a questa porta per
        // capire con cosa sta parlando.
        SubjectAlternativeNameBuilder nomi = new();
        nomi.AddDnsName(nomeMacchina);
        nomi.AddDnsName("localhost");
        richiesta.CertificateExtensions.Add(nomi.Build());

        return richiesta.CreateSelfSigned(adesso - Anticipo, adesso + Validita);
    }

    /// <summary>Impacchetta il certificato con la sua chiave, per depositarlo.</summary>
    /// <param name="certificato">Il certificato da esportare.</param>
    /// <returns>Il PKCS#12 in byte.</returns>
    /// <remarks>
    /// Senza password, e non e' una svista: una password scritta accanto al file che dovrebbe
    /// proteggere non protegge niente. Il file sta gia' in una cartella che esclude ogni altro
    /// account, e la protezione qui e' il perimetro — esattamente come per il token.
    /// </remarks>
    public static byte[] Esporta(X509Certificate2 certificato)
    {
        ArgumentNullException.ThrowIfNull(certificato);

        return certificato.Export(X509ContentType.Pkcs12);
    }

    /// <summary>Rilegge un certificato depositato.</summary>
    /// <param name="pkcs12">Il contenuto del file.</param>
    /// <returns>Il certificato con la sua chiave privata.</returns>
    /// <remarks>
    /// <b>Il flag cambia per sistema operativo, e non e' una preferenza: e' misurato.</b>
    /// <para>
    /// La scelta ovvia sarebbe <c>EphemeralKeySet</c> ovunque — la chiave resta in memoria e
    /// non tocca il portachiavi del sistema. Su Windows <b>non funziona</b>: il certificato si
    /// carica benissimo, ma SChannel non riesce a servirlo e l'handshake TLS muore con
    /// <i>"Received an unexpected EOF or 0 bytes from the transport stream"</i> — un errore che
    /// non nomina la propria causa e che nessun test di unita' avrebbe visto, perche' fino a
    /// <c>TrasportoHttpsTests</c> nessun test toccava un trasporto vero.
    /// </para>
    /// <para>
    /// Su Windows serve quindi il portachiavi dell'UTENTE del processo: come LocalSystem e'
    /// il profilo di SYSTEM, protetto quanto il deposito. Volutamente NON
    /// <c>MachineKeySet</c>, che finirebbe in <c>ProgramData\Microsoft\Crypto\RSA\MachineKeys</c>,
    /// una cartella con permessi molto piu' larghi. E volutamente NON <c>PersistKeySet</c>:
    /// senza, il contenitore della chiave si cancella da solo. Misurato contando i file dei
    /// portachiavi prima e dopo — otto caricamenti, processo del servizio compreso, ucciso
    /// senza chiusura pulita: quindici file prima, quindici dopo.
    /// </para>
    /// </remarks>
    public static X509Certificate2 Carica(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        X509KeyStorageFlags flag = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, flag);
    }

    /// <summary>L'impronta con cui i client lo riconoscono.</summary>
    /// <param name="certificato">Il certificato.</param>
    /// <returns>L'impronta in forma canonica.</returns>
    public static string Impronta(X509Certificate2 certificato)
    {
        ArgumentNullException.ThrowIfNull(certificato);

        return CertificateFingerprint.Da(certificato.RawDataMemory.Span);
    }

    /// <summary>Il percorso del certificato, accanto al deposito del token.</summary>
    /// <param name="percorsoDelDeposito">Il percorso di <c>credentials.json</c>.</param>
    /// <returns>Il percorso del file del certificato.</returns>
    public static string PercorsoAccantoA(string percorsoDelDeposito)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDelDeposito);

        return Path.Combine(Path.GetDirectoryName(percorsoDelDeposito) ?? ".", NomeFile);
    }
}