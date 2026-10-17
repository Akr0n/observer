using System.Net.NetworkInformation;
using Observer.Core.Metrics.Network;

namespace Observer.Core.Platform.Windows;

/// <summary>
/// Windows adapter for the network interface counters, through
/// <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.
/// </summary>
/// <remarks>
/// Chosen over the IP Helper API (<c>GetIfTable2</c>) for two measured reasons. The first: no
/// P/Invoke is needed — <c>NetworkInterface</c> is built into .NET and works without any extra
/// package. The second: the 64-bit counters of <c>MIB_IF_ROW2</c> live at an offset past a
/// struct that is over 1 KB long, and defining it field by field is the kind of maintenance
/// burden that a monitoring tool does not need when the managed API is sufficient.
/// <para>
/// The managed API reads the 32-bit <c>InOctets</c> and <c>OutOctets</c> from
/// <c>MIB_IF_ROW2</c> and returns them as <see cref="long"/>. On a 1 Gbps connection at full
/// utilisation the 32-bit counter wraps every ~34 seconds; the collector treats the wrap as a
/// <see cref="SampleFailure.CounterWentBackwards"/> and resumes after one warm-up round,
/// losing one second of visibility — an acceptable trade-off for a monitoring tool against the
/// complexity of defining the full native struct.
/// </para>
/// </remarks>
public sealed class WindowsNetworkProvider : INetworkInterfaceProvider
{
    /// <summary>
    /// How long the filtered adapter list is reused. Enumerating adapters hits the IP Helper
    /// API and the registry every time; the per-second counters are still read live on every
    /// round, only the discovery is cached.
    /// </summary>
    /// <remarks>
    /// Two bounds, and both are deliberate. The steady one is the interval below: a new
    /// adapter appears with up to this delay, and a removed one is pruned on the next
    /// refresh. The prompt one is the operating system's own notification: address and
    /// availability changes invalidate the list at once, so a cable pulled or a Wi-Fi
    /// dropped is picked up on the next round instead of at the next interval. The
    /// <see cref="OperationalStatus"/> of a cached adapter is the snapshot taken at
    /// refresh time (the runtime caches it per <c>NetworkInterface</c> instance), not a
    /// live reading — the promptness for the down case comes from the invalidation above,
    /// not from re-reading the property.
    /// </remarks>
    private static readonly TimeSpan ListRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock;
    private List<CachedInterface> cache = [];
    private DateTimeOffset lastRefresh = DateTimeOffset.MinValue;
    private bool cacheInitialized;

    // Set by the OS network-change callbacks, read by the sampler (0 = calm, 1 = refresh).
    // The sampler calls TryRead from its single loop (see MetricSamplingService), so the
    // int + Interlocked pair below is enough: if this provider is ever read from two
    // threads at once, the three cache fields need a lock first.
    private int refreshRequested;

