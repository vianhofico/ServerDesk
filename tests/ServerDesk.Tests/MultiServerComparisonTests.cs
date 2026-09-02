using ServerDesk.Application.Dashboard;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class MultiServerComparisonTests
{
    private readonly MultiServerComparisonService _service = new();

    [Fact]
    public void RequiresAtLeastTwoServers()
    {
        var profile = CreateProfile("one");

        Assert.Throws<ArgumentException>(() =>
        {
            _ = _service.Compare([new MultiServerComparisonInput(profile, null)]);
        });
    }

    [Fact]
    public void MissingSnapshotIsUnknownAndNeverEqualOrDifferent()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");
        var result = _service.Compare([
            new MultiServerComparisonInput(first, null),
            new MultiServerComparisonInput(second, CreateSnapshot(second, cpuPercent: 25d)),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.CpuUtilization);

        Assert.Equal(MultiServerComparisonFactStatus.Incomplete, fact.Status);
        Assert.Equal(MultiServerComparisonValueStatus.Unknown, fact.Values[0].Status);
        Assert.Equal(MultiServerComparisonValueStatus.Available, fact.Values[1].Status);
    }

    [Fact]
    public void MissingCapabilityIsUnsupportedAndNeverEqual()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");
        var missingCpu = DashboardSection<CpuMetrics>.Missing("cpu unavailable");
        var result = _service.Compare([
            new MultiServerComparisonInput(first, CreateSnapshot(first, cpu: missingCpu)),
            new MultiServerComparisonInput(second, CreateSnapshot(second, cpu: missingCpu)),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.LogicalProcessors);

        Assert.Equal(MultiServerComparisonFactStatus.Incomplete, fact.Status);
        Assert.All(fact.Values, value => Assert.Equal(MultiServerComparisonValueStatus.Unsupported, value.Status));
    }

    [Fact]
    public void InvalidCapabilityIsUnknown()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");
        var invalidMemory = DashboardSection<MemoryMetrics>.Invalid("parse failed");
        var result = _service.Compare([
            new MultiServerComparisonInput(first, CreateSnapshot(first, memory: invalidMemory)),
            new MultiServerComparisonInput(second, CreateSnapshot(second, memory: invalidMemory)),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.TotalMemory);

        Assert.Equal(MultiServerComparisonFactStatus.Incomplete, fact.Status);
        Assert.All(fact.Values, value => Assert.Equal(MultiServerComparisonValueStatus.Unknown, value.Status));
    }

    [Fact]
    public void PercentFactsCompareAtOneDecimalPrecision()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");
        var result = _service.Compare([
            new MultiServerComparisonInput(first, CreateSnapshot(first, cpuPercent: 42.01d)),
            new MultiServerComparisonInput(second, CreateSnapshot(second, cpuPercent: 42.04d)),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.CpuUtilization);

        Assert.Equal(MultiServerComparisonFactStatus.Equal, fact.Status);
        Assert.All(fact.Values, value => Assert.Equal("42.0%", value.DisplayValue));
    }

    [Fact]
    public void AvailableDifferentValuesReportDifferent()
    {
        var first = CreateProfile("first", port: 22);
        var second = CreateProfile("second", port: 2222);
        var result = _service.Compare([
            new MultiServerComparisonInput(first, null),
            new MultiServerComparisonInput(second, null),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.SshPort);

        Assert.Equal(MultiServerComparisonFactStatus.Different, fact.Status);
    }

    [Fact]
    public void EnvironmentComparisonIsCaseInsensitive()
    {
        var first = CreateProfile("first", environment: "Production");
        var second = CreateProfile("second", environment: "production");
        var result = _service.Compare([
            new MultiServerComparisonInput(first, null),
            new MultiServerComparisonInput(second, null),
        ]);

        var fact = FindFact(result, MultiServerComparisonFact.Environment);

        Assert.Equal(MultiServerComparisonFactStatus.Equal, fact.Status);
    }

    [Fact]
    public void RejectsSnapshotFromAnotherProfile()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");

        Assert.Throws<ArgumentException>(() =>
        {
            _ = _service.Compare([
                new MultiServerComparisonInput(first, CreateSnapshot(second)),
                new MultiServerComparisonInput(second, null),
            ]);
        });
    }

    private static MultiServerComparisonFactResult FindFact(
        MultiServerComparisonResult result,
        MultiServerComparisonFact fact) =>
        result.Facts.Single(item => item.Fact == fact);

    private static ServerProfile CreateProfile(
        string name,
        int port = 22,
        string? environment = "production") =>
        ServerProfile.Create(
            Guid.NewGuid(),
            name,
            $"{name}.example.test",
            port,
            "ops",
            environment);

    private static ServerDashboardSnapshot CreateSnapshot(
        ServerProfile profile,
        double cpuPercent = 20d,
        DashboardSection<CpuMetrics>? cpu = null,
        DashboardSection<MemoryMetrics>? memory = null)
    {
        return new ServerDashboardSnapshot(
            profile.Id,
            DateTimeOffset.UtcNow,
            cpu ?? DashboardSection<CpuMetrics>.Available(new CpuMetrics(cpuPercent, 8)),
            DashboardSection<LoadMetrics>.Available(new LoadMetrics(0.1d, 0.2d, 0.3d)),
            DashboardSection<UptimeMetrics>.Available(new UptimeMetrics(TimeSpan.FromHours(12))),
            memory ?? DashboardSection<MemoryMetrics>.Available(new MemoryMetrics(
                16L * 1024 * 1024 * 1024,
                8L * 1024 * 1024 * 1024,
                8L * 1024 * 1024 * 1024,
                50d,
                2L * 1024 * 1024 * 1024,
                1L * 1024 * 1024 * 1024,
                1L * 1024 * 1024 * 1024,
                50d)),
            DashboardSection<NetworkMetrics>.Available(new NetworkMetrics(0, 0, 0d, 0d, [])),
            DashboardSection<IReadOnlyList<FileSystemMetrics>>.Available([
                new FileSystemMetrics("/dev/sda1", "ext4", 1000, 400, 600, 40d, "/"),
            ]),
            []);
    }
}
