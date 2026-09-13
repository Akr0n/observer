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
    /// <param name="options">Nome della pipe e path del socket, gia' convalidati.</param>
    /// <returns>Il path del socket effettivamente usato su Linux, altrimenti null.</returns>
    public static async Task<string?> ConfigureAsync(WebApplicationBuilder builder, LocalChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsNamedPipe.Listen(builder, options.PipeName);
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            string path = await PrepareUsablePathAsync(options.SocketPath).ConfigureAwait(false);

            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(path));

            return path;
        }

        return null;
    }

    /// <summary>Il primo path che questo processo riesce davvero a preparare.</summary>
    /// <remarks>
    /// /run/observer non e' creabile da un utente normale, e "dotnet run" durante lo sviluppo
    /// gira come utente normale su meta' della CI. Senza un ripiego il servizio non sarebbe
    /// avviabile fuori da systemd. Chi lo esegue deve pero' sapere DOVE e' finito il socket:
    /// per questo il path scelto viene restituito e stampato dal chiamante, invece di
    /// restare un dettaglio interno.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    private static async Task<string> PrepareUsablePathAsync(string preferred)
    {
        List<string> attemptedPaths = [];

        foreach (string candidate in CandidatePaths(preferred))
        {
            attemptedPaths.Add(candidate);

            if (EndpointUrl.Problem("http://unix:" + candidate) is not null)
            {
                continue;
            }

            try
            {
                LinuxUnixSocket.PreparePath(candidate);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            await LinuxUnixSocket.RemoveStaleSocketAsync(candidate, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            return candidate;
        }

        throw new InvalidOperationException(
            "None of these unix socket paths could be prepared: " + string.Join(", ", attemptedPaths) +
            ". Set " + LocalChannelOptions.SectionName + ":SocketPath to a directory this " +
            "process can write to, or set Enabled to false.");
    }

    private static IEnumerable<string> CandidatePaths(string preferred)
    {
        yield return preferred;

        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg)
        {
            yield return Path.Combine(xdg, "observer", "observer.sock");
        }

        yield return Path.Combine(Path.GetTempPath(), "observer", "observer.sock");
    }
}