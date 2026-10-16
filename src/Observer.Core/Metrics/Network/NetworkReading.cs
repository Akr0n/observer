namespace Observer.Core.Metrics.Network;

/// <summary>
/// The cumulative counters of ONE network interface: bytes received and bytes sent.
/// </summary>
/// <remarks>
/// These are counters since the interface came up, not measurements: on their own they say
/// nothing useful. The value comes from the difference between two readings, and
/// <see cref="NetworkRates"/> is what turns that difference into bytes per second.
/// <para>
/// The instance is a network interface: <c>Ethernet</c> on Windows, <c>eth0</c> on Linux. It
/// must stay stable from one sample to the next, or the series breaks in two.
/// </para>
/// </remarks>
/// <param name="Instance">
/// What the interface is called for whoever is looking. Must be stable across samples.
/// </param>
/// <param name="BytesReceived">Bytes received since the interface came up.</param>
/// <param name="BytesSent">Bytes sent since the interface came up.</param>
public readonly record struct NetworkReading(
    string Instance,
    ulong BytesReceived,
    ulong BytesSent)
{
    /// <summary>
    /// Builds a reading, validating the instance name.
    /// </summary>
    /// <param name="instance">Interface name.</param>
    /// <param name="bytesReceived">Bytes received since the interface came up.</param>
    /// <param name="bytesSent">Bytes sent since the interface came up.</param>
    /// <returns>The reading.</returns>
    public static NetworkReading Create(string instance, ulong bytesReceived, ulong bytesSent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);

        return new NetworkReading(instance, bytesReceived, bytesSent);
    }
}

/// <summary>
/// The port that reads the network interface counters.
/// </summary>
/// <remarks>
/// Kept separate from the other providers on purpose: network traffic is measured on
/// INTERFACES, not on volumes or physical disks, and the two are read from different places
/// under different names. A single port would force one of them to pretend it knows the other.
/// </remarks>
public interface INetworkInterfaceProvider
{
    /// <summary>False when on this platform nothing is measured at all.</summary>
    bool IsSupported { get; }

    /// <summary>Why nothing is measured, when nothing is measured.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Reads the counters. False when the reading fails entirely.</summary>
    bool TryRead(out IReadOnlyList<NetworkReading> readings);
}
