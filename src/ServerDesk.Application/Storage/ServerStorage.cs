using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Storage;

public sealed record ServerFilesystemInfo(
    string Device,
    string FileSystemType,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    double UsedPercent,
    string MountPoint)
{
    public bool IsWarning => UsedPercent >= 85;
}

public sealed record ServerBlockDeviceInfo(
    string Name,
    string KernelName,
    string Type,
    long SizeBytes,
    string FileSystemType,
    string MountPoint,
    string Model,
    bool? IsRotational,
    string? ParentName);

public sealed record ServerDirectoryUsageInfo(string Path, long SizeBytes);

public sealed record ServerStorageSnapshotResult(
    IReadOnlyList<ServerFilesystemInfo> Filesystems,
    IReadOnlyList<ServerBlockDeviceInfo> BlockDevices,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record ServerDirectoryAnalysisResult(
    IReadOnlyList<ServerDirectoryUsageInfo> Entries,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public interface IServerStorageService
{
    Task<ServerStorageSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<ServerDirectoryAnalysisResult> AnalyzeDirectoryAsync(
        ServerProfile profile,
        string path,
        CancellationToken cancellationToken = default);
}

public sealed class ServerStorageService : IServerStorageService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;

    public ServerStorageService(IRemoteCommandExecutorFactory commandExecutorFactory)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
    }

    public async Task<ServerStorageSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);

        var filesystemsExecution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "df",
                    ["-P", "-T", "-B1"],
                    TimeSpan.FromSeconds(20),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var filesystemError = ToReadError(filesystemsExecution, "Unable to inspect mounted filesystems.");
        if (filesystemError is not null)
        {
            return new ServerStorageSnapshotResult([], [], filesystemError);
        }

        var blockExecution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "lsblk",
                    ["--json", "--bytes", "-o", "NAME,KNAME,TYPE,SIZE,FSTYPE,MOUNTPOINT,MODEL,ROTA"],
                    TimeSpan.FromSeconds(20),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var blockError = ToReadError(blockExecution, "Unable to inspect block devices.");
        if (blockError is not null)
        {
            return new ServerStorageSnapshotResult([], [], blockError);
        }

        try
        {
            return new ServerStorageSnapshotResult(
                StorageParser.ParseFilesystems(filesystemsExecution.Command!.StandardOutput),
                StorageParser.ParseBlockDevices(blockExecution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new ServerStorageSnapshotResult(
                [],
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse remote storage output.",
                    exception.Message));
        }
    }

    public async Task<ServerDirectoryAnalysisResult> AnalyzeDirectoryAsync(
        ServerProfile profile,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/', StringComparison.Ordinal))
        {
            return new ServerDirectoryAnalysisResult(
                [],
                new RemoteError(RemoteErrorCode.InvalidEndpoint, "Directory analysis requires an absolute Linux path."));
        }

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "du",
                    ["-x", "-B1", "--max-depth=1", "--", path],
                    TimeSpan.FromMinutes(2),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var error = ToReadError(execution, $"Unable to analyze directory '{path}'.");
        if (error is not null)
        {
            return new ServerDirectoryAnalysisResult([], error);
        }

        try
        {
            return new ServerDirectoryAnalysisResult(
                StorageParser.ParseDirectoryUsage(execution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new ServerDirectoryAnalysisResult(
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse directory usage output.",
                    exception.Message));
        }
    }

    private static RemoteError? ToReadError(RemoteExecutionResult execution, string fallback)
    {
        if (execution.Error is not null)
        {
            return execution.Error;
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return null;
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, fallback);
        var code = detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.PathNotFound
            : detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.PermissionDenied
                : detail.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.CommandNotFound
                    : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, detail);
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }
}

public static partial class StorageParser
{
    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)%\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FilesystemRowRegex();

    public static IReadOnlyList<ServerFilesystemInfo> ParseFilesystems(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<ServerFilesystemInfo>();
        foreach (var raw in lines)
        {
            if (raw.StartsWith("Filesystem", StringComparison.Ordinal))
            {
                continue;
            }

            var match = FilesystemRowRegex().Match(raw);
            if (!match.Success ||
                !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !long.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var available) ||
                !double.TryParse(match.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            {
                throw new FormatException($"Malformed df row: '{raw}'.");
            }

            result.Add(new ServerFilesystemInfo(
                match.Groups[1].Value,
                match.Groups[2].Value,
                total,
                used,
                available,
                percent,
                match.Groups[7].Value.Trim()));
        }

        return result;
    }

    public static IReadOnlyList<ServerBlockDeviceInfo> ParseBlockDevices(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("lsblk JSON is missing blockdevices array.");
            }

            var result = new List<ServerBlockDeviceInfo>();
            foreach (var device in devices.EnumerateArray())
            {
                ParseBlockDevice(device, parentName: null, result);
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new FormatException("lsblk returned invalid JSON.", exception);
        }
    }

    public static IReadOnlyList<ServerDirectoryUsageInfo> ParseDirectoryUsage(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<ServerDirectoryUsageInfo>();
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = raw.IndexOfAny(['\t', ' ']);
            if (separator <= 0 ||
                !long.TryParse(raw[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                bytes < 0)
            {
                throw new FormatException($"Malformed du row: '{raw}'.");
            }

            var path = raw[(separator + 1)..].TrimStart();
            if (path.Length == 0)
            {
                throw new FormatException($"Malformed du path: '{raw}'.");
            }

            result.Add(new ServerDirectoryUsageInfo(path, bytes));
        }

        return result.OrderByDescending(entry => entry.SizeBytes).ToArray();
    }

    private static void ParseBlockDevice(
        JsonElement element,
        string? parentName,
        ICollection<ServerBlockDeviceInfo> result)
    {
        var name = RequiredString(element, "name");
        var size = RequiredInt64(element, "size");
        result.Add(new ServerBlockDeviceInfo(
            name,
            OptionalString(element, "kname"),
            OptionalString(element, "type"),
            size,
            OptionalString(element, "fstype"),
            OptionalString(element, "mountpoint"),
            OptionalString(element, "model"),
            OptionalBoolean(element, "rota"),
            parentName));

        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ParseBlockDevice(child, name, result);
            }
        }
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        return value.Length > 0
            ? value
            : throw new FormatException($"lsblk device is missing required property '{property}'.");
    }

    private static string OptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static long RequiredInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw new FormatException($"lsblk device is missing required property '{property}'.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw new FormatException($"lsblk property '{property}' is not a valid integer.");
    }

    private static bool? OptionalBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when value.GetString() is "1" or "true" => true,
            JsonValueKind.String when value.GetString() is "0" or "false" => false,
            _ => null,
        };
    }
}
