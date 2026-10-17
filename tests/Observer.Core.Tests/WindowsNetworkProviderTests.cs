using Observer.Core.Metrics.Network;
using Observer.Core.Platform.Windows;

namespace Observer.Core.Tests;

/// <summary>A fact that is SKIPPED outside Linux instead of failing.</summary>
/// <remarks>
/// Twin of the attribute of the same name in <c>Observer.Service.Tests/PlatformOnly.cs</c>:
/// duplicated and not shared because the two test projects do not reference each other, and
/// twenty duplicated lines are a lower price than a dependency between test assemblies.
/// </remarks>
public sealed class LinuxOnlyAttribute : FactAttribute
{
    /// <summary>Skips when the system is not Linux.</summary>
    public LinuxOnlyAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This branch exists only off Windows: it runs on ubuntu-latest.";
        }
    }
}

/// <summary>
/// The Windows network provider: a smoke test on Windows, and the off-Windows branch
/// everywhere else. The filtered enumeration is not unit-testable without real adapters
/// (NetworkInterface.GetAllNetworkInterfaces is a static over the OS), so these tests
/// pin the contract — supported everywhere, readable on Windows, a clean false
/// elsewhere — rather than the filtering itself.
/// </summary>
public class WindowsNetworkProviderTests
{
    [WindowsOnly]
    public void TryRead_OnWindows_ReturnsTrue()
    {
        WindowsNetworkProvider provider = new();

        // True even with zero usable adapters: no Ethernet/Wi-Fi up is an answer
        // ("nothing to report"), not a read failure. The collector tells the two apart.
        Assert.True(provider.TryRead(out IReadOnlyList<NetworkReading> readings));
        Assert.NotNull(readings);
    }

    [WindowsOnly]
    public void TryRead_OnWindows_TwiceInARow_IsConsistent()
    {
        // The second read hits the cached adapter list: it must answer the same way
        // as the first instead of failing or throwing.
        WindowsNetworkProvider provider = new();

        Assert.True(provider.TryRead(out IReadOnlyList<NetworkReading> first));
        Assert.True(provider.TryRead(out IReadOnlyList<NetworkReading> second));

        Assert.Equal(first.Count, second.Count);
    }

    [LinuxOnly]
    public void TryRead_OutsideWindows_ReturnsFalseWithNoReadings()
    {
        WindowsNetworkProvider provider = new();

        Assert.False(provider.TryRead(out IReadOnlyList<NetworkReading> readings));
        Assert.Empty(readings);
    }
}
