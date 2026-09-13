using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Observer.Core.Security;

namespace Observer.Service.Credentials;

/// <summary>
/// Il certificate con cui il servizio si presenta alle ALTRE macchine.
/// </summary>
/// <remarks>
/// Autofirmato, uno per macchina, generato al primo avvio e custodito nello stesso perimetro
/// del token. Nessuna autorita' lo garantisce: cio' che lega un collegamento a questa macchina
/// e' la sua impronta, che si prende a mano con <c>observer share</c>.
/// <para>
/// <b>La validita' e' lunga di proposito, e non e' pigrizia.</b> Con l'impronta fissata dal
/// client, sostituire il certificate significa far fallire OGNI client finche' qualcuno non
/// riscrive l'impronta a mano su ognuno. Una scadenza breve non aggiungerebbe sicurezza — la
/// fiducia qui non viene ne' dalla scadenza ne' da una catena — e trasformerebbe un rinnovo
/// automatico in un guasto simultaneo di tutte le dashboard remote.
/// </para>
/// </remarks>
public static class MachineCertificate
{
    /// <summary>Il nome del file del certificate, accanto al deposito del token.</summary>
    public const string FileName = "certificate.pfx";

    /// <summary>Quanto vale il certificate. Vedi le note del tipo: e' lunga di proposito.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(3653);

    /// <summary>Quanto indietro parte la validita', per tollerare orologi non allineati.</summary>
    /// <remarks>
    /// Un certificate che comincia a valere "now" viene rifiutato da una macchina il cui
    /// orologio e' indietro di qualche minuto, e il sintomo — un errore TLS all'avvio che
    /// sparisce da solo poco dopo — non nomina la propria causa.
    /// </remarks>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromDays(1);

    /// <summary>Create un certificate nuovo per questa macchina.</summary>
    /// <param name="machineName">Il nome da mettere nel soggetto e fra i subjectAlternativeNames alternativi.</param>
    /// <param name="now">L'istante da cui contare la validita'.</param>
    /// <returns>Il certificate, con la sua key privata.</returns>
    public static X509Certificate2 Create(string machineName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        using RSA key = RSA.Create(3072);

        CertificateRequest request = new(
            "CN=" + machineName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        // Autenticazione del SERVER e basta. Un certificate senza uso dichiarato e' un
        // certificate che vale per tutto, e questo non deve valere per nient'altro.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")],
            critical: false));

        // I subjectAlternativeNames alternativi non servono a noi — il client confronta l'impronta, non il nome —
        // ma servono a chiunque punti un browser o "openssl s_client" a questa porta per
        // capire con cosa sta parlando.
        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName(machineName);
        subjectAlternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        return request.CreateSelfSigned(now - ClockSkewAllowance, now + Validity);
    }

    /// <summary>Impacchetta il certificate con la sua key, per depositarlo.</summary>
    /// <param name="certificate">Il certificate da esportare.</param>
    /// <returns>Il PKCS#12 in byte.</returns>
    /// <remarks>
    /// Senza password, e non e' una svista: una password scritta accanto al file che dovrebbe
    /// proteggere non protegge niente. Il file sta gia' in una cartella che esclude ogni altro
    /// account, e la protezione qui e' il perimetro — esattamente come per il token.
    /// </remarks>
    public static byte[] Export(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>Rilegge un certificate depositato.</summary>
    /// <param name="pkcs12">Il contenuto del file.</param>
    /// <returns>Il certificate con la sua key privata.</returns>
    /// <remarks>
    /// <b>Il flag cambia per sistema operativo, e non e' una preferenza: e' misurato.</b>
    /// <para>
    /// La scelta ovvia sarebbe <c>EphemeralKeySet</c> ovunque — la key resta in memoria e
    /// non tocca il portachiavi del sistema. Su Windows <b>non funziona</b>: il certificate si
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
    /// senza, il contenitore della key si cancella da solo. Misurato contando i file dei
    /// portachiavi prima e dopo — otto caricamenti, processo del servizio compreso, ucciso
    /// senza chiusura pulita: quindici file prima, quindici dopo.
    /// </para>
    /// </remarks>
    public static X509Certificate2 Load(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        X509KeyStorageFlags flag = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, flag);
    }

    /// <summary>Rilegge un certificate per GUARDARLO, senza importarne la key.</summary>
    /// <param name="pkcs12">Il contenuto del file.</param>
    /// <returns>Il certificate, utilizzabile solo per leggerne i dati.</returns>
    /// <remarks>
    /// Serve alla riga di comando, che del certificate vuole solo l'impronta. Con
    /// <see cref="Load"/> la key privata finirebbe nel portachiavi dell'utente che ha
    /// lanciato il comando — un amministratore qualsiasi — mentre <c>EphemeralKeySet</c> la
    /// tiene in memoria e la butta. Non regge un handshake TLS, e qui non deve reggerlo.
    /// </remarks>
    public static X509Certificate2 LoadForInspection(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>L'impronta con cui i client lo riconoscono.</summary>
    /// <param name="certificate">Il certificate.</param>
    /// <returns>L'impronta in forma canonica.</returns>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return CertificateFingerprint.From(certificate.RawDataMemory.Span);
    }

    /// <summary>Il percorso del certificate, accanto al deposito del token.</summary>
    /// <param name="storePath">Il percorso di <c>credentials.json</c>.</param>
    /// <returns>Il percorso del file del certificate.</returns>
    public static string PathNextTo(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        return Path.Combine(Path.GetDirectoryName(storePath) ?? ".", FileName);
    }
}