using System.Globalization;

namespace ServerDesk.Agent;

public sealed record AgentMetricsReading(
    DateTimeOffset CapturedAtUtc,
    double CpuUtilizationPercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long MemoryUsedBytes,
    double MemoryUsedPercent,
    double LoadOneMinute,
    double LoadFiveMinutes,
    double LoadFifteenMinutes);

public interface IAgentMetricsSampler
{
    ValueTask<AgentMetricsReading> CaptureAsync(
        TimeSpan samplingInterval,
        CancellationToken cancellationToken = default);
}

public sealed class LinuxMetricsSampler : IAgentMetricsSampler
{
    public async ValueTask<AgentMetricsReading> CaptureAsync(
        TimeSpan samplingInterval,
        CancellationToken cancellationToken = default)
    {
        if (samplingInterval < TimeSpan.FromMilliseconds(250) || samplingInterval > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(samplingInterval));
        }

        var firstCpuText = await File.ReadAllTextAsync("/proc/stat", cancellationToken).ConfigureAwait(false);
        var firstCpu = LinuxMetricsParser.ParseCpu(firstCpuText);
        await Task.Delay(samplingInterval, cancellationToken).ConfigureAwait(false);

        var secondCpuText = await File.ReadAllTextAsync("/proc/stat", cancellationToken).ConfigureAwait(false);
        var memoryText = await File.ReadAllTextAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
        var loadText = await File.ReadAllTextAsync("/proc/loadavg", cancellationToken).ConfigureAwait(false);
        var secondCpu = LinuxMetricsParser.ParseCpu(secondCpuText);
        var memory = LinuxMetricsParser.ParseMemory(memoryText);
        var load = LinuxMetricsParser.ParseLoad(loadText);
        return new AgentMetricsReading(
            DateTimeOffset.UtcNow,
            LinuxMetricsParser.CalculateCpuUtilization(firstCpu, secondCpu),
            memory.TotalBytes,
            memory.AvailableBytes,
            memory.UsedBytes,
            memory.UsedPercent,
            load.OneMinute,
            load.FiveMinutes,
            load.FifteenMinutes);
    }
}

internal readonly record struct CpuTimes(ulong IdleTicks, ulong TotalTicks);

internal readonly record struct ParsedMemory(
    long TotalBytes,
    long AvailableBytes,
    long UsedBytes,
    double UsedPercent);

internal readonly record struct ParsedLoad(double OneMinute, double FiveMinutes, double FifteenMinutes);

internal static class LinuxMetricsParser
{
    public static CpuTimes ParseCpu(string content)
    {
        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(candidate => candidate.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException("Linux CPU metrics are unavailable.");
        }

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            throw new InvalidOperationException("Linux CPU metrics are invalid.");
        }

        var values = fields.Skip(1).Select(ParseUnsigned).ToArray();
        var total = values.Aggregate(0UL, checkedAdd);
        var idle = checked(values[3] + (values.Length > 4 ? values[4] : 0UL));
        return new CpuTimes(idle, total);

        static ulong checkedAdd(ulong left, ulong right) => checked(left + right);
    }

    public static double CalculateCpuUtilization(CpuTimes first, CpuTimes second)
    {
        if (second.TotalTicks <= first.TotalTicks || second.IdleTicks < first.IdleTicks)
        {
            throw new InvalidOperationException("Linux CPU counters did not advance monotonically.");
        }

        var totalDelta = second.TotalTicks - first.TotalTicks;
        var idleDelta = second.IdleTicks - first.IdleTicks;
        if (idleDelta > totalDelta)
        {
            throw new InvalidOperationException("Linux CPU counters are inconsistent.");
        }

        var utilization = (totalDelta - idleDelta) * 100d / totalDelta;
        return ValidatePercent(utilization, "CPU utilization");
    }

    public static ParsedMemory ParseMemory(string content)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var fields = line[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes))
            {
                continue;
            }

            values[key] = checked(kibibytes * 1024L);
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0 ||
            !values.TryGetValue("MemAvailable", out var available) || available < 0 || available > total)
        {
            throw new InvalidOperationException("Linux memory metrics are unavailable or invalid.");
        }

        var used = total - available;
        var usedPercent = ValidatePercent(used * 100d / total, "memory utilization");
        return new ParsedMemory(total, available, used, usedPercent);
    }

    public static ParsedLoad ParseLoad(string content)
    {
        var fields = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
        {
            throw new InvalidOperationException("Linux load metrics are unavailable.");
        }

        return new ParsedLoad(
            ParseNonNegativeDouble(fields[0], "one-minute load"),
            ParseNonNegativeDouble(fields[1], "five-minute load"),
            ParseNonNegativeDouble(fields[2], "fifteen-minute load"));
    }

    private static ulong ParseUnsigned(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException("Linux CPU metrics contain an invalid counter.");
        }

        return parsed;
    }

    private static double ParseNonNegativeDouble(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) || parsed < 0d)
        {
            throw new InvalidOperationException($"Linux {name} is invalid.");
        }

        return parsed;
    }

    private static double ValidatePercent(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0d or > 100d)
        {
            throw new InvalidOperationException($"Linux {name} is outside the valid range.");
        }

        return value;
    }
}
