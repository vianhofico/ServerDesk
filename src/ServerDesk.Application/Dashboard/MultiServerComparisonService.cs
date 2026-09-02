using System.Globalization;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Dashboard;

public enum MultiServerComparisonFact
{
    Environment,
    SshPort,
    LogicalProcessors,
    TotalMemory,
    SwapTotal,
    CpuUtilization,
    MemoryUtilization,
    HighestDiskUtilization,
    WarningCount,
    CriticalWarningPresent,
}

public enum MultiServerComparisonValueStatus
{
    Available,
    Unknown,
    Unsupported,
}

public enum MultiServerComparisonFactStatus
{
    Equal,
    Different,
    Incomplete,
}

public sealed record MultiServerComparisonInput(
    ServerProfile Profile,
    ServerDashboardSnapshot? Snapshot);

public sealed record MultiServerComparisonServer(
    Guid ServerProfileId,
    string Name,
    string Endpoint);

public sealed record MultiServerComparisonValue(
    Guid ServerProfileId,
    MultiServerComparisonValueStatus Status,
    string? DisplayValue,
    string? CanonicalValue);

public sealed record MultiServerComparisonFactResult(
    MultiServerComparisonFact Fact,
    MultiServerComparisonFactStatus Status,
    IReadOnlyList<MultiServerComparisonValue> Values);

public sealed record MultiServerComparisonResult(
    IReadOnlyList<MultiServerComparisonServer> Servers,
    IReadOnlyList<MultiServerComparisonFactResult> Facts);

public interface IMultiServerComparisonService
{
    MultiServerComparisonResult Compare(IReadOnlyList<MultiServerComparisonInput> inputs);
}

