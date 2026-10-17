using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests.Platform.Linux;

/// <summary>
/// Tests for /proc/net/dev parsing. Pure function: receives content already read, no files opened.
/// </summary>
/// <remarks>
/// The parser must handle the real format of /proc/net/dev, which has two header lines and then
/// one line per interface. The loopback interface is filtered out, and malformed lines are skipped.
/// </remarks>
public class ProcNetDevParserTests
{
    [Fact]
    public void Read_EmptyContent_ReturnsEmpty()
    {
        var result = ProcNetDevParser.Read(string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_OnlyHeaders_ReturnsEmpty()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Empty(result);
    }

    [Fact]
    public void Read_SingleInterface_ReturnsOneReading()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                eth0: 1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth0", result[0].Interface);
        Assert.Equal(1000000UL, result[0].BytesReceived);
        Assert.Equal(500000UL, result[0].BytesSent);
    }

    [Fact]
    public void Read_MultipleInterfaces_ReturnsAllExceptLoopback()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:   10000     100    0    0    0     0          0         0    10000     100    0    0    0     0       0          0
              eth0: 1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
              eth1: 2000000   20000    0    0    0     0          0         0  1000000   10000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Equal(2, result.Count);
        
        Assert.Equal("eth0", result[0].Interface);
        Assert.Equal(1000000UL, result[0].BytesReceived);
        Assert.Equal(500000UL, result[0].BytesSent);
        
        Assert.Equal("eth1", result[1].Interface);
        Assert.Equal(2000000UL, result[1].BytesReceived);
        Assert.Equal(1000000UL, result[1].BytesSent);
    }

    [Fact]
    public void Read_LoopbackVariants_AreFiltered()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:   10000     100    0    0    0     0          0         0    10000     100    0    0    0     0       0          0
              eth0: 1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.DoesNotContain(result, r => r.Interface == "lo");
    }

    [Fact]
    public void Read_MalformedLine_IsSkipped()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0: not_a_number   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
              eth1: 2000000   20000    0    0    0     0          0         0  1000000   10000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth1", result[0].Interface);
    }

    [Fact]
    public void Read_TooFewFields_IsSkipped()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0: 1000000   10000    0    0    0     0          0         0
              eth1: 2000000   20000    0    0    0     0          0         0  1000000   10000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth1", result[0].Interface);
    }

    [Fact]
    public void Read_WifiInterface_IsParsed()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
             wlan0: 5000000   50000    0    0    0     0          0         0  2000000   20000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("wlan0", result[0].Interface);
        Assert.Equal(5000000UL, result[0].BytesReceived);
        Assert.Equal(2000000UL, result[0].BytesSent);
    }

    [Fact]
    public void Read_VpnInterface_IsParsed()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
             tun0: 3000000   30000    0    0    0     0          0         0  1500000   15000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("tun0", result[0].Interface);
        Assert.Equal(3000000UL, result[0].BytesReceived);
        Assert.Equal(1500000UL, result[0].BytesSent);
    }

    [Fact]
    public void Read_InterfaceWithExtraWhitespace_IsParsed()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                eth0:    1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth0", result[0].Interface);
        Assert.Equal(1000000UL, result[0].BytesReceived);
        Assert.Equal(500000UL, result[0].BytesSent);
    }

    [Fact]
    public void Read_ZeroCounters_AreValid()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth0", result[0].Interface);
        Assert.Equal(0UL, result[0].BytesReceived);
        Assert.Equal(0UL, result[0].BytesSent);
    }

    [Fact]
    public void Read_LargeCounters_DoNotOverflow()
    {
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0: 18446744073709551615   10000    0    0    0     0          0         0 18446744073709551615    5000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal(ulong.MaxValue, result[0].BytesReceived);
        Assert.Equal(ulong.MaxValue, result[0].BytesSent);
    }

    [Fact]
    public void Read_RealWorldSample_IsParsedCorrectly()
    {
        // Real output from a Linux machine with loopback, ethernet, and wifi
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:  254876    2842    0    0    0     0          0         0   254876    2842    0    0    0     0       0          0
            enp3s0: 1234567890 1234567    0    0    0     0          0         0 987654321  765432    0    0    0     0       0          0
            wlp2s0:  543210987  543210    0    0    0     0          0         0 123456789  123456    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Equal(2, result.Count);
        
        var eth0 = result.First(r => r.Interface == "enp3s0");
        Assert.Equal(1234567890UL, eth0.BytesReceived);
        Assert.Equal(987654321UL, eth0.BytesSent);
        
        var wifi = result.First(r => r.Interface == "wlp2s0");
        Assert.Equal(543210987UL, wifi.BytesReceived);
        Assert.Equal(123456789UL, wifi.BytesSent);
    }

    [Fact]
    public void Read_EmptyInterfaceName_IsSkipped()
    {
        // A line with no name before the colon is not an interface: keeping it would
        // publish a row with an empty label that no panel could explain.
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                      : 1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
              eth0: 2000000   20000    0    0    0     0          0         0  1000000   10000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth0", result[0].Interface);
    }

    [Fact]
    public void Read_CounterBeyondUlongMax_IsSkipped()
    {
        // One past ulong.MaxValue is not a counter but a corrupt or wrapped read:
        // publishing it would show a huge and plausible number.
        var content = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0: 18446744073709551616   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0
              eth1: 2000000   20000    0    0    0     0          0         0  1000000   10000    0    0    0     0       0          0
            """;

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth1", result[0].Interface);
    }

    [Fact]
    public void Read_CrLfLineEndings_AreParsed()
    {
        // Files copied from a Windows share or read in text mode can carry \r\n:
        // the readings must not depend on which newline the content arrived with.
        var content =
            "Inter-|   Receive                                                |  Transmit\r\n" +
            " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\r\n" +
            "  eth0: 1000000   10000    0    0    0     0          0         0   500000    5000    0    0    0     0       0          0\r\n";

        var result = ProcNetDevParser.Read(content);

        Assert.Single(result);
        Assert.Equal("eth0", result[0].Interface);
        Assert.Equal(1000000UL, result[0].BytesReceived);
        Assert.Equal(500000UL, result[0].BytesSent);
    }
}
