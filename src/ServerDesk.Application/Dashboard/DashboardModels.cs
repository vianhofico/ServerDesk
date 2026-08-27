using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Dashboard;

public enum DashboardSectionStatus
{
    Available,
    Missing,
    Invalid,
}

public sealed record DashboardSection<T>(DashboardSectionStatus Status, T? Value, string? Detail = null)
    where T : class
{
    public static DashboardSection<T> Available(T value) => new(DashboardSectionStatus.Available, value);

    public static DashboardSection<T> Missing(string detail) => new(DashboardSectionStatus.Missing, null, detail);

    public static DashboardSection<T> Invalid(string detail) => new(DashboardSectionStatus.Invalid, null, detail);
}

public sealed record CpuMetrics(double UtilizationPercent, int LogicalProcessors);

public sealed record LoadMetrics(double OneMinute, double FiveMinutes, double FifteenMinutes);

public sealed record UptimeMetrics(TimeSpan Uptime);

public sealed record MemoryMetrics(
    long TotalBytes,
    long AvailableBytes,
    long UsedBytes,
    double UsedPercent,
    long SwapTotalBytes,
    long SwapFreeBytes,
    long SwapUsedBytes,
    double? SwapUsedPercent);

public sealed record NetworkInterfaceMetrics(
    string Name,
    long ReceivedBytes,
    long TransmittedBytes,
    double ReceivedBytesPerSecond,
    double TransmittedBytesPerSecond);

public sealed record NetworkMetrics(
    long ReceivedBytes,
    long TransmittedBytes,
    double ReceivedBytesPerSecond,
    double TransmittedBytesPerSecond,
    IReadOnlyList<NetworkInterfaceMetrics> Interfaces);

public sealed record FileSystemMetrics(
    string Device,
    string FileSystemType,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    double UsedPercent,
    string MountPoint);

public enum DashboardWarningSeverity
{
    Warning,
    Critical,
}

public sealed record DashboardHealthWarning(
    string Code,
    DashboardWarningSeverity Severity,
    string Message);

public sealed record ServerDashboardSnapshot(
    Guid ServerProfileId,
    DateTimeOffset CapturedAtUtc,
    DashboardSection<CpuMetrics> Cpu,
    DashboardSection<LoadMetrics> Load,
    DashboardSection<UptimeMetrics> Uptime,
    DashboardSection<MemoryMetrics> Memory,
    DashboardSection<NetworkMetrics> Network,
    DashboardSection<IReadOnlyList<FileSystemMetrics>> FileSystems,
    IReadOnlyList<DashboardHealthWarning> Warnings);

public sealed record ServerDashboardOptions(
    TimeSpan SamplingInterval,
    double DiskWarningPercent,
    double DiskCriticalPercent,
    double MemoryWarningPercent,
    double MemoryCriticalPercent)
{
    public static ServerDashboardOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(300),
        85d,
        95d,
        90d,
        95d);

    public void Validate()
    {
        if (SamplingInterval <= TimeSpan.Zero || SamplingInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(SamplingInterval), "Sampling interval must be between 0 and 5 seconds.");
        }

        ValidateThreshold(DiskWarningPercent, nameof(DiskWarningPercent));
        ValidateThreshold(DiskCriticalPercent, nameof(DiskCriticalPercent));
        ValidateThreshold(MemoryWarningPercent, nameof(MemoryWarningPercent));
        ValidateThreshold(MemoryCriticalPercent, nameof(MemoryCriticalPercent));
        if (DiskWarningPercent >= DiskCriticalPercent)
        {
            throw new ArgumentException("Disk warning threshold must be lower than the critical threshold.");
        }

        if (MemoryWarningPercent >= MemoryCriticalPercent)
        {
            throw new ArgumentException("Memory warning threshold must be lower than the critical threshold.");
        }
    }

    private static void ValidateThreshold(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0d or > 100d)
        {
            throw new ArgumentOutOfRangeException(name, "Dashboard thresholds must be between 0 and 100 percent.");
        }
    }
}

public sealed class ServerDashboardException : Exception
{
    public ServerDashboardException(RemoteError error)
        : base(error?.Message)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IServerDashboardService
{
    ValueTask<ServerDashboardSnapshot> GetAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}
