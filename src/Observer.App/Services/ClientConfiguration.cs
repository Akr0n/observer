using System.Text.Json;
using Observer.Core.Security;
using System.Text.Json.Serialization;

namespace Observer.App.Services;

/// <summary>
/// Outcome of reading the configuration: either the endpoint to query, or the sentence to
/// show on screen. Never both null.
/// </summary>
/// <param name="Endpoint">The service to query, or null.</param>
/// <param name="Problem">The explanation when <paramref name="Endpoint"/> is null.</param>
public sealed record ClientConfigurationResult(ObserverEndpoint? Endpoint, string? Problem);

/// <summary>
/// Contents of the client's local configuration file.
/// </summary>
/// <param name="BaseAddress">The service address. Optional.</param>
/// <param name="ApiToken">Access token. Optional if it is present in the environment.</param>
/// <param name="Fingerprint">
/// That machine's certificate fingerprint. Required for a remote endpoint: without it, the
/// connection would be encrypted but with no idea WHO is on the other end.
/// </param>
public sealed record ObserverClientFile(
    [property: JsonPropertyName("baseAddress")] string? BaseAddress,
    [property: JsonPropertyName("apiToken")] string? ApiToken,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint);

/// <summary>
/// Decides where the client gets its address and token from.
/// </summary>
/// <remarks>
/// The part that decides (<see cref="Resolve"/>) is a pure function of its inputs:
/// it reads neither the environment nor the disk, so it can be checked with a test instead
/// of by starting the application and looking at it.
/// </remarks>
public static class ClientConfiguration
{
    /// <summary>Environment variable holding the token. Same name the service uses.</summary>
    public const string TokenVariable = "Observer__ApiToken";

    /// <summary>Environment variable holding the service address.</summary>
    public const string BaseAddressVariable = "Observer__BaseAddress";

    /// <summary>Environment variable holding that machine's certificate fingerprint.</summary>
    /// <remarks>
    /// It exists for symmetry with the other two, and it is not a convenience: without it, making
    /// the fingerprint mandatory would have made the whole environment-variable route unusable,
    /// which is the only practicable one where the configuration file cannot be written.
    /// </remarks>
    public const string FingerprintVariable = "Observer__Fingerprint";

