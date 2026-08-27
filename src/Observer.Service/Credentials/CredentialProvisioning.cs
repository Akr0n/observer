namespace Observer.Service.Credentials;

/// <summary>Da dove sono arrivate le credenziali in uso.</summary>
public enum CredentialOrigin
{
    /// <summary>Effimere: generate in memoria e mai depositate. Valgono per questa esecuzione.</summary>
    Effimero = 0,

    /// <summary>Da un token esplicito in configurazione.</summary>
    Configurazione,

    /// <summary>Lette dal deposito su disco.</summary>
    Deposito,

    /// <summary>Generate adesso e depositate su disco.</summary>
    GeneratoEDepositato,
}

/// <summary>Le credenziali in uso, con la loro provenienza.</summary>
/// <param name="Credentials">Le credenziali.</param>
/// <param name="Origin">Da dove arrivano.</param>
/// <param name="Percorso">Il deposito usato, oppure null se non ce n'e' uno.</param>
public sealed record ProvisionedCredentials(
    MachineCredentials Credentials,
    CredentialOrigin Origin,
    string? Percorso);

/// <summary>
/// Procura al servizio il proprio token di macchina.
/// </summary>
/// <remarks>
/// E' il pezzo che rende possibile un installer. Finche' il servizio pretende un token in
/// configurazione, chi installa deve generarne uno — cioe' conoscerlo, registrarlo nel proprio
/// log, e lasciarselo dietro se fallisce a meta'.
/// </remarks>
public static class CredentialProvisioning
{
    /// <summary>Procura le credenziali secondo la precedenza stabilita.</summary>
    /// <param name="tokenDaConfigurazione">Il token esplicito, se configurato.</param>
    /// <param name="percorsoDeposito">Il percorso del deposito.</param>
    /// <param name="giraComeServizio">Se il processo e' registrato come servizio di sistema.</param>
    /// <returns>Le credenziali e la loro provenienza.</returns>
    /// <exception cref="InvalidOperationException">
    /// Quando gira come servizio e il deposito non puo' essere messo in sicurezza.
    /// </exception>
    public static ProvisionedCredentials Provvedi(
        string? tokenDaConfigurazione,
        string percorsoDeposito,
        bool giraComeServizio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorsoDeposito);

        if (!string.IsNullOrWhiteSpace(tokenDaConfigurazione))
        {
            // La configurazione esplicita vince su tutto: e' la retrocompatibilita', ed e' cio'
            // che tiene in piedi i test e la CI.
            return new ProvisionedCredentials(
                new MachineCredentials(tokenDaConfigurazione.Trim(), null, null),
                CredentialOrigin.Configurazione,
                null);
        }

        try
        {
            CredentialDirectory.Prepara(percorsoDeposito);

            if (CredentialStore.Leggi(percorsoDeposito) is { } depositate)
            {
                return new ProvisionedCredentials(depositate, CredentialOrigin.Deposito, percorsoDeposito);
            }

            MachineCredentials nuove = MachineCredentials.Nuove();
            CredentialStore.Scrivi(percorsoDeposito, nuove);

            return new ProvisionedCredentials(nuove, CredentialOrigin.GeneratoEDepositato, percorsoDeposito);
        }
        catch (Exception errore) when (errore is IOException or UnauthorizedAccessException)
        {
            if (giraComeServizio)
            {
                throw new InvalidOperationException(TestoRifiuto(percorsoDeposito), errore);
            }

            // Lanciato a mano. Token EFFIMERO, in memoria, mai scritto: mai un ripiego
            // per-utente su disco, che sposterebbe il segreto in un posto meno protetto
            // facendo credere di averlo messo al sicuro.
            return new ProvisionedCredentials(MachineCredentials.Nuove(), CredentialOrigin.Effimero, null);
        }
        catch (InvalidOperationException) when (!giraComeServizio && !DepositoDanneggiato(percorsoDeposito))
        {
            return new ProvisionedCredentials(MachineCredentials.Nuove(), CredentialOrigin.Effimero, null);
        }
    }

    /// <summary>Un deposito che esiste ma non si riesce a interpretare non va mai sovrascritto.</summary>
    private static bool DepositoDanneggiato(string percorso)
    {
        try
        {
            return File.ReadAllText(percorso).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string TestoRifiuto(string percorso) =>
        $"Observer runs as a system service and can't secure its credential store at '{percorso}'. " +
        "It will not start: depositing a machine token that other accounts can read would be " +
        "worse than not starting at all, because nothing would report it. Check that the " +
        "directory is not a junction, that it is owned by SYSTEM or Administrators, and that " +
        "no other account is granted access.";
}