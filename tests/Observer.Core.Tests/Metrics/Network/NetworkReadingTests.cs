using Observer.Core.Metrics.Network;

namespace Observer.Core.Tests.Metrics.Network;

public class NetworkReadingTests
{
    [Fact]
    public void Create_ValidParameters_ReturnsReading()
    {
        // Arrange & Act
        var reading = NetworkReading.Create("eth0", 1000UL, 500UL);

        // Assert
        Assert.Equal("eth0", reading.Instance);
        Assert.Equal(1000UL, reading.BytesReceived);
        Assert.Equal(500UL, reading.BytesSent);
    }

    [Fact]
    public void Create_NullInterface_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => NetworkReading.Create(null!, 1000UL, 500UL));
    }

    [Fact]
    public void Create_EmptyInterface_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentException>(() => NetworkReading.Create("", 1000UL, 500UL));
    }

    [Fact]
    public void Create_WhitespaceInterface_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentException>(() => NetworkReading.Create("   ", 1000UL, 500UL));
    }
}
