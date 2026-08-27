namespace Observer.Service.Credentials;

/// <summary>Dove vive il deposito, e come si mette in sicurezza la sua cartella.</summary>
public static class CredentialDirectory
{
    /// <summary>Il nome del file del deposito.</summary>
    public const string NomeFile = "credentials.json";

    // 0700: solo il proprietario, che in produzione e' root perche' il servizio gira come root.
    // .NET non offre chown, ma non serve: il proprietario e' giusto per costruzione.
    private const UnixFileMode ModoCartella =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Il percorso predefinito del deposito su questo sistema.</summary>
    /// <returns>Il percorso completo del file.</returns>
    public static string PercorsoPredefinito() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Observer",
                NomeFile)
            : Path.Combine("/etc", "observer", NomeFile);

    /// <summary>Porta la cartella del deposito in uno stato in cui puo' ospitare un segreto.</summary>
    /// <param name="percorsoDelFile">Il percorso del file del deposito.</param>
    /// <exception cref="InvalidOperationException">Se non e' possibile.</exception>
    public static void Prepara(string percorsoDelFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDelFile);

        string? cartella = Path.GetDirectoryName(percorsoDelFile);

        if (string.IsNullOrEmpty(cartella))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsDirectoryTrust.Prepara(cartella);
            return;
        }

        Directory.CreateDirectory(cartella);

        if (OperatingSystem.IsLinux())
        {
            // La creazione NON applica il modo a una cartella che esiste gia': verificato, e'
            // un no-op silenzioso. Senza questa seconda riga la protezione non esisterebbe dal
            // secondo avvio in poi, ne' su una cartella preparata da un installer.
            File.SetUnixFileMode(cartella, ModoCartella);
        }
    }
}