    /// <summary>Creates the provider.</summary>
    /// <param name="timeProvider">The clock for the refresh interval, or null for the system one.</param>
    public WindowsNetworkProvider(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;

        // One subscription per instance, and the composition creates the instance once, so
        // in production there is exactly one handler. Each handler keeps its own instance
        // reachable until process exit; the instances are tiny, and test doubles that
        // construct many of them stay cheap because each handler only flips its own flag.
        if (OperatingSystem.IsWindows())
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Interlocked.Exchange(ref refreshRequested, 1);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Interlocked.Exchange(ref refreshRequested, 1);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always true, even outside Windows: the platform is a parameter of the composition, not a
    /// reading of the environment, and the tests must be able to build this provider from the
    /// Linux runner. Whoever is not on Windows never gets to use it, because
    /// <c>ObserverMetrics.CreateCollectors</c> picks another branch.
    /// </remarks>
    public bool IsSupported => true;

    /// <inheritdoc />
    public string? UnsupportedReason => null;

    /// <inheritdoc />
    public bool TryRead(out IReadOnlyList<NetworkReading> readings)
    {
        if (!OperatingSystem.IsWindows())
        {
            readings = [];

            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();

        // Consumed atomically: an event arriving during the refresh below sets the flag
        // again instead of being lost in the reset.
        bool eventRefresh = Interlocked.Exchange(ref refreshRequested, 0) != 0;

        if (!cacheInitialized || eventRefresh || now - lastRefresh >= ListRefreshInterval)
        {
            // A failed enumeration keeps the previous list AND the event request, so the
            // next round retries promptly instead of waiting out the interval.
            if (!RefreshCache(now) && eventRefresh)
            {
                Interlocked.Exchange(ref refreshRequested, 1);
            }
        }

        if (!cacheInitialized)
        {
            // The very first enumeration failed: that is "I could not read", not "this
            // machine has no interfaces". Returning true with an empty list here would
            // make the collector publish Ok + "no interface", masking the failure.
            readings = [];

            return false;
        }

        List<NetworkReading> found = new(cache.Count);

        foreach (CachedInterface cached in cache)
        {
            // Refresh-time snapshot, not a live reading: the runtime captures
            // OperationalStatus when the NetworkInterface instance is created. An adapter
            // that went down after the refresh is caught because the OS notification
            // above already invalidated the list, not because this property moved.
            // Kept as a defensive second net for a refresh that raced the event.
            if (cached.Adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceStatistics stats;

            try
            {
                stats = cached.Adapter.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                // One interface that cannot be read does not bring the others down.
                // It stays in the cache until the next refresh prunes it if gone.
                continue;
            }

            found.Add(NetworkReading.Create(
                cached.DisplayName,
                (ulong)Math.Max(0, stats.BytesReceived),
                (ulong)Math.Max(0, stats.BytesSent)));
        }

        readings = found;

        // Zero interfaces is not a READ failure: it is an answer, and the collector can tell
        // it apart from "I could not read".
        return true;
    }

    /// <summary>Rebuilds the cached adapter list.</summary>
    /// <returns>True when the enumeration succeeded (even with zero usable adapters).</returns>
    private bool RefreshCache(DateTimeOffset now)
    {
        NetworkInterface[] interfaces;

        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            // Keep the previous list: a failed enumeration must not wipe known adapters.
            // lastRefresh is left alone so the next round retries instead of waiting out
            // the interval on an empty or stale list.
            return false;
        }

        List<CachedInterface> next = new(interfaces.Length);

        foreach (NetworkInterface nic in interfaces)
        {
            // Loopback is the same machine talking to itself: including it would make a
            // local-only chat look like network traffic.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            // Only keep "real" network interfaces: Ethernet and Wi-Fi. Everything else
            // (tunnels, virtual adapters, filter drivers) would clutter the dashboard with
            // dozens of entries that show the same traffic multiple times.
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            {
                continue;
            }

            // An interface that is down has no traffic to report. Including it would publish
            // zero for a reason that is not "idle" but "disconnected", and the two must stay
            // distinguishable.
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            // The friendly description is what the user recognises: "Ethernet" instead of a
            // GUID, "Wi-Fi" instead of "802.11n Wireless Adapter". On Windows Description is
            // human-readable; when it is empty the system name is a safer fallback.
            string name = !string.IsNullOrWhiteSpace(nic.Description)
                ? nic.Description
                : nic.Name;

            // Skip virtual adapters and filter drivers. These are reported as Ethernet/Wi-Fi
            // by the API but they duplicate the traffic of the real physical interface.
            if (IsVirtualOrFilterDriver(name))
            {
                continue;
            }

            next.Add(new CachedInterface(nic, name));
        }

        cache = next;
        lastRefresh = now;
        cacheInitialized = true;

        return true;
    }

    private sealed record CachedInterface(NetworkInterface Adapter, string DisplayName);

    /// <summary>
    /// Checks whether the interface name belongs to a virtual adapter or a filter driver.
    /// These interfaces duplicate the traffic of the real physical interface and would clutter
    /// the dashboard with dozens of entries showing the same numbers.
    /// </summary>
    private static bool IsVirtualOrFilterDriver(string name)
    {
        // Filter drivers append suffixes like "-WFP Native MAC Layer LightWeight Filter-0000"
        // or "-QoS Packet Scheduler-0000" to the base interface name.
        if (name.Contains("-WFP", StringComparison.Ordinal) ||
            name.Contains("-QoS", StringComparison.Ordinal) ||
            name.Contains("Filter Driver", StringComparison.Ordinal) ||
            name.Contains("-Virtual", StringComparison.Ordinal) ||
            name.Contains("Filter-0000", StringComparison.Ordinal))
        {
            return true;
        }

        // Virtual adapters: Hyper-V virtual switches, WAN Miniport (tunnel endpoints), etc.
        if (name.Contains("Hyper-V", StringComparison.Ordinal) ||
            name.Contains("WAN Miniport", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
