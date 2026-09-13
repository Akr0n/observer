using System.Globalization;
using System.Text;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Says whether a Kestrel endpoint URL is usable, before Kestrel tries it.
/// </summary>
/// <remarks>
/// PURE function: no I/O, no environment, so it can be verified with a table on both
/// runners instead of by starting a host.
/// <para>
/// It exists because the ways of getting it wrong are not equivalent. A relative socket path
/// makes start-up fail, and that is the good case. A Windows-style path inside "http://unix:"
/// does not fail at all: Kestrel binds [::]:80 on EVERY interface, with no exception and no
/// warning, and puts the machine's telemetry behind it.
/// </para>
/// </remarks>
public static class EndpointUrl
{
    /// <summary>Usable bytes in a unix socket path. <b>107, not 108.</b></summary>
    /// <remarks>
    /// The sockaddr_un struct has 108 bytes of sun_path, but one is needed for the terminator.
    /// .NET's message says "must be between 1 and 108 characters, inclusive" and it is false on
    /// two counts: the real limit is 107, and the count is in UTF-8 BYTES, not in characters.
    /// Verified by bisection: 107 accepted, 108 refused.
    /// </remarks>
    public const int MaxUnixSocketPathBytes = 107;

    private const string UnixPrefix = "unix:";
    private const string PipePrefix = "pipe:";

    /// <summary>The URL's problem, in English, or null if there are none.</summary>
    /// <param name="url">The URL exactly as it stands in configuration.</param>
    /// <returns>The sentence to show, or null if the URL is usable.</returns>
    public static string? Problem(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "An empty endpoint URL was configured. Remove the entry or give it a value.";
        }

        int separatorIndex = url.IndexOf("://", StringComparison.Ordinal);

        if (separatorIndex <= 0)
        {
            return UnusableUrl(url, "it has no scheme, so it isn't a URL at all");
        }

        string rest = url[(separatorIndex + 3)..];

        if (rest.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UnixProblem(url, rest[UnixPrefix.Length..]);
        }

        if (rest.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PipeProblem(url, rest[PipePrefix.Length..]);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? null
            : UnusableUrl(url, "it isn't a well-formed absolute URL");
    }

    private static string? UnixProblem(string url, string path)
    {
        if (!path.StartsWith('/'))
        {
            // The dangerous case: a Windows-style path lands here too. Without this check
            // Kestrel does not complain and opens port 80 on every interface.
            return UnusableUrl(
                url,
                "the unix socket path must be absolute and start with '/'. A Windows-style " +
                "path here does NOT fail: Kestrel silently listens on port 80 on every " +
                "network interface instead");
        }

        int pathBytes = Encoding.UTF8.GetByteCount(path);

        return pathBytes > MaxUnixSocketPathBytes
            ? UnusableUrl(
                url,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the unix socket path is {pathBytes} bytes long and the limit is {MaxUnixSocketPathBytes}. The limit counts UTF-8 bytes, not characters"))
            : null;
    }

    private static string? PipeProblem(string url, string name)
    {
        if (!name.StartsWith('/'))
        {
            return UnusableUrl(url, "a named pipe endpoint must be written as http://pipe:/<name>");
        }

        return name.Length > 1
            ? null
            : UnusableUrl(url, "the pipe name is missing after http://pipe:/");
    }

    private static string UnusableUrl(string url, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"The endpoint URL \"{url}\" can't be used: {reason}.");
}