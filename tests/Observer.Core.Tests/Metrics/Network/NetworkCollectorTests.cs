using Observer.Core.Metrics;
using Observer.Core.Metrics.Network;
using Observer.Core.Platform;

namespace Observer.Core.Tests.Metrics.Network;

public class NetworkCollectorTests
{
    [Fact]
    public async Task CollectAsync_FirstCall_ReturnsWarmup()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        provider.SetReadings(NetworkReading.Create("eth0", 1000UL, 500UL));
        var collector = new NetworkCollector(provider);

        // Act
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal("network", collector.Id);
        Assert.Equal(CollectorStatus.Warmup, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task CollectAsync_SecondCall_ReturnsMeasuredValues()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        provider.SetReadings(NetworkReading.Create("eth0", 1000UL, 500UL));
        var collector = new NetworkCollector(provider);

        // Act
        await collector.CollectAsync(CancellationToken.None);
        provider.SetReadings(NetworkReading.Create("eth0", 2000UL, 1000UL));
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Points.Count);
        
        var receivedPoint = snapshot.Points.First(p => p.MetricId == "network.received.bytespersecond");
        var sentPoint = snapshot.Points.First(p => p.MetricId == "network.sent.bytespersecond");
        
        Assert.Equal("eth0", receivedPoint.Instance);
        Assert.Equal("eth0", sentPoint.Instance);
        Assert.Equal(MetricValueKind.Number, receivedPoint.Value!.Value.Kind);
        Assert.Equal(MetricValueKind.Number, sentPoint.Value!.Value.Kind);
    }

    [Fact]
    public async Task CollectAsync_MultipleInterfaces_ReturnsAllInterfaces()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        provider.SetReadings(
            NetworkReading.Create("eth0", 1000UL, 500UL),
            NetworkReading.Create("eth1", 2000UL, 1000UL));
        var collector = new NetworkCollector(provider);

        // Act
        await collector.CollectAsync(CancellationToken.None);
        provider.SetReadings(
            NetworkReading.Create("eth0", 2000UL, 1000UL),
            NetworkReading.Create("eth1", 4000UL, 2000UL));
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Equal(4, snapshot.Points.Count); // 2 metrics × 2 interfaces
        
        var instances = snapshot.Points.Select(p => p.Instance).Distinct().ToList();
        Assert.Contains("eth0", instances);
        Assert.Contains("eth1", instances);
    }

    [Fact]
    public async Task CollectAsync_ProviderUnsupported_ReturnsUnsupportedStatus()
    {
        // Arrange
        var provider = new FakeNetworkProvider(isSupported: false, unsupportedReason: "Network monitoring not available");
        var collector = new NetworkCollector(provider);

        // Act
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Unsupported, snapshot.Status);
        Assert.Equal("Network monitoring not available", snapshot.Message);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task CollectAsync_ProviderReadFails_ReturnsUnavailableStatus()
    {
        // Arrange
        var provider = new FakeNetworkProvider(readFails: true);
        var collector = new NetworkCollector(provider);

        // Act
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Unavailable, snapshot.Status);
        Assert.NotNull(snapshot.Message);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task CollectAsync_EmptyReadings_ReturnsWarmupOnFirstCall()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        var collector = new NetworkCollector(provider);

        // Act
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Warmup, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task CollectAsync_EmptyReadings_ReturnsOkOnSecondCall()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        var collector = new NetworkCollector(provider);

        // Act
        await collector.CollectAsync(CancellationToken.None);
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(CollectorStatus.Ok, snapshot.Status);
        Assert.Empty(snapshot.Points);
    }

    [Fact]
    public async Task CollectAsync_InterfaceDisappears_RemovesOldPoints()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        provider.SetReadings(
            NetworkReading.Create("eth0", 1000UL, 500UL),
            NetworkReading.Create("eth1", 2000UL, 1000UL));
        var collector = new NetworkCollector(provider);

        // Act
        await collector.CollectAsync(CancellationToken.None);
        provider.SetReadings(NetworkReading.Create("eth0", 2000UL, 1000UL)); // eth1 disappeared
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, snapshot.Points.Count); // Only eth0 remains
        Assert.All(snapshot.Points, p => Assert.Equal("eth0", p.Instance));
    }

    [Fact]
    public async Task CollectAsync_NewInterfaceAppears_EmitsUnavailablePoints()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        provider.SetReadings(NetworkReading.Create("eth0", 1000UL, 500UL));
        var collector = new NetworkCollector(provider);

        // Act - First collection
        await collector.CollectAsync(CancellationToken.None);
        
        // Second collection - eth1 appears
        provider.SetReadings(
            NetworkReading.Create("eth0", 2000UL, 1000UL),
            NetworkReading.Create("eth1", 500UL, 250UL));
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        // Assert - eth0 has measured values, eth1 has Unavailable points (waiting for next sample)
        Assert.Equal(4, snapshot.Points.Count); // 2 metrics × 2 interfaces
        
        var eth0Points = snapshot.Points.Where(p => p.Instance == "eth0").ToList();
        var eth1Points = snapshot.Points.Where(p => p.Instance == "eth1").ToList();
        
        Assert.Equal(2, eth0Points.Count);
        Assert.All(eth0Points, p => Assert.Equal(CollectorStatus.Ok, p.Status));
        
        Assert.Equal(2, eth1Points.Count);
        Assert.All(eth1Points, p => Assert.Equal(CollectorStatus.Unavailable, p.Status));
    }

    [Fact]
    public void Descriptors_ReturnsCorrectDescriptors()
    {
        // Arrange
        var provider = new FakeNetworkProvider();
        var collector = new NetworkCollector(provider);

        // Act
        var descriptors = collector.Descriptors;

        // Assert
        Assert.Equal(2, descriptors.Count);
        
        var receivedDescriptor = descriptors.First(d => d.MetricId == "network.received.bytespersecond");
        var sentDescriptor = descriptors.First(d => d.MetricId == "network.sent.bytespersecond");
        
        Assert.Equal("Network in", receivedDescriptor.DisplayName);
        Assert.Equal("Network out", sentDescriptor.DisplayName);
        Assert.Equal("B/s", receivedDescriptor.Unit.Symbol);
        Assert.Equal("B/s", sentDescriptor.Unit.Symbol);
        Assert.True(receivedDescriptor.IsPerInstance);
        Assert.True(sentDescriptor.IsPerInstance);
    }

    private sealed class FakeNetworkProvider : INetworkInterfaceProvider
    {
        private IReadOnlyList<NetworkReading> _readings = Array.Empty<NetworkReading>();
        private readonly bool _isSupported;
        private readonly string? _unsupportedReason;
        private readonly bool _readFails;

        public FakeNetworkProvider(bool isSupported = true, string? unsupportedReason = null, bool readFails = false)
        {
            _isSupported = isSupported;
            _unsupportedReason = unsupportedReason;
            _readFails = readFails;
        }

        public bool IsSupported => _isSupported;
        public string? UnsupportedReason => _unsupportedReason;

        public bool TryRead(out IReadOnlyList<NetworkReading> readings)
        {
            if (_readFails)
            {
                readings = Array.Empty<NetworkReading>();
                return false;
            }

            readings = _readings;
            return true;
        }

        public void SetReadings(params NetworkReading[] newReadings)
        {
            _readings = newReadings;
        }
    }
}
