using Observer.Core.Platform;

namespace Observer.Core.Security;

/// <summary>
/// Dove il CLIENT tiene i token delle macchine remote.
/// </summary>
/// <remarks>
/// Nasce da un difetto vero: i token stavano in chiaro dentro <c>machines.json</c>, un file
/// scritto a mano e fatto per essere guardato. Finche' quel token serviva solo a leggere la
/// CPU altrui il costo di perderlo era contenuto; da quando autorizza anche a terminare
/// processi, lo stesso file vale molto di piu'.
/// <para>
/// Da non confondere con il deposito del SERVIZIO, sotto
/// <c>Observer.Service/Credentials/</c>: quello custodisce il token che una macchina
/// pretende, e' unico per macchina e sta in una cartella di sistema. Questo custodisce i
/// token che un utente presenta ad ALTRE macchine, e' per utente, e non prova a difendersi
/// dagli amministratori della propria macchina, che possono comunque leggere tutto.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>Dove i segreti sono custoditi, in una frase da mostrare a chi guarda.</summary>
    string Descrizione { get; }

    /// <summary>Legge un segreto. False se non c'e'.</summary>
    /// <param name="nome">Il nome sotto cui e' stato depositato.</param>
    /// <param name="segreto">Il segreto letto.</param>
    /// <returns>True se c'era.</returns>
    /// <exception cref="SecretStoreException">Se c'e' ma non e' sicuro leggerlo.</exception>
    bool TryRead(string nome, out string segreto);

    /// <summary>Deposita un segreto, sostituendo quello che c'era.</summary>
    /// <param name="nome">Il nome sotto cui depositarlo.</param>
    /// <param name="segreto">Il segreto.</param>
    void Write(string nome, string segreto);

    /// <summary>Cancella un segreto. False se non c'era.</summary>
    /// <param name="nome">Il nome del segreto.</param>
    /// <returns>True se c'era ed e' stato tolto.</returns>
    bool Delete(string nome);
}

/// <summary>
/// Il deposito c'e' ma non ci si puo' fidare, oppure non ha voluto rispondere.
/// </summary>
/// <remarks>
/// Eccezione e non un <c>false</c>: "il segreto non c'e'" e "il segreto c'e' ma il file e'
/// leggibile da chiunque" sono due cose diverse, e la seconda non deve poter essere scambiata
/// per la prima e finire in un ramo che invita a depositarlo di nuovo.
/// </remarks>
public sealed class SecretStoreException : Exception
{
    /// <summary>Crea l'eccezione con il motivo da mostrare.</summary>
    /// <param name="message">Il motivo, gia' scritto per chi legge.</param>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Crea l'eccezione con il motivo e la causa.</summary>
    /// <param name="message">Il motivo.</param>
    /// <param name="innerException">La causa.</param>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Crea l'eccezione senza motivo. Esiste solo per l'analizzatore.</summary>
    public SecretStoreException()
    {
    }
}

/// <summary>Come si chiama un segreto, e cosa non puo' chiamarsi.</summary>
/// <remarks>
/// Sta qui e non dentro il deposito Unix perche' il nome arriva da <c>machines.json</c>, che
/// lo scrive una persona, e finisce a comporre un percorso di file: se non fosse controllato,
/// una voce chiamata <c>../../id_rsa</c> farebbe leggere e sovrascrivere un file fuori dalla
/// cartella dei segreti. Neutro rispetto alla piattaforma anche perche' la regola vada
/// provata da entrambi i runner, e non solo dove il deposito a file esiste davvero.
/// </remarks>
public static class SecretName
{
    /// <summary>Il nome ripulito, oppure un'eccezione se non e' utilizzabile.</summary>
    /// <param name="nome">Il nome come arriva dalla configurazione.</param>
    /// <returns>Il nome senza spazi ai bordi.</returns>
    /// <exception cref="SecretStoreException">Se il nome non e' utilizzabile.</exception>
    public static string Valida(string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);

        foreach (char lettera in nome)
        {
            if (!char.IsAsciiLetterOrDigit(lettera) && lettera is not ('-' or '_' or '.' or ' '))
            {
                throw new SecretStoreException(
                    $"\"{nome}\" cannot be used as a machine name here: only letters, digits, " +
                    "spaces, dots, dashes and underscores are allowed.");
            }
        }

        string pulito = nome.Trim();

        if (pulito.Length == 0 || pulito is "." or "..")
        {
            throw new SecretStoreException($"\"{nome}\" cannot be used as a machine name here.");
        }

        return pulito;
    }
}

/// <summary>Sceglie il deposito giusto per una piattaforma.</summary>
/// <remarks>
/// La piattaforma e' un PARAMETRO, come per i collector: e' cio' che permette di provare la
/// scelta dal runner Windows come da quello Linux, invece di avere un ramo che nessuno dei due
/// esegue mai.
/// </remarks>
public static class SecretStores
{
    /// <summary>Il deposito per la piattaforma indicata.</summary>
    /// <param name="piattaforma">Quale sistema operativo.</param>
    /// <returns>Il deposito.</returns>
    public static ISecretStore Per(HostPlatform piattaforma) => piattaforma switch
    {
        HostPlatform.Windows when OperatingSystem.IsWindows() => new WindowsSecretStore(),
        HostPlatform.Linux when OperatingSystem.IsLinux() => new UnixSecretStore(),
        _ => new UnsupportedSecretStore(),
    };

    /// <summary>Il deposito di questa macchina.</summary>
    /// <returns>Il deposito.</returns>
    public static ISecretStore PerQuestaMacchina() => Per(HostPlatformDetector.Current);
}

/// <summary>Deposito per una piattaforma su cui non si sa custodire niente.</summary>
/// <remarks>
/// Esiste per la stessa ragione degli altri provider "Unsupported": una piattaforma
/// sconosciuta deve DIRE che non sa custodire un segreto, non fingere un deposito vuoto e far
/// concludere a chi guarda di essersi dimenticato di depositarlo.
/// </remarks>
public sealed class UnsupportedSecretStore : ISecretStore
{
    private const string Motivo =
        "This platform has no supported place to keep machine tokens. Observer knows the " +
        "Windows Credential Manager and, on Linux, a file readable only by its owner.";

    /// <inheritdoc />
    public string Descrizione => "no secret store is available on this platform";

    /// <inheritdoc />
    public bool TryRead(string nome, out string segreto) => throw new SecretStoreException(Motivo);

    /// <inheritdoc />
    public void Write(string nome, string segreto) => throw new SecretStoreException(Motivo);

    /// <inheritdoc />
    public bool Delete(string nome) => throw new SecretStoreException(Motivo);
}
