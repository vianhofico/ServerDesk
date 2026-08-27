using ServerDesk.Application.Dashboard;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DashboardMetricsTests
{
    [Theory]
    [InlineData("ubuntu-24.04-dashboard.txt", "eth0", 30d, 600d, 300d)]
    [InlineData("ubuntu-26.04-dashboard.txt", "enp0s3", 31.25d, 1200d, 600d)]
    [InlineData("debian-13-dashboard.txt", "ens18", 30d, 1800d, 900d)]
    public void CertifiedFixturesProduceNormalizedSnapshot(
        string fixtureName,
        string expectedInterface,
        double expectedCpuPercent,
        double expectedRxRate,
        double expectedTxRate)
    {
        var (first, second) = LoadSamples(fixtureName);
        var profileId = Guid.NewGuid();

        var snapshot = LinuxDashboardParser.ParseSnapshot(
            profileId,
            DateTimeOffset.Parse("2026-08-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            first,
            second,
            TimeSpan.FromSeconds(1),
            ServerDashboardOptions.Default);

        Assert.Equal(profileId, snapshot.ServerProfileId);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Cpu.Status);
        Assert.Equal(expectedCpuPercent, snapshot.Cpu.Value!.UtilizationPercent, 2);
        Assert.Equal(2, snapshot.Cpu.Value.LogicalProcessors);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Load.Status);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Uptime.Status);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Memory.Status);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Network.Status);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.FileSystems.Status);

        var networkInterface = Assert.Single(snapshot.Network.Value!.Interfaces);
        Assert.Equal(expectedInterface, networkInterface.Name);
        Assert.Equal(expectedRxRate, networkInterface.ReceivedBytesPerSecond, 2);
        Assert.Equal(expectedTxRate, networkInterface.TransmittedBytesPerSecond, 2);
        Assert.NotEmpty(snapshot.FileSystems.Value!);
    }

    [Fact]
    public void MissingInputIsRepresentedExplicitlyInsteadOfThrowing()
    {
        var snapshot = LinuxDashboardParser.ParseSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            string.Empty,
            string.Empty,
            TimeSpan.FromSeconds(1),
            ServerDashboardOptions.Default);

        Assert.Equal(DashboardSectionStatus.Missing, snapshot.Cpu.Status);
        Assert.Equal(DashboardSectionStatus.Missing, snapshot.Load.Status);
        Assert.Equal(DashboardSectionStatus.Missing, snapshot.Uptime.Status);
        Assert.Equal(DashboardSectionStatus.Missing, snapshot.Memory.Status);
        Assert.Equal(DashboardSectionStatus.Missing, snapshot.Network.Status);
        Assert.Equal(DashboardSectionStatus.Missing, snapshot.FileSystems.Status);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void NonMonotonicCpuAndNetworkSamplesAreExplicitlyInvalidOrUnavailable()
    {
        var cpu = LinuxDashboardParser.ParseCpu(
            "cpu 100 0 0 900\ncpu0 100 0 0 900\n",
            "cpu 90 0 0 800\ncpu0 90 0 0 800\n");
        var network = LinuxDashboardParser.ParseNetwork(
            "eth0: 1000 1 0 0 0 0 0 0 500 1 0 0 0 0 0 0",
            "eth0: 900 1 0 0 0 0 0 0 400 1 0 0 0 0 0 0",
            TimeSpan.FromSeconds(1));

        Assert.Equal(DashboardSectionStatus.Invalid, cpu.Status);
        Assert.Equal(DashboardSectionStatus.Missing, network.Status);
    }

    [Fact]
    public void WarningThresholdsAreDeterministic()
    {
        var (ubuntuFirst, ubuntuSecond) = LoadSamples("ubuntu-26.04-dashboard.txt");
        var ubuntu = LinuxDashboardParser.ParseSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ubuntuFirst,
            ubuntuSecond,
            TimeSpan.FromSeconds(1),
            ServerDashboardOptions.Default);
        var (debianFirst, debianSecond) = LoadSamples("debian-13-dashboard.txt");
        var debian = LinuxDashboardParser.ParseSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            debianFirst,
            debianSecond,
            TimeSpan.FromSeconds(1),
            ServerDashboardOptions.Default);

        var ubuntuWarning = Assert.Single(ubuntu.Warnings);
        Assert.Equal("disk-warning:/data", ubuntuWarning.Code);
        Assert.Equal(DashboardWarningSeverity.Warning, ubuntuWarning.Severity);

        var debianWarning = Assert.Single(debian.Warnings);
        Assert.Equal("disk-critical:/", debianWarning.Code);
        Assert.Equal(DashboardWarningSeverity.Critical, debianWarning.Severity);
    }

    [Fact]
    public void MemoryParserHandlesNoSwapWithoutDivisionByZero()
    {
        var parsed = LinuxDashboardParser.ParseMemory(
            "MemTotal: 4096 kB\nMemAvailable: 1024 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n");

        Assert.Equal(DashboardSectionStatus.Available, parsed.Status);
        Assert.Equal(75d, parsed.Value!.UsedPercent, 2);
        Assert.Equal(0, parsed.Value.SwapTotalBytes);
        Assert.Null(parsed.Value.SwapUsedPercent);
    }

    private static (string First, string Second) LoadSamples(string fileName)
    {
        var content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dashboard", fileName));
        var samples = content.Split("__SD_SAMPLE_2__", 2, StringSplitOptions.None);
        Assert.Equal(2, samples.Length);
        return (samples[0], samples[1]);
    }
}
