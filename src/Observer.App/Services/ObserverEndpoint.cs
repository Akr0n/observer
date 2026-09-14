namespace Observer.App.Services;

/// <summary>Where the client reaches an Observer service.</summary>
/// <remarks>
/// The ZERO value is <see cref="Local"/>: the local channel carries no secrets, so if a
/// forgotten field has to stand for something, let it stand for the one that cannot leak anything.
/// </remarks>
public enum EndpointKind
{
    /// <summary>The machine this dashboard runs on, over the local channel. No token.</summary>
    Local = 0,

    /// <summary>Another computer, over the network. That machine's token is required.</summary>
    Remote,
}

/// <summary>An Observer service to query.</summary>
/// <param name="Kind">Whether it is the local machine or another computer.</param>
/// <param name="BaseAddress">
/// Root of the service, always with the trailing slash: without it, <see cref="Uri"/> would
/// resolve "metrics/latest" by dropping the last segment of a base address like
/// "http://host:5057/observer/". For the local channel it is a FICTITIOUS host: the connection
/// is opened by the ConnectCallback, and this value only ends up in the Host header.
/// </param>
/// <param name="ApiToken">The token, only for remote endpoints. Null on the local channel.</param>
/// <param name="Origin">Where the configuration comes from, with no token inside.</param>
/// <param name="Fingerprint">
/// The fingerprint of the certificate that machine MUST present. Null on the local channel, which
/// does not cross the network and has nothing to encrypt.
/// </param>
/// <param name="Name">
/// What to call it in the list, if whoever wrote the configuration gave it a name. Null means
/// "use the address".
/// </param>
public sealed record ObserverEndpoint(
    EndpointKind Kind,
    Uri BaseAddress,
    string? ApiToken,
    string Origin,
    string? Fingerprint = null,
    string? Name = null)
{
    /// <summary>The name of the local channel, the same as the service's default.</summary>
    public const string LocalChannelName = "Observer";

    /// <summary>The path of the unix socket, the same as the service's default.</summary>
    public const string LocalSocketPath = "/run/observer/observer.sock";

    /// <summary>The machine this dashboard runs on.</summary>
    /// <returns>The local endpoint.</returns>
    public static ObserverEndpoint LocalChannel() =>
        new(
            EndpointKind.Local,
            // Fictitious host under .invalid, which by definition never resolves: it makes it
            // explicit that nobody should try to resolve it.
            new Uri("http://observer-local.invalid/"),
            null,
            "the local channel on this machine");

    /// <summary>Another computer.</summary>
    /// <param name="baseAddress">The root of the remote service.</param>
    /// <param name="token">That machine's token.</param>
    /// <param name="origin">Where the configuration comes from.</param>
    /// <returns>The remote endpoint.</returns>
    public static ObserverEndpoint Remote(
        Uri baseAddress,
        string token,
        string origin,
        string? fingerprint = null,
        string? name = null) =>
        new(EndpointKind.Remote, baseAddress, token, origin, fingerprint, name);

    /// <summary>What this endpoint is called on screen.</summary>
    /// <remarks>
    /// On the local channel it names no token, because there is none there: saying so would
    /// send the user looking for a credential that is not needed.
    /// </remarks>
    public string Description =>
        Kind == EndpointKind.Local
            ? "this machine"
            : BaseAddress.ToString();

    /// <summary>What this endpoint is called IN THE LIST of machines.</summary>
    /// <remarks>
    /// The hand-chosen name takes precedence over the address, because in a sidebar
    /// "https://192.168.1.24:5058/" tells nobody which machine it is.
    /// </remarks>
    /// <remarks>
    /// It is not the same as <see cref="Description"/>, and the difference is not a whim: that
    /// one lives INSIDE a sentence ("Connected to this machine"), this one is a list entry
    /// standing on its own and wants an initial capital.
    /// </remarks>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? (Kind == EndpointKind.Local ? "This machine" : Description)
            : Name.Trim();

    /// <summary>
    /// True when traffic to this endpoint is encrypted and the fingerprint is pinned.
    /// </summary>
    public bool IsFingerprintPinned => !string.IsNullOrWhiteSpace(Fingerprint);

    /// <summary>
    /// Hides the token. Records generate a ToString() with ALL the properties in it: without
    /// this override a careless binding or one log line would be enough to print the secret
    /// on the screen of whoever walks past.
    /// </summary>
    /// <returns>A description with no secrets in it.</returns>
    public override string ToString() =>
        FormattableString.Invariant($"ObserverEndpoint {{ {Kind}, {Description}, {Origin} }}");
}