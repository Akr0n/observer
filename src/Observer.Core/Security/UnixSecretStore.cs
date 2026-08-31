using System.Runtime.Versioning;
using System.Text;

namespace Observer.Core.Security;

/// <summary>
/// I token delle macchine remote in file leggibili solo dal proprietario.
/// </summary>
/// <remarks>
/// Su Linux non esiste un deposito di sistema sempre presente: il portachiavi di GNOME o di
/// KDE c'e' su una sessione grafica e non su una macchina raggiunta via SSH, e farne una
/// dipendenza vorrebbe dire che la dashboard non parte dove quel servizio non gira. Un file a
/// <c>0600</c> e' lo stesso livello di protezione con cui vive una chiave privata di SSH, ed
/// e' quello che il servizio usa gia' per il proprio token.
/// <para>
/// La differenza che conta rispetto a <c>machines.json</c> non e' solo il modo del file: e'
/// che qui i permessi si <b>verificano in lettura</b>, e un file che qualcun altro puo'
/// leggere fa fallire la lettura invece di funzionare in silenzio. Un permesso sbagliato che
/// non rompe niente e' un permesso sbagliato che resta li' per sempre.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class UnixSecretStore : ISecretStore
{
    private const UnixFileMode SoloProprietario = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode CartellaSoloProprietario =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Cio' che nessun altro deve poter fare sul file di un segreto.</summary>
    private const UnixFileMode Altrui =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string cartella;

    /// <summary>Crea il deposito nella cartella indicata.</summary>
    /// <param name="cartella">Dove tenere i segreti, oppure null per il percorso predefinito.</param>
    public UnixSecretStore(string? cartella = null) =>
        this.cartella = cartella ?? PercorsoPredefinito();

    /// <summary>La cartella dei segreti sotto il profilo dell'utente.</summary>
    /// <returns>Il percorso.</returns>
    public static string PercorsoPredefinito() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Observer",
        "secrets");

    /// <inheritdoc />
    public string Descrizione => "files readable only by their owner, under " + cartella;

    /// <inheritdoc />
    public bool TryRead(string nome, out string segreto)
    {
        segreto = string.Empty;

        string percorso = Percorso(nome);

        if (!File.Exists(percorso))
        {
            return false;
        }

        Verifica(percorso);

        segreto = File.ReadAllText(percorso, Encoding.UTF8).Trim();

        return segreto.Length > 0;
    }

    /// <inheritdoc />
    public void Write(string nome, string segreto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segreto);

        string percorso = Percorso(nome);

        Directory.CreateDirectory(cartella, CartellaSoloProprietario);

        // La cartella puo' esistere gia' da prima, con permessi ereditati piu' larghi:
        // CreateDirectory non li corregge, e un segreto dentro una cartella attraversabile da
        // altri e' protetto solo finche' nessuno prova.
        File.SetUnixFileMode(cartella, CartellaSoloProprietario);

        // Si scrive su un file temporaneo e poi si sposta: sovrascrivere sul posto lascerebbe
        // il segreto vecchio troncato a meta' se il processo muore, e ricrearlo lascerebbe una
        // finestra senza segreto. Il modo si passa alla CREAZIONE, quindi il file non esiste
        // mai con permessi piu' larghi.
        string temporaneo = percorso + ".nuovo";

        using (FileStream flusso = new(temporaneo, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = SoloProprietario,
        }))
        {
            flusso.Write(Encoding.UTF8.GetBytes(segreto));
            flusso.Flush(flushToDisk: true);
        }

        File.SetUnixFileMode(temporaneo, SoloProprietario);
        File.Move(temporaneo, percorso, overwrite: true);
    }

    /// <inheritdoc />
    public bool Delete(string nome)
    {
        string percorso = Percorso(nome);

        if (!File.Exists(percorso))
        {
            return false;
        }

        File.Delete(percorso);

        return true;
    }

    private static void Verifica(string percorso)
    {
        UnixFileMode modo = File.GetUnixFileMode(percorso);

        if ((modo & Altrui) != 0)
        {
            throw new SecretStoreException(
                $"The token file {percorso} is readable by someone other than you ({modo}). " +
                $"Observer will not use it. Fix it with: chmod 600 \"{percorso}\"");
        }

        string? contenitore = Path.GetDirectoryName(percorso);

        if (contenitore is null)
        {
            return;
        }

        UnixFileMode modoCartella = File.GetUnixFileMode(contenitore);

        // La cartella conta quanto il file: chi puo' scriverci dentro puo' sostituire il file
        // con uno suo, e da quel momento la dashboard presenterebbe alle macchine remote un
        // token scelto da qualcun altro.
        if ((modoCartella & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new SecretStoreException(
                $"The folder {contenitore} is writable by others ({modoCartella}), so the token " +
                $"inside it can be replaced. Observer will not use it. Fix it with: " +
                $"chmod 700 \"{contenitore}\"");
        }
    }

    private string Percorso(string nome) => Path.Combine(cartella, SecretName.Valida(nome));
}
