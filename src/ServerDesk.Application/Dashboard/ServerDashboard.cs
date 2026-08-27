using System.Diagnostics;
using System.Globalization;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Dashboard;

public enum DashboardSectionStatus
{
    Available,
    Missing,
    Invalid,
}

public sealed record DashboardSection<T>(
    DashboardSectionStatus Status,
    T? Value,
    string? Detail = null)
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

public sealed class ServerDashboardService : IServerDashboardService
{
    private const string FirstSampleScript =
        "cat /proc/stat 2>/dev/null; printf '\n__SD_NET__\n'; cat /proc/net/dev 2>/dev/null";

    private const string SecondSampleScript =
        "cat /proc/stat 2>/dev/null; " +
        "printf '\n__SD_NET__\n'; cat /proc/net/dev 2>/dev/null; " +
        "printf '\n__SD_LOAD__\n'; cat /proc/loadavg 2>/dev/null; " +
        "printf '\n__SD_UPTIME__\n'; cat /proc/uptime 2>/dev/null; " +
        "printf '\n__SD_MEM__\n'; cat /proc/meminfo 2>/dev/null; " +
        "printf '\n__SD_DF__\n'; df -P -T -B1 2>/dev/null";

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly ServerDashboardOptions _options;

    public ServerDashboardService(
        IRemoteCommandExecutorFactory executorFactory,
        ServerDashboardOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<ServerDashboardSnapshot> GetAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _executorFactory.Create(profile);

        var first = await ExecuteAsync(executor, FirstSampleScript, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(_options.SamplingInterval, cancellationToken).ConfigureAwait(false);
        var second = await ExecuteAsync(executor, SecondSampleScript, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return LinuxDashboardParser.ParseSnapshot(
            profile.Id,
            DateTimeOffset.UtcNow,
            first,
            second,
            stopwatch.Elapsed,
            _options);
    }

    private static async Task<string> ExecuteAsync(
        IRemoteCommandExecutor executor,
        string script,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "/bin/sh",
                ["-lc", script],
                TimeSpan.FromSeconds(10)),
            cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            throw new ServerDashboardException(result.Error);
        }

        return result.Command?.StandardOutput ?? string.Empty;
    }
}

public static class LinuxDashboardParser
{
    private const string NetMarker = "__SD_NET__";
    private const string LoadMarker = "__SD_LOAD__";
    private const string UptimeMarker = "__SD_UPTIME__";
    private const string MemoryMarker = "__SD_MEM__";
    private const string FileSystemMarker = "__SD_DF__";

    public static ServerDashboardSnapshot ParseSnapshot(
        Guid serverProfileId,
        DateTimeOffset capturedAtUtc,
        string firstSample,
        string secondSample,
        TimeSpan sampleElapsed,
        ServerDashboardOptions options)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        ArgumentNullException.ThrowIfNull(options);
        var first = SplitSample(firstSample, includeExtendedSections: false);
        var second = SplitSample(secondSample, includeExtendedSections: true);

        var cpu = ParseCpu(first.Cpu, second.Cpu);
        var load = ParseLoad(second.Load);
        var uptime = ParseUptime(second.Uptime);
        var memory = ParseMemory(second.Memory);
        var network = ParseNetwork(first.Network, second.Network, sampleElapsed);
        var fileSystems = ParseFileSystems(second.FileSystems);
        var warnings = BuildWarnings(memory, fileSystems, options);

