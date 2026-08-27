using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Apre il canale locale sulla piattaforma corrente.</summary>
/// <remarks>
/// Punto d'ingresso cross-platform: il codice specifico di ogni sistema sta nelle classi
/// annotate, e qui ci sono solo le guardie. Non puo' vivere nei top-level statements di
/// Program.cs perche' [SupportedOSPlatform] non li copre.
/// </remarks>
public static class LocalChannelSetup
{
    /// <summary>Configura l'ascolto locale.</summary>
    /// <param name="builder">Il builder dell'applicazione.</param>
    /// <param name="opzioni">Nome della pipe e percorso del socket, gia' convalidati.</param>
    /// <returns>Il percorso del socket effettivamente usato su Linux, altrimenti null.</returns>
    public static async Task<string?> ConfiguraAsync(WebApplicationBuilder builder, LocalChannelOptions opzioni)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(opzioni);

        if (!opzioni.Enabled)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsNamedPipe.Ascolta(builder, opzioni.PipeName);
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            string percorso = await PercorsoUtilizzabileAsync(opzioni.SocketPath).ConfigureAwait(false);

            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(percorso));

            return percorso;
        }

        return null;
    }

    /// <summary>Il primo percorso che questo processo riesce davvero a preparare.</summary>
    /// <remarks>
    /// /run/observer non e' creabile da un utente normale, e "dotnet run" durante lo sviluppo
    /// gira come utente normale su meta' della CI. Senza un ripiego il servizio non sarebbe
    /// avviabile fuori da systemd. Chi lo esegue deve pero' sapere DOVE e' finito il socket:
    /// per questo il percorso scelto viene restituito e stampato dal chiamante, invece di
    /// restare un dettaglio interno.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    private static async Task<string> PercorsoUtilizzabileAsync(string preferito)
    {
        List<string> tentati = [];

        foreach (string candidato in Candidati(preferito))
        {
            tentati.Add(candidato);

            if (EndpointUrl.Problema("http://unix:" + candidato) is not null)
            {
                continue;
            }

            try
            {
                LinuxUnixSocket.PreparaPercorso(candidato);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            await LinuxUnixSocket.BonificaSocketOrfanoAsync(candidato, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            return candidato;
        }

        throw new InvalidOperationException(
            "None of these unix socket paths could be prepared: " + string.Join(", ", tentati) +
            ". Set " + LocalChannelOptions.SectionName + ":SocketPath to a directory this " +
            "process can write to, or set Enabled to false.");
    }

    private static IEnumerable<string> Candidati(string preferito)
    {
        yield return preferito;

        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg)
        {
            yield return Path.Combine(xdg, "observer", "observer.sock");
        }

        yield return Path.Combine(Path.GetTempPath(), "observer", "observer.sock");
    }
}