using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Opens the local channel on the current platform.</summary>
/// <remarks>
/// Cross-platform entry point: the code specific to each system lives in the annotated
/// classes, and only the guards are here. It cannot live in Program.cs's top-level
/// statements because [SupportedOSPlatform] does not cover them.
/// </remarks>
public static class LocalChannelSetup
{
    /// <summary>Configures local listening.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="options">Pipe name and socket path, already validated.</param>
    /// <returns>The socket path actually used on Linux, null otherwise.</returns>
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

    /// <summary>The first path this process can actually prepare.</summary>
    /// <remarks>
    /// /run/observer cannot be created by a normal user, and "dotnet run" during development
    /// runs as a normal user on half of the CI. Without a fallback the service would not be
    /// startable outside systemd. Whoever runs it must however know WHERE the socket ended up:
    /// that is why the chosen path is returned and printed by the caller, instead of staying
    /// an internal detail.
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