public sealed class MultiServerComparisonService : IMultiServerComparisonService
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public MultiServerComparisonResult Compare(IReadOnlyList<MultiServerComparisonInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count < 2)
        {
            throw new ArgumentException("At least two servers are required for comparison.", nameof(inputs));
        }

        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Profile);
            if (input.Snapshot is not null && input.Snapshot.ServerProfileId != input.Profile.Id)
            {
                throw new ArgumentException("Comparison snapshots must belong to their server profile.", nameof(inputs));
            }
        }

        if (inputs.Select(input => input.Profile.Id).Distinct().Count() != inputs.Count)
        {
            throw new ArgumentException("Comparison servers must have unique profile ids.", nameof(inputs));
        }

        var servers = inputs
            .Select(input => new MultiServerComparisonServer(
                input.Profile.Id,
                input.Profile.Name,
                $"{input.Profile.Username}@{input.Profile.Host}:{input.Profile.Port}"))
            .ToArray();
        var facts = Enum.GetValues<MultiServerComparisonFact>()
            .Select(fact => BuildFact(fact, inputs))
            .ToArray();
        return new MultiServerComparisonResult(servers, facts);
    }

    private static MultiServerComparisonFactResult BuildFact(
        MultiServerComparisonFact fact,
        IReadOnlyList<MultiServerComparisonInput> inputs)
    {
        var values = inputs.Select(input => ExtractValue(fact, input)).ToArray();
        var status = values.All(value => value.Status == MultiServerComparisonValueStatus.Available)
            ? values.Select(value => value.CanonicalValue).Distinct(StringComparer.Ordinal).Count() == 1
                ? MultiServerComparisonFactStatus.Equal
                : MultiServerComparisonFactStatus.Different
            : MultiServerComparisonFactStatus.Incomplete;
        return new MultiServerComparisonFactResult(fact, status, values);
    }

    private static MultiServerComparisonValue ExtractValue(
        MultiServerComparisonFact fact,
        MultiServerComparisonInput input)
    {
        var profile = input.Profile;
        var id = profile.Id;
        return fact switch
        {
            MultiServerComparisonFact.Environment => StringValue(id, profile.Environment, ignoreCase: true),
            MultiServerComparisonFact.SshPort => IntegerValue(id, profile.Port),
            MultiServerComparisonFact.LogicalProcessors => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.Cpu,
                cpu => cpu.LogicalProcessors > 0
                    ? IntegerValue(id, cpu.LogicalProcessors)
                    : Unknown(id)),
            MultiServerComparisonFact.TotalMemory => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.Memory,
                memory => BytesValue(id, memory.TotalBytes)),
            MultiServerComparisonFact.SwapTotal => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.Memory,
                memory => BytesValue(id, memory.SwapTotalBytes)),
            MultiServerComparisonFact.CpuUtilization => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.Cpu,
                cpu => PercentValue(id, cpu.UtilizationPercent)),
            MultiServerComparisonFact.MemoryUtilization => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.Memory,
                memory => PercentValue(id, memory.UsedPercent)),
            MultiServerComparisonFact.HighestDiskUtilization => FromSection(
                id,
                input.Snapshot,
                snapshot => snapshot.FileSystems,
                fileSystems => HighestDiskValue(id, fileSystems)),
            MultiServerComparisonFact.WarningCount => input.Snapshot is null
                ? Unknown(id)
                : IntegerValue(id, input.Snapshot.Warnings.Count),
            MultiServerComparisonFact.CriticalWarningPresent => input.Snapshot is null
                ? Unknown(id)
                : BooleanValue(
                    id,
                    input.Snapshot.Warnings.Any(warning => warning.Severity == DashboardWarningSeverity.Critical)),
            _ => Unknown(id),
        };
    }

    private static MultiServerComparisonValue FromSection<T>(
        Guid serverProfileId,
        ServerDashboardSnapshot? snapshot,
        Func<ServerDashboardSnapshot, DashboardSection<T>> selector,
        Func<T, MultiServerComparisonValue> available)
        where T : class
    {
        if (snapshot is null)
        {
            return Unknown(serverProfileId);
        }

        var section = selector(snapshot);
        return section.Status switch
        {
            DashboardSectionStatus.Missing => Unsupported(serverProfileId),
            DashboardSectionStatus.Invalid => Unknown(serverProfileId),
            DashboardSectionStatus.Available when section.Value is not null => available(section.Value),
            _ => Unknown(serverProfileId),
        };
    }

    private static MultiServerComparisonValue StringValue(
        Guid serverProfileId,
        string? value,
        bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown(serverProfileId);
        }

        var display = value.Trim();
        var canonical = ignoreCase ? display.ToUpperInvariant() : display;
        return Available(serverProfileId, display, canonical);
    }

    private static MultiServerComparisonValue IntegerValue(Guid serverProfileId, long value) =>
        Available(
            serverProfileId,
            value.ToString(Invariant),
            value.ToString(Invariant));

    private static MultiServerComparisonValue BytesValue(Guid serverProfileId, long value)
    {
        if (value < 0)
        {
            return Unknown(serverProfileId);
        }

        return Available(
            serverProfileId,
            FormatBytes(value),
            value.ToString(Invariant));
    }

    private static MultiServerComparisonValue PercentValue(Guid serverProfileId, double value)
    {
        if (!double.IsFinite(value) || value is < 0d or > 100d)
        {
            return Unknown(serverProfileId);
        }

        var normalized = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var canonical = normalized.ToString("F1", Invariant);
        return Available(serverProfileId, $"{canonical}%", canonical);
    }

    private static MultiServerComparisonValue HighestDiskValue(
        Guid serverProfileId,
        IReadOnlyList<FileSystemMetrics> fileSystems)
    {
        var values = fileSystems
            .Select(fileSystem => fileSystem.UsedPercent)
            .Where(value => double.IsFinite(value) && value is >= 0d and <= 100d)
            .ToArray();
        return values.Length == 0
            ? Unknown(serverProfileId)
            : PercentValue(serverProfileId, values.Max());
    }

    private static MultiServerComparisonValue BooleanValue(Guid serverProfileId, bool value) =>
        Available(serverProfileId, value ? "true" : "false", value ? "true" : "false");

    private static MultiServerComparisonValue Available(
        Guid serverProfileId,
        string displayValue,
        string canonicalValue) =>
        new(serverProfileId, MultiServerComparisonValueStatus.Available, displayValue, canonicalValue);

    private static MultiServerComparisonValue Unknown(Guid serverProfileId) =>
        new(serverProfileId, MultiServerComparisonValueStatus.Unknown, null, null);

    private static MultiServerComparisonValue Unsupported(Guid serverProfileId) =>
        new(serverProfileId, MultiServerComparisonValueStatus.Unsupported, null, null);

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024d;
        const double mib = kib * 1024d;
        const double gib = mib * 1024d;
        const double tib = gib * 1024d;
        return bytes switch
        {
            >= (long)tib => $"{bytes / tib:0.##} TiB",
            >= (long)gib => $"{bytes / gib:0.##} GiB",
            >= (long)mib => $"{bytes / mib:0.##} MiB",
            >= (long)kib => $"{bytes / kib:0.##} KiB",
            _ => $"{bytes.ToString(Invariant)} B",
        };
    }
}
