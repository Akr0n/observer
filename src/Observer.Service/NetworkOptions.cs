using System.Globalization;

namespace Observer.Service;

/// <summary>
/// How the service makes itself reachable from the OTHER machines.
/// </summary>
/// <remarks>
/// The local channel does not go through here: it has neither a port nor a certificate, and
/// whoever watches the machine they are sitting at never touches the network.
/// </remarks>
public sealed class NetworkOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Observer:Network";

    /// <summary>The default HTTPS port.</summary>
    public const int DefaultPort = 5058;

    /// <summary>
    /// Whether to expose HTTPS to the other machines. On by default.
    /// </summary>
    /// <remarks>
    /// It is switched off in the tests, where the transport is fake and generating an RSA key at
    /// every start of the host would cost seconds for nothing.
    /// </remarks>
    public bool Https { get; set; } = true;

    /// <summary>The port to listen on for HTTPS.</summary>
    public int HttpsPort { get; set; } = DefaultPort;

    /// <summary>It checks the options before they open a port.</summary>
    /// <exception cref="InvalidOperationException">If the port is not usable.</exception>
    public void Validate()
    {
        if (Https && HttpsPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "Observer:Network:HttpsPort is " +
                HttpsPort.ToString(CultureInfo.InvariantCulture) +
                ", which is not a usable TCP port. Remove it to use the default (" +
                DefaultPort.ToString(CultureInfo.InvariantCulture) + ").");
        }
    }
}
