namespace Observer.Service.Credentials;

/// <summary>Where the credentials in use came from.</summary>
public enum CredentialOrigin
{
    /// <summary>Ephemeral: generated in memory and never stored. They hold for this run only.</summary>
    Ephemeral = 0,

    /// <summary>From an explicit token in configuration.</summary>
    Configuration,

    /// <summary>Read from the store on disk.</summary>
    Stored,

    /// <summary>Generated now and stored on disk.</summary>
    CreatedAndStored,
}

/// <summary>The credentials in use, with where they came from.</summary>
/// <param name="Credentials">The credentials.</param>
/// <param name="Origin">Where they come from.</param>
/// <param name="Path">The store used, or null if there is none.</param>
public sealed record ProvisionedCredentials(
    MachineCredentials Credentials,
    CredentialOrigin Origin,
    string? Path);

/// <summary>
/// Provides the service with its own machine token.
/// </summary>
/// <remarks>
/// This is the piece that makes an installer possible. As long as the service demands a token in
/// configuration, whoever installs it has to generate one — that is, know it, record it in their
/// own log, and leave it behind if they fail halfway.
/// </remarks>
public static class CredentialProvisioning
{
    /// <summary>Provides the credentials following the precedence that was decided.</summary>
    /// <param name="configuredToken">The explicit token, if configured.</param>
    /// <param name="storePath">The path of the store.</param>
    /// <param name="runningAsService">Whether the process is registered as a system service.</param>
    /// <returns>The credentials and where they came from.</returns>
    /// <exception cref="InvalidOperationException">
    /// When it runs as a service and the store cannot be secured.
    /// </exception>
    public static ProvisionedCredentials Provision(
        string? configuredToken,
        string storePath,
        bool runningAsService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            // Explicit configuration wins over everything: it is the backwards compatibility, and
            // it is what keeps the tests and CI working.
            return new ProvisionedCredentials(
                new MachineCredentials(configuredToken.Trim(), null, null),
                CredentialOrigin.Configuration,
                null);
        }

        try
        {
            CredentialDirectory.Prepare(storePath);

            if (CredentialStore.Read(storePath) is { } stored)
            {
                return new ProvisionedCredentials(stored, CredentialOrigin.Stored, storePath);
            }

            MachineCredentials created = MachineCredentials.Create();
            CredentialStore.Write(storePath, created);

            return new ProvisionedCredentials(created, CredentialOrigin.CreatedAndStored, storePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (runningAsService)
            {
                throw new InvalidOperationException(RefusalMessage(storePath), error);
            }

            // Launched by hand. EPHEMERAL token, in memory, never written: never a per-user
            // fallback on disk, which would move the secret to a less protected place while
            // making it look like it had been put somewhere safe.
            return new ProvisionedCredentials(MachineCredentials.Create(), CredentialOrigin.Ephemeral, null);
        }
        catch (InvalidOperationException) when (!runningAsService && !IsStoreDamaged(storePath))
        {
            return new ProvisionedCredentials(MachineCredentials.Create(), CredentialOrigin.Ephemeral, null);
        }
    }

    /// <summary>A store that exists but cannot be interpreted must never be overwritten.</summary>
    private static bool IsStoreDamaged(string path)
    {
        try
        {
            return File.ReadAllText(path).Length > 0;
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

    private static string RefusalMessage(string path) =>
        $"Observer runs as a system service and can't secure its credential store at '{path}'. " +
        "It will not start: depositing a machine token that other accounts can read would be " +
        "worse than not starting at all, because nothing would report it. Check that the " +
        "directory is not a junction, that it is owned by SYSTEM or Administrators, and that " +
        "no other account is granted access.";
}