namespace Observer.Service.Credentials;

/// <summary>Crea il file del deposito gia' con i permessi giusti.</summary>
/// <remarks>
/// "Gia'" e' la parola importante: creare il file e poi applicare i permessi lascia una
/// finestra in cui il segreto sta su disco con quelli ereditati dalla cartella.
/// </remarks>
public static class CredentialFile
{
    /// <summary>Crea un file nuovo, leggibile solo da chi deve.</summary>
    /// <param name="percorso">Il percorso del file da creare.</param>
    /// <returns>Il flusso su cui scrivere.</returns>
    /// <exception cref="IOException">Se il file esiste gia'.</exception>
    public static Stream CreaProtetto(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        if (OperatingSystem.IsWindows())
        {
            return WindowsCredentialFile.CreaProtetto(percorso);
        }

        // Su Unix il modo si passa alla creazione, quindi non esiste finestra. 0600: solo il
        // proprietario. Il servizio gira come root e il proprietario e' root per costruzione,
        // il che risparmia una chiamata a chown che .NET non offre.
        return new FileStream(percorso, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}