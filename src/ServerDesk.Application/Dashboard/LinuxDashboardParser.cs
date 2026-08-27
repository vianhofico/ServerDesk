using System.Globalization;

namespace ServerDesk.Application.Dashboard;

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
        options.Validate();
        var first = SplitSample(firstSample, includeExtendedSections: false);
        var second = SplitSample(secondSample, includeExtendedSections: true);

        var cpu = ParseCpu(first.Cpu, second.Cpu);
        var load = ParseLoad(second.Load);
        var uptime = ParseUptime(second.Uptime);
        var memory = ParseMemory(second.Memory);
        var network = ParseNetwork(first.Network, second.Network, sampleElapsed);
        var fileSystems = ParseFileSystems(second.FileSystems);

        return new ServerDashboardSnapshot(
            serverProfileId,
            capturedAtUtc,
            cpu,
            load,
            uptime,
            memory,
            network,
            fileSystems,
            BuildWarnings(memory, fileSystems, options));
    }

    public static DashboardSection<CpuMetrics> ParseCpu(string first, string second)
    {
        if (!TryParseCpuCounters(first, out var before, out var beforeLogicalProcessors) ||
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

        return DashboardSection<CpuMetrics>.Available(new CpuMetrics(
            Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0d, 100d),
            Math.Max(1, Math.Max(beforeLogicalProcessors, afterLogicalProcessors))));
    }

    public static DashboardSection<LoadMetrics> ParseLoad(string value)
    {
        var parts = Tokens(value);
        if (parts.Length < 3)
        {
            return DashboardSection<LoadMetrics>.Missing("/proc/loadavg is unavailable.");
        }

        if (!TryFiniteDouble(parts[0], out var one) ||
            !TryFiniteDouble(parts[1], out var five) ||
            !TryFiniteDouble(parts[2], out var fifteen) ||
            one < 0 || five < 0 || fifteen < 0)
        {
            return DashboardSection<LoadMetrics>.Invalid("Load averages could not be parsed.");
        }

        return DashboardSection<LoadMetrics>.Available(new LoadMetrics(one, five, fifteen));
    }

    public static DashboardSection<UptimeMetrics> ParseUptime(string value)
    {
        var token = Tokens(value).FirstOrDefault();
        if (token is null)
        {
            return DashboardSection<UptimeMetrics>.Missing("/proc/uptime is unavailable.");
        }

        if (!TryFiniteDouble(token, out var seconds) ||
            seconds < 0 ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
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
            var tokens = Tokens(line[(separator + 1)..]);
            if (tokens.Length == 0 ||
                !long.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
                amount < 0)
            {
                continue;
            }

            var multiplier = tokens.Length > 1 && string.Equals(tokens[1], "kB", StringComparison.OrdinalIgnoreCase)
                ? 1024L
                : 1L;
            if (amount > long.MaxValue / multiplier)
            {
                continue;
            }

            values[key] = amount * multiplier;
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
        var swapTotal = Math.Max(0, values.GetValueOrDefault("SwapTotal"));
        var swapFree = Math.Clamp(values.GetValueOrDefault("SwapFree"), 0, swapTotal);
        var swapUsed = swapTotal - swapFree;
        double? swapPercent = swapTotal > 0 ? 100d * swapUsed / swapTotal : null;

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
            if (string.Equals(pair.Key, "lo", StringComparison.Ordinal) ||
                !before.TryGetValue(pair.Key, out var previous))
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
            return DashboardSection<NetworkMetrics>.Missing("No non-loopback interface has comparable samples.");
        }

        return DashboardSection<NetworkMetrics>.Available(new NetworkMetrics(
            SumSaturated(rows.Select(row => row.ReceivedBytes)),
            SumSaturated(rows.Select(row => row.TransmittedBytes)),
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

            var parts = line.Split(
                (char[]?)null,
                7,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 7 ||
                !TryNonNegativeLong(parts[2], out var total) ||
                !TryNonNegativeLong(parts[3], out var used) ||
                !TryNonNegativeLong(parts[4], out var available) ||
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

        if (fileSystems.Value is null)
        {
            return warnings;
        }

        foreach (var fileSystem in fileSystems.Value
                     .Where(row => !IsPseudoFileSystem(row.FileSystemType))
                     .OrderByDescending(row => row.UsedPercent))
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

        return warnings;
    }

    private static DashboardSampleParts SplitSample(string? value, bool includeExtendedSections)
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
        var remainder = afterNet[(loadIndex + LoadMarker.Length)..];
        var load = SliceUntil(remainder, UptimeMarker, out remainder);
        var uptime = SliceUntil(remainder, MemoryMarker, out remainder);
        var memory = SliceUntil(remainder, FileSystemMarker, out var fileSystems);
        return new DashboardSampleParts(cpu, network, load, uptime, memory, fileSystems);
    }

    private static string SliceUntil(string value, string marker, out string remainder)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            remainder = string.Empty;
            return value;
        }

        remainder = value[(index + marker.Length)..];
        return value[..index];
    }

    private static bool TryParseCpuCounters(string value, out CpuCounters counters, out int logicalProcessors)
    {
        counters = default;
        logicalProcessors = 0;
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("cpu", StringComparison.Ordinal) &&
                line.Length > 3 &&
                char.IsAsciiDigit(line[3]))
            {
                logicalProcessors++;
                continue;
            }

            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = Tokens(line);
            if (parts.Length < 5)
            {
                return false;
            }

            var numbers = new long[parts.Length - 1];
            for (var index = 1; index < parts.Length; index++)
            {
                if (!TryNonNegativeLong(parts[index], out numbers[index - 1]))
                {
                    return false;
                }
            }

            var total = SumSaturated(numbers);
            var idle = SaturatingAdd(numbers[3], numbers.Length > 4 ? numbers[4] : 0);
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
            var parts = Tokens(rawLine[(separator + 1)..]);
            if (parts.Length < 16 ||
                !TryNonNegativeLong(parts[0], out var received) ||
                !TryNonNegativeLong(parts[8], out var transmitted))
            {
                continue;
            }

            result[name] = new NetworkCounters(received, transmitted);
        }

        return result;
    }

    private static string[] Tokens(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryFiniteDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private static bool TryNonNegativeLong(string value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static bool TryPercent(string value, out double result)
    {
        var trimmed = value.Trim().TrimEnd('%');
        return TryFiniteDouble(trimmed, out result) && result is >= 0d and <= 100d;
    }

    private static long SumSaturated(IEnumerable<long> values)
    {
        var result = 0L;
        foreach (var value in values)
        {
            result = SaturatingAdd(result, value);
        }

        return result;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsPseudoFileSystem(string fileSystemType) =>
        fileSystemType is "tmpfs" or "devtmpfs" or "squashfs" or "proc" or "sysfs";

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
