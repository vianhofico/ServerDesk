using ServerDesk.Application.Networking;
using Xunit;

namespace ServerDesk.Tests;

public sealed class NetworkTests
{
    [Fact]
    public void ProcNetDevParserReadsReceiveAndTransmitBytes()
    {
        const string output = "Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\n" +
                              "  eth0: 1000 1 0 0 0 0 0 0 2500 2 0 0 0 0 0 0\n";

        var result = NetworkParser.ParseProcNetDev(output);

        Assert.Equal(1000, result["eth0"].RxBytes);
        Assert.Equal(2500, result["eth0"].TxBytes);
    }

    [Fact]
    public void InterfaceParserCombinesJsonAddressesAndCounterRates()
    {
        const string json = """
            [
              {
                "ifname": "eth0",
                "operstate": "UP",
                "mtu": 1500,
                "address": "02:42:ac:11:00:02",
                "addr_info": [
                  { "family": "inet", "local": "10.0.0.4", "prefixlen": 24, "scope": "global" },
                  { "family": "inet6", "local": "fe80::1", "prefixlen": 64, "scope": "link" }
                ]
              }
            ]
            """;
        var before = new Dictionary<string, NetworkCounter> { ["eth0"] = new(1000, 2000) };
        var after = new Dictionary<string, NetworkCounter> { ["eth0"] = new(1600, 3200) };

        var result = NetworkParser.ParseInterfaces(json, before, after, TimeSpan.FromSeconds(2));

        var item = Assert.Single(result);
        Assert.Equal("eth0", item.Name);
        Assert.Equal("UP", item.OperationalState);
        Assert.Equal(1500, item.Mtu);
        Assert.Equal(2, item.Addresses.Count);
        Assert.Equal(300, item.RxBytesPerSecond);
        Assert.Equal(600, item.TxBytesPerSecond);
        Assert.Equal(1600, item.RxBytes);
        Assert.Equal(3200, item.TxBytes);
    }

    [Theory]
    [InlineData("ubuntu-24.04-ip.json")]
    [InlineData("ubuntu-26.04-ip.json")]
    [InlineData("debian-13-ip.json")]
    public void CertifiedIpJsonFixturesParseWithoutGuessing(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Network", fileName));
        var counters = new Dictionary<string, NetworkCounter>();

        var result = NetworkParser.ParseInterfaces(json, counters, counters, TimeSpan.FromSeconds(1));

        Assert.Contains(result, item => item.Name == "lo");
        Assert.All(result, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
    }

    [Fact]
    public void ListeningSocketParserHandlesIpv4Ipv6AndOwnerVisibility()
    {
        const string output =
            "tcp LISTEN 0 4096 127.0.0.1:18080 0.0.0.0:* users:((\"python3\",pid=4242,fd=3))\n" +
            "tcp LISTEN 0 128 [::]:22 [::]:*\n" +
            "udp UNCONN 0 0 0.0.0.0:5353 0.0.0.0:*\n";

        var result = NetworkParser.ParseListeningSockets(output);

        Assert.Equal(3, result.Count);
        var http = Assert.Single(result, item => item.Port == 18080);
        Assert.Equal("127.0.0.1", http.LocalAddress);
        Assert.Equal(4242, http.ProcessId);
        Assert.Equal("python3", http.ProcessName);
        Assert.True(http.OwnerVisible);
        var ssh = Assert.Single(result, item => item.Port == 22);
        Assert.Equal("::", ssh.LocalAddress);
        Assert.False(ssh.OwnerVisible);
    }

    [Fact]
    public void MalformedProcRowFailsClosed()
    {
        Assert.Throws<FormatException>(() => NetworkParser.ParseProcNetDev("eth0: nope"));
    }

    [Fact]
    public void InvalidIpJsonFailsClosed()
    {
        Assert.Throws<FormatException>(() => NetworkParser.ParseInterfaces("{ nope", new Dictionary<string, NetworkCounter>(), new Dictionary<string, NetworkCounter>(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MalformedSocketRowFailsClosed()
    {
        Assert.Throws<FormatException>(() => NetworkParser.ParseListeningSockets("not an ss row"));
    }
}
