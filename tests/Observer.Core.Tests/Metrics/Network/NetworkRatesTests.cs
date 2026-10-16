using Observer.Core.Metrics;
using Observer.Core.Metrics.Network;

namespace Observer.Core.Tests.Metrics.Network;

public class NetworkRatesTests
{
    [Fact]
    public void TryComputeBytesPerSecond_NormalCase_ReturnsTrueAndCorrectRate()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 3000;
        TimeSpan elapsed = TimeSpan.FromSeconds(2);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(1000.0, rate, 2);
        Assert.Equal(SampleFailure.Unknown, failure);
    }

    [Fact]
    public void TryComputeBytesPerSecond_ZeroElapsed_ReturnsFalse()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 3000;
        TimeSpan elapsed = TimeSpan.Zero;

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.False(success);
        Assert.Equal(0.0, rate);
        Assert.Equal(SampleFailure.NoElapsedTime, failure);
    }

    [Fact]
    public void TryComputeBytesPerSecond_NegativeElapsed_ReturnsFalse()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 3000;
        TimeSpan elapsed = TimeSpan.FromSeconds(-1);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.False(success);
        Assert.Equal(0.0, rate);
        Assert.Equal(SampleFailure.NoElapsedTime, failure);
    }

    [Fact]
    public void TryComputeBytesPerSecond_CounterWentBackwards_ReturnsFalse()
    {
        // Arrange
        ulong previous = 3000;
        ulong current = 1000;
        TimeSpan elapsed = TimeSpan.FromSeconds(2);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.False(success);
        Assert.Equal(0.0, rate);
        Assert.Equal(SampleFailure.CounterWentBackwards, failure);
    }

    [Fact]
    public void TryComputeBytesPerSecond_OneSecondInterval_ReturnsRawDifference()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 1500;
        TimeSpan elapsed = TimeSpan.FromSeconds(1);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(500.0, rate, 2);
    }

    [Fact]
    public void TryComputeBytesPerSecond_SubSecondInterval_CalculatesCorrectly()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 1500;
        TimeSpan elapsed = TimeSpan.FromMilliseconds(500);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(1000.0, rate, 2);
    }

    [Fact]
    public void TryComputeBytesPerSecond_NoChange_ReturnsZero()
    {
        // Arrange
        ulong previous = 1000;
        ulong current = 1000;
        TimeSpan elapsed = TimeSpan.FromSeconds(2);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void TryComputeBytesPerSecond_VeryLargeCounters_DoesNotOverflow()
    {
        // Arrange
        ulong previous = ulong.MaxValue - 2000;
        ulong current = ulong.MaxValue - 1000;
        TimeSpan elapsed = TimeSpan.FromSeconds(1);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(1000.0, rate, 2);
    }

    [Fact]
    public void TryComputeBytesPerSecond_MillisecondPrecision_WorksCorrectly()
    {
        // Arrange
        ulong previous = 0;
        ulong current = 100;
        TimeSpan elapsed = TimeSpan.FromMilliseconds(100);

        // Act
        bool success = NetworkRates.TryComputeBytesPerSecond(previous, current, elapsed, out double rate, out SampleFailure failure);

        // Assert
        Assert.True(success);
        Assert.Equal(1000.0, rate, 2); // 100 bytes in 0.1 seconds = 1000 bytes/sec
    }
}