    /// <summary>An example address, for the messages. It is NOT a default value any more.</summary>
    /// <remarks>
    /// With no address configured the client uses the LOCAL channel, which has neither a port nor
    /// a token. An address is set only to watch ANOTHER computer.
    /// </remarks>
    public const string ExampleAddress = "https://another-machine:5058/";

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Path of the client's configuration file: on Windows
    /// <c>%LOCALAPPDATA%\Observer\client.json</c>, on Linux
    /// <c>~/.local/share/Observer/client.json</c>.
    /// It sits OUTSIDE the repository on purpose, so the token cannot end up in a commit.
    /// </summary>
    /// <remarks>
    /// LocalApplicationData and not ApplicationData, that is Local and not Roaming: on a
    /// domain-joined machine the Roaming folder is synchronised with a file server, so the token
    /// would cross the network and be left sitting somewhere else. A secret tied to ONE machine
    /// must not follow the user from one computer to another.
    /// </remarks>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Observer",
        "client.json");

    /// <summary>
    /// Actually reads the environment and the disk and produces the configuration.
    /// </summary>
    public static ClientConfigurationResult Read() =>
        Resolve(
            Environment.GetEnvironmentVariable(TokenVariable),
            Environment.GetEnvironmentVariable(BaseAddressVariable),
            Environment.GetEnvironmentVariable(FingerprintVariable),
            ReadFile(FilePath));

    /// <summary>
    /// Combines the environment and the file by the established precedence, without touching the disk.
    /// </summary>
    /// <param name="tokenFromEnvironment">Value of <see cref="TokenVariable"/>, if present.</param>
    /// <param name="baseAddressFromEnvironment">Value of <see cref="BaseAddressVariable"/>, if present.</param>
    /// <param name="fingerprintFromEnvironment">Value of <see cref="FingerprintVariable"/>, if present.</param>
    /// <param name="fileContent">Raw contents of the configuration file, if it exists.</param>
    /// <remarks>
    /// The environment WINS over the file, for the same reason it wins in the service: an old
    /// token forgotten in the file would silently override the newly exported one, and the
    /// symptom would be an unexplained 401.
    /// </remarks>
    public static ClientConfigurationResult Resolve(
        string? tokenFromEnvironment,
        string? baseAddressFromEnvironment,
        string? fingerprintFromEnvironment,
        string? fileContent)
    {
        ObserverClientFile? file;

        try
        {
            file = string.IsNullOrWhiteSpace(fileContent)
                ? null
                : JsonSerializer.Deserialize<ObserverClientFile>(fileContent, FileOptions);
        }
        catch (JsonException ex)
        {
            return new ClientConfigurationResult(
                null,
                $"The configuration file {FilePath} isn't valid JSON ({ex.Message}). " +
                $"It must contain exactly: {{ \"baseAddress\": \"{ExampleAddress}\", \"apiToken\": \"the other machine's token\" }}");
        }

        string? address = FirstNonEmpty(baseAddressFromEnvironment, file?.BaseAddress);

        if (address is null)
        {
            // NO address configured means "watch the machine you are sitting at", and there the
            // service asks for no token at all. That is what makes the dashboard installable:
            // after the install there is nothing to configure.
            // A token exported by mistake does NOT divert the client: it is ignored here.
            return new ClientConfigurationResult(ObserverEndpoint.LocalChannel(), null);
        }

        if (!Uri.TryCreate(WithTrailingSlash(address), UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme != Uri.UriSchemeHttps)
        {
            // http:// is NOT accepted any more, and that is not gratuitous hardening: the
            // service no longer answers in the clear over the network. Accepting it here would
            // mean sending the token once a second to a port that is not there, or - worse - to
            // something answering in its place.
            return new ClientConfigurationResult(
                null,
                $"The service address \"{address}\" can't be used. " +
                $"It must be a full https address, for example {ExampleAddress}. " +
                "Observer no longer answers in the clear over the network: the token used to " +
                "cross it once a second. " +
                $"Set it in the {BaseAddressVariable} environment variable, or in the " +
                $"\"baseAddress\" field of {FilePath}. " +
                "Remove it entirely to watch the machine you are sitting at.");
        }

        string? token = FirstNonEmpty(tokenFromEnvironment, file?.ApiToken);

        if (token is null)
        {
            // A remote address with no credential is not a case to guess at: that service
            // will reject every request, and saying so at once is better than showing a burst
            // of 401s once a second.
            return new ClientConfigurationResult(null, DescribeMissingToken(address));
        }

        string? fingerprint = FirstNonEmpty(fingerprintFromEnvironment, file?.Fingerprint);

        if (CertificateFingerprint.Normalize(fingerprint) is null)
        {
            // Encrypted is not enough. Without a fingerprint the connection is protected against
            // whoever listens but not against whoever stands in the middle, and that is the worst
            // case because it looks secure.
            return new ClientConfigurationResult(null, DescribeMissingFingerprint(address));
        }

        string origin = string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? $"from the file {FilePath}"
            : $"from the {TokenVariable} environment variable";

        return new ClientConfigurationResult(
            ObserverEndpoint.Remote(baseAddress, token, origin, fingerprint),
            null);
    }

    /// <summary>The text shown when the fingerprint is missing for a REMOTE service.</summary>
    /// <param name="address">The configured address.</param>
    /// <returns>The sentence to show.</returns>
    public static string DescribeMissingFingerprint(string address) =>
        $"No certificate fingerprint is configured for {address}. Observer's certificate is " +
        "self-signed, so without a fingerprint there is nothing to tell that machine apart from " +
        "anyone able to stand in the middle of the connection: the traffic would be encrypted, " +
        "but not to a known machine. Run \"observer share\" on THAT machine and put the value it " +
        $"prints in the \"fingerprint\" field of {FilePath}, next to the token.";

    /// <summary>The text shown when the token is missing for a REMOTE service.</summary>
    /// <param name="address">The configured address.</param>
    /// <returns>The sentence to show.</returns>
    public static string DescribeMissingToken(string address) =>
        $"No token is configured for {address}, so there is no point in trying to connect: " +
        "another machine's Observer rejects every request that isn't authenticated. Get its " +
        "token by running \"observer share\" on THAT machine, from an elevated terminal, then " +
        $"put it in the {TokenVariable} environment variable, or in the \"apiToken\" field of " +
        $"{FilePath}. " +
        "To watch the machine you are sitting at instead, remove the address entirely: no token " +
        "is needed for that.";

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            // An unreadable file is the same as a missing file: the useful diagnosis is the one
            // about the missing token, not the stack trace of a disk access.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred)
            ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim())
            : preferred.Trim();

    private static string WithTrailingSlash(string address) =>
        address.EndsWith('/') ? address : address + "/";
}