        return new ServerDashboardSnapshot(
            serverProfileId,
            capturedAtUtc,
            cpu,
            load,
            uptime,
            memory,
            network,
            fileSystems,
            warnings);
    }

    public static DashboardSection<CpuMetrics> ParseCpu(string first, string second)
    {
        if (!TryParseCpuCounters(first, out var before, out var logicalProcessors) ||
            !TryParseCpuCounters(second, out var after, out var afterLogicalProcessors))
        {
            return DashboardSection<CpuMetrics>.Missing("/proc/stat CPU counters are unavailable.");
        }

        var totalDelta = after.Total - before.Total;
        var idleDelta = after.Idle - before.Idle;
        if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta)
        {
            return DashboardSection<CpuMetrics>.Invalid("CPU counters did not advance monotonically.");
        }

        var busyPercent = 100d * (totalDelta - idleDelta) / totalDelta;
        return DashboardSection<CpuMetrics>.Available(new CpuMetrics(
            Math.Clamp(busyPercent, 0d, 100d),
            Math.Max(logicalProcessors, afterLogicalProcessors)));
    }

    public static DashboardSection<LoadMetrics> ParseLoad(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return DashboardSection<LoadMetrics>.Missing("/proc/loadavg is unavailable.");
        }

        if (!TryDouble(parts[0], out var one) || !TryDouble(parts[1], out var five) || !TryDouble(parts[2], out var fifteen))
        {
            return DashboardSection<LoadMetrics>.Invalid("Load averages could not be parsed.");
        }

        return DashboardSection<LoadMetrics>.Available(new LoadMetrics(one, five, fifteen));
    }

    public static DashboardSection<UptimeMetrics> ParseUptime(string value)
    {
        var first = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (first is null)
        {
            return DashboardSection<UptimeMetrics>.Missing("/proc/uptime is unavailable.");
        }

        if (!TryDouble(first, out var seconds) || seconds < 0)
        {
            return DashboardSection<UptimeMetrics>.Invalid("Uptime could not be parsed.");
        }

        return DashboardSection<UptimeMetrics>.Available(new UptimeMetrics(TimeSpan.FromSeconds(seconds)));
    }

    public static DashboardSection<MemoryMetrics> ParseMemory(string value)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var tokens = line[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0 || !long.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            var multiplier = tokens.Length > 1 && string.Equals(tokens[1], "kB", StringComparison.OrdinalIgnoreCase)
                ? 1024L
                : 1L;
            values[key] = checked(amount * multiplier);
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            return DashboardSection<MemoryMetrics>.Missing("MemTotal is unavailable in /proc/meminfo.");
        }

        if (!values.TryGetValue("MemAvailable", out var available))
        {
            return DashboardSection<MemoryMetrics>.Missing("MemAvailable is unavailable in /proc/meminfo.");
        }

        available = Math.Clamp(available, 0, total);
        var used = total - available;
        var swapTotal = values.GetValueOrDefault("SwapTotal");
        var swapFree = Math.Clamp(values.GetValueOrDefault("SwapFree"), 0, swapTotal);
        var swapUsed = swapTotal - swapFree;
        var swapPercent = swapTotal > 0 ? 100d * swapUsed / swapTotal : null;

        return DashboardSection<MemoryMetrics>.Available(new MemoryMetrics(
            total,
            available,
            used,
            100d * used / total,
            swapTotal,
            swapFree,
            swapUsed,
            swapPercent));
    }

    public static DashboardSection<NetworkMetrics> ParseNetwork(
        string first,
        string second,
        TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return DashboardSection<NetworkMetrics>.Invalid("Network sample interval must be positive.");
        }

        var before = ParseNetworkCounters(first);
        var after = ParseNetworkCounters(second);
        if (before.Count == 0 || after.Count == 0)
        {
            return DashboardSection<NetworkMetrics>.Missing("/proc/net/dev interface counters are unavailable.");
        }

        var rows = new List<NetworkInterfaceMetrics>();
        foreach (var pair in after.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.Equals(pair.Key, "lo", StringComparison.Ordinal) || !before.TryGetValue(pair.Key, out var previous))
            {
                continue;
            }

            var receivedDelta = pair.Value.Received - previous.Received;
            var transmittedDelta = pair.Value.Transmitted - previous.Transmitted;
            if (receivedDelta < 0 || transmittedDelta < 0)
            {
                continue;
            }

            rows.Add(new NetworkInterfaceMetrics(
                pair.Key,
                pair.Value.Received,
                pair.Value.Transmitted,
                receivedDelta / elapsed.TotalSeconds,
                transmittedDelta / elapsed.TotalSeconds));
        }

        if (rows.Count == 0)
        {
            return DashboardSection<NetworkMetrics>.Missing("No non-loopback network interface has comparable samples.");
        }

        return DashboardSection<NetworkMetrics>.Available(new NetworkMetrics(
            rows.Sum(row => row.ReceivedBytes),
            rows.Sum(row => row.TransmittedBytes),
            rows.Sum(row => row.ReceivedBytesPerSecond),
            rows.Sum(row => row.TransmittedBytesPerSecond),
            rows));
    }

    public static DashboardSection<IReadOnlyList<FileSystemMetrics>> ParseFileSystems(string value)
    {
        var rows = new List<FileSystemMetrics>();
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 7 ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var available) ||
                !TryPercent(parts[5], out var usedPercent))
            {
                continue;
            }

            rows.Add(new FileSystemMetrics(
                parts[0],
                parts[1],
                total,
                used,
                available,
                usedPercent,
                parts[6]));
        }

        return rows.Count == 0
            ? DashboardSection<IReadOnlyList<FileSystemMetrics>>.Missing("No filesystem rows could be read from df.")
            : DashboardSection<IReadOnlyList<FileSystemMetrics>>.Available(rows);
    }

    private static IReadOnlyList<DashboardHealthWarning> BuildWarnings(
        DashboardSection<MemoryMetrics> memory,
        DashboardSection<IReadOnlyList<FileSystemMetrics>> fileSystems,
        ServerDashboardOptions options)
    {
        var warnings = new List<DashboardHealthWarning>();
        if (memory.Value is { } memoryValue)
        {
            if (memoryValue.UsedPercent >= options.MemoryCriticalPercent)
            {
                warnings.Add(new DashboardHealthWarning(
                    "memory-critical",
                    DashboardWarningSeverity.Critical,
                    $"Memory usage is {memoryValue.UsedPercent:F1}% (critical)."));
            }
            else if (memoryValue.UsedPercent >= options.MemoryWarningPercent)
            {
                warnings.Add(new DashboardHealthWarning(
                    "memory-warning",
                    DashboardWarningSeverity.Warning,
                    $"Memory usage is {memoryValue.UsedPercent:F1}% (warning)."));
            }
        }

        if (fileSystems.Value is not null)
        {
            foreach (var fileSystem in fileSystems.Value.OrderByDescending(row => row.UsedPercent))
            {
                if (fileSystem.UsedPercent >= options.DiskCriticalPercent)
                {
                    warnings.Add(new DashboardHealthWarning(
                        $"disk-critical:{fileSystem.MountPoint}",
                        DashboardWarningSeverity.Critical,
                        $"Filesystem {fileSystem.MountPoint} is {fileSystem.UsedPercent:F0}% full."));
                }
                else if (fileSystem.UsedPercent >= options.DiskWarningPercent)
                {
                    warnings.Add(new DashboardHealthWarning(
                        $"disk-warning:{fileSystem.MountPoint}",
                        DashboardWarningSeverity.Warning,
                        $"Filesystem {fileSystem.MountPoint} is {fileSystem.UsedPercent:F0}% full."));
                }
            }
        }

        return warnings;
    }

    private static DashboardSampleParts SplitSample(string value, bool includeExtendedSections)
    {
        value ??= string.Empty;
        var netIndex = value.IndexOf(NetMarker, StringComparison.Ordinal);
        if (netIndex < 0)
        {
            return new DashboardSampleParts(value, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var cpu = value[..netIndex];
        var afterNet = value[(netIndex + NetMarker.Length)..];
        if (!includeExtendedSections)
        {
            return new DashboardSampleParts(cpu, afterNet, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var loadIndex = afterNet.IndexOf(LoadMarker, StringComparison.Ordinal);
        if (loadIndex < 0)
        {
            return new DashboardSampleParts(cpu, afterNet, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var network = afterNet[..loadIndex];
        var afterLoadMarker = afterNet[(loadIndex + LoadMarker.Length)..];
        return new DashboardSampleParts(
            cpu,
            network,
            SliceBetween(afterLoadMarker, UptimeMarker, out var afterUptimeMarker),
            SliceBetween(afterUptimeMarker, MemoryMarker, out var afterMemoryMarker),
            SliceBetween(afterMemoryMarker, FileSystemMarker, out var fileSystems),
            fileSystems);
    }

    private static string SliceBetween(string value, string nextMarker, out string remainder)
    {
        var index = value.IndexOf(nextMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            remainder = string.Empty;
            return value;
        }

        var result = value[..index];
        remainder = value[(index + nextMarker.Length)..];
        return result;
    }

    private static bool TryParseCpuCounters(string value, out CpuCounters counters, out int logicalProcessors)
    {
        counters = default;
        logicalProcessors = 0;
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsDigit(line[3]))
            {
                logicalProcessors++;
                continue;
            }

            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
            {
                return false;
            }

            var numbers = new List<long>();
            for (var index = 1; index < parts.Length; index++)
            {
                if (!long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    return false;
                }

                numbers.Add(number);
            }

            var total = numbers.Sum();
            var idle = numbers[3] + (numbers.Count > 4 ? numbers[4] : 0);
            counters = new CpuCounters(total, idle);
        }

        return counters.Total > 0;
    }

    private static Dictionary<string, NetworkCounters> ParseNetworkCounters(string value)
    {
        var result = new Dictionary<string, NetworkCounters>(StringComparer.Ordinal);
        foreach (var rawLine in value.Split('\n'))
        {
            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = rawLine[..separator].Trim();
            var parts = rawLine[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 16 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var received) ||
                !long.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out var transmitted))
            {
                continue;
            }

            result[name] = new NetworkCounters(received, transmitted);
        }

        return result;
    }

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryPercent(string value, out double result)
    {
        var trimmed = value.Trim().TrimEnd('%');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private readonly record struct CpuCounters(long Total, long Idle);

    private readonly record struct NetworkCounters(long Received, long Transmitted);

    private sealed record DashboardSampleParts(
        string Cpu,
        string Network,
        string Load,
        string Uptime,
        string Memory,
        string FileSystems);
}
