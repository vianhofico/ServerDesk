using System.Globalization;
using System.Text;
using System.Text.Json;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public enum DockerRuntimeStatus
{
    Available,
    CliUnavailable,
    DaemonUnavailable,
    PermissionDenied,
    Unsupported,
    Unknown,
}

public sealed record DockerRuntimeState(
    DockerRuntimeStatus Status,
    string CliVersion,
    string EngineVersion,
    string ApiVersion,
    string OperatingSystem,
    string Architecture,
    string Detail)
{
    public bool IsUsable => Status == DockerRuntimeStatus.Available;
}

public sealed record DockerSystemSummary(
    int Containers,
    int ContainersRunning,
    int ContainersPaused,
    int ContainersStopped,
    int Images,
    string StorageDriver,
    string DockerRootDirectory,
    string ServerVersion,
    string OperatingSystem,
    string OsType,
    string Architecture,
    int CpuCount,
    long MemoryBytes,
    string Hostname);

public sealed record DockerContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    string Ports,
    string Mounts,
    string Networks,
    string CreatedAt,
    string Size);

public sealed record DockerImageInfo(
    string Id,
    string Repository,
    string Tag,
    string Digest,
    string CreatedAt,
    string Size);

public sealed record DockerVolumeInfo(
    string Name,
    string Driver,
    string Scope,
    string Mountpoint,
    string Labels);

public sealed record DockerNetworkInfo(
    string Id,
    string Name,
    string Driver,
    string Scope,
    bool? Ipv6,
    bool? Internal,
    string Labels);

public sealed record DockerInventorySnapshot(
    DockerRuntimeState Runtime,
    DockerSystemSummary? System,
    IReadOnlyList<DockerContainerInfo> Containers,
    IReadOnlyList<DockerImageInfo> Images,
    IReadOnlyList<DockerVolumeInfo> Volumes,
    IReadOnlyList<DockerNetworkInfo> Networks);

public sealed record DockerInventoryResult(
    DockerInventorySnapshot? Snapshot,
    IReadOnlyList<RemoteError> Warnings,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;

    public bool IsPartial => Error is null && Warnings.Count > 0;
}

public sealed record DockerInventoryOptions(TimeSpan CommandTimeout)
{
    public static DockerInventoryOptions Default { get; } = new(TimeSpan.FromSeconds(20));

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
    }
}

public interface IDockerInventoryService
{
    Task<DockerInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class DockerInventoryService : IDockerInventoryService
{
    private const string JsonTemplate = "{{json .}}";
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;
    private readonly DockerInventoryOptions _options;

    public DockerInventoryService(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        DockerInventoryOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DockerInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);

        var runtimeProbe = await ProbeRuntimeAsync(executor, cancellationToken).ConfigureAwait(false);
        if (runtimeProbe.Error is not null)
        {
            return new DockerInventoryResult(null, [], runtimeProbe.Error);
        }

        var runtime = runtimeProbe.Runtime!;
        if (!runtime.IsUsable)
        {
            return new DockerInventoryResult(
                new DockerInventorySnapshot(runtime, null, [], [], [], []),
                [],
                null);
        }

        var warnings = new List<RemoteError>();
        DockerSystemSummary? system = null;
        IReadOnlyList<DockerContainerInfo> containers = [];
        IReadOnlyList<DockerImageInfo> images = [];
        IReadOnlyList<DockerVolumeInfo> volumes = [];
        IReadOnlyList<DockerNetworkInfo> networks = [];

        var info = await ExecuteDockerAsync(
            executor,
            ["info", "--format", JsonTemplate],
            "Unable to inspect Docker system information.",
            cancellationToken).ConfigureAwait(false);
        if (info.Error is null)
        {
            if (!TryParse(() => DockerInventoryParser.ParseSystemInfo(info.Output!), out system, out var parseError))
            {
                warnings.Add(parseError!);
            }
        }
        else
        {
            warnings.Add(info.Error);
        }

        var containerRead = await ExecuteDockerAsync(
            executor,
            ["container", "ls", "--all", "--no-trunc", "--format", JsonTemplate],
            "Unable to list Docker containers.",
            cancellationToken).ConfigureAwait(false);
        if (containerRead.Error is null)
        {
            if (!TryParse(() => DockerInventoryParser.ParseContainers(containerRead.Output!), out containers, out var parseError))
            {
                warnings.Add(parseError!);
            }
        }
        else
        {
            warnings.Add(containerRead.Error);
        }

        var imageRead = await ExecuteDockerAsync(
            executor,
            ["image", "ls", "--all", "--no-trunc", "--format", JsonTemplate],
            "Unable to list Docker images.",
            cancellationToken).ConfigureAwait(false);
        if (imageRead.Error is null)
        {
            if (!TryParse(() => DockerInventoryParser.ParseImages(imageRead.Output!), out images, out var parseError))
            {
                warnings.Add(parseError!);
            }
        }
        else
        {
            warnings.Add(imageRead.Error);
        }

        var volumeRead = await ExecuteDockerAsync(
            executor,
            ["volume", "ls", "--format", JsonTemplate],
            "Unable to list Docker volumes.",
            cancellationToken).ConfigureAwait(false);
        if (volumeRead.Error is null)
        {
            if (!TryParse(() => DockerInventoryParser.ParseVolumes(volumeRead.Output!), out volumes, out var parseError))
            {
                warnings.Add(parseError!);
            }
        }
        else
        {
            warnings.Add(volumeRead.Error);
        }

        var networkRead = await ExecuteDockerAsync(
            executor,
            ["network", "ls", "--no-trunc", "--format", JsonTemplate],
            "Unable to list Docker networks.",
            cancellationToken).ConfigureAwait(false);
        if (networkRead.Error is null)
        {
            if (!TryParse(() => DockerInventoryParser.ParseNetworks(networkRead.Output!), out networks, out var parseError))
            {
                warnings.Add(parseError!);
            }
        }
        else
        {
            warnings.Add(networkRead.Error);
        }

        return new DockerInventoryResult(
            new DockerInventorySnapshot(runtime, system, containers, images, volumes, networks),
            warnings,
            null);
    }

    private async Task<RuntimeProbeResult> ProbeRuntimeAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var cli = await ExecuteDockerAsync(
            executor,
            ["--version"],
            "Unable to detect the Docker CLI.",
            cancellationToken,
            classifyMissingAsCapability: true).ConfigureAwait(false);
        if (cli.Error is not null)
        {
            if (cli.Error.Code == RemoteErrorCode.CommandNotFound)
            {
                return new RuntimeProbeResult(
                    new DockerRuntimeState(
                        DockerRuntimeStatus.CliUnavailable,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "Docker CLI was not found in PATH."),
                    null);
            }

            return new RuntimeProbeResult(null, cli.Error);
        }

        var cliVersion = FirstNonEmptyLine(cli.Output!);
        var version = await ExecuteDockerAsync(
            executor,
            ["version", "--format", JsonTemplate],
            "Unable to query the Docker daemon.",
            cancellationToken).ConfigureAwait(false);
        if (version.Error is not null)
        {
            var status = version.Error.Code switch
            {
                RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => DockerRuntimeStatus.PermissionDenied,
                RemoteErrorCode.CapabilityUnavailable => DockerRuntimeStatus.DaemonUnavailable,
                RemoteErrorCode.UnsupportedVersion => DockerRuntimeStatus.Unsupported,
                RemoteErrorCode.CommandNotFound => DockerRuntimeStatus.CliUnavailable,
                _ => DockerRuntimeStatus.Unknown,
            };
            if (status == DockerRuntimeStatus.Unknown &&
                version.Error.Code is RemoteErrorCode.ConnectionFailed or RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.OperationCancelled)
            {
                return new RuntimeProbeResult(null, version.Error);
            }

            return new RuntimeProbeResult(
                new DockerRuntimeState(
                    status,
                    cliVersion,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    version.Error.Message),
                null);
        }

        try
        {
            var parsed = DockerInventoryParser.ParseRuntimeVersion(version.Output!);
            return new RuntimeProbeResult(
                parsed with { CliVersion = cliVersion.Length > 0 ? cliVersion : parsed.CliVersion },
                null);
        }
        catch (FormatException exception)
        {
            return new RuntimeProbeResult(
                null,
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse Docker version output.",
                    exception.Message));
        }
    }

    private async Task<DockerReadResult> ExecuteDockerAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        string fallback,
        CancellationToken cancellationToken,
        bool classifyMissingAsCapability = false)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "docker",
                    arguments,
                    _options.CommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var error = ToDockerError(execution, fallback, classifyMissingAsCapability);
        return error is null
            ? new DockerReadResult(execution.Command!.StandardOutput, null)
            : new DockerReadResult(null, error);
    }

    private static RemoteError? ToDockerError(
        RemoteExecutionResult execution,
        string fallback,
        bool classifyMissingAsCapability)
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
        var combined = $"{command.StandardError}\n{command.StandardOutput}";
        var code = LooksLikePermissionDenied(combined)
            ? RemoteErrorCode.PermissionDenied
            : LooksLikeDaemonUnavailable(combined)
                ? RemoteErrorCode.CapabilityUnavailable
                : LooksLikeUnsupportedVersion(combined)
                    ? RemoteErrorCode.UnsupportedVersion
                    : LooksLikeCommandMissing(combined) || classifyMissingAsCapability && command.ExitCode == 127
                        ? RemoteErrorCode.CommandNotFound
                        : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, detail);
    }

    private static bool LooksLikePermissionDenied(string value) =>
        value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("permission denied while trying to connect", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("got permission denied", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDaemonUnavailable(string value) =>
        value.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("error during connect", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("docker daemon is not running", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed to connect to the docker api", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUnsupportedVersion(string value) =>
        value.Contains("client version", StringComparison.OrdinalIgnoreCase) &&
        value.Contains("too new", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("minimum supported api version", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api version is too old", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCommandMissing(string value) =>
        value.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("docker: not found", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase);

    private static bool TryParse<T>(Func<T> parser, out T value, out RemoteError? error)
    {
        try
        {
            value = parser();
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            value = default!;
            error = new RemoteError(
                RemoteErrorCode.ParseFailed,
                "ServerDesk could not parse structured Docker output.",
                exception.Message);
            return false;
        }
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second) ? Sanitize(second.Trim()) : fallback;
    }

    private static string FirstNonEmptyLine(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var replacement = character switch
            {
                '\t' => '\t',
                '\r' or '\n' => ' ',
                _ when char.IsControl(character) => '\uFFFD',
                _ => character,
            };
            if (replacement == character && builder is null)
            {
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            builder.Append(replacement);
        }

        return builder?.ToString() ?? value;
    }

    private sealed record DockerReadResult(string? Output, RemoteError? Error);

    private sealed record RuntimeProbeResult(DockerRuntimeState? Runtime, RemoteError? Error);
}

public static class DockerInventoryParser
{
    public static DockerRuntimeState ParseRuntimeVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = RequireObject(document.RootElement, "Docker version root");
            var client = OptionalObject(root, "Client");
            var server = OptionalObject(root, "Server");
            if (server is null)
            {
                throw new FormatException("Docker version output does not contain a Server object.");
            }

            return new DockerRuntimeState(
                DockerRuntimeStatus.Available,
                Text(client, "Version"),
                Text(server.Value, "Version"),
                Text(server.Value, "ApiVersion"),
                Text(server.Value, "Os"),
                Text(server.Value, "Arch"),
                "Docker CLI can communicate with the Docker daemon for the current user.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker version output is not valid JSON.", exception);
        }
    }

    public static DockerSystemSummary ParseSystemInfo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = RequireObject(document.RootElement, "Docker info root");
            return new DockerSystemSummary(
                Integer(root, "Containers"),
                Integer(root, "ContainersRunning"),
                Integer(root, "ContainersPaused"),
                Integer(root, "ContainersStopped"),
                Integer(root, "Images"),
                Text(root, "Driver"),
                Text(root, "DockerRootDir"),
                Text(root, "ServerVersion"),
                Text(root, "OperatingSystem"),
                Text(root, "OSType"),
                Text(root, "Architecture"),
                Integer(root, "NCPU"),
                Long(root, "MemTotal"),
                Text(root, "Name"));
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker info output is not valid JSON.", exception);
        }
    }

    public static IReadOnlyList<DockerContainerInfo> ParseContainers(string output) =>
        ParseJsonLines(output, element => new DockerContainerInfo(
            RequiredText(element, "ID", "container"),
            Text(element, "Names"),
            Text(element, "Image"),
            Text(element, "State"),
            Text(element, "Status"),
            Text(element, "Ports"),
            Text(element, "Mounts"),
            Text(element, "Networks"),
            Text(element, "CreatedAt"),
            Text(element, "Size")))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<DockerImageInfo> ParseImages(string output) =>
        ParseJsonLines(output, element => new DockerImageInfo(
            RequiredText(element, "ID", "image"),
            Text(element, "Repository"),
            Text(element, "Tag"),
            Text(element, "Digest"),
            Text(element, "CreatedAt"),
            Text(element, "Size")))
        .OrderBy(item => item.Repository, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<DockerVolumeInfo> ParseVolumes(string output) =>
        ParseJsonLines(output, element => new DockerVolumeInfo(
            RequiredText(element, "Name", "volume"),
            Text(element, "Driver"),
            Text(element, "Scope"),
            Text(element, "Mountpoint"),
            Text(element, "Labels")))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<DockerNetworkInfo> ParseNetworks(string output) =>
        ParseJsonLines(output, element => new DockerNetworkInfo(
            RequiredText(element, "ID", "network"),
            Text(element, "Name"),
            Text(element, "Driver"),
            Text(element, "Scope"),
            Boolean(element, "IPv6"),
            Boolean(element, "Internal"),
            Text(element, "Labels")))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<T> ParseJsonLines<T>(string output, Func<JsonElement, T> projector)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<T>();
        using var reader = new StringReader(output.Replace("\r\n", "\n", StringComparison.Ordinal));
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = RequireObject(document.RootElement, $"Docker row {lineNumber}");
                result.Add(projector(root));
            }
            catch (JsonException exception)
            {
                throw new FormatException($"Docker row {lineNumber} is not valid JSON.", exception);
            }
        }

        return result;
    }

    private static JsonElement RequireObject(JsonElement value, string label) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FormatException($"{label} is not a JSON object.");

    private static JsonElement? OptionalObject(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FormatException($"Docker property '{property}' is not an object.");
    }

    private static string RequiredText(JsonElement element, string property, string resource)
    {
        var value = Text(element, property);
        return value.Length > 0
            ? value
            : throw new FormatException($"Docker {resource} row is missing '{property}'.");
    }

    private static string Text(JsonElement? element, string property) =>
        element is null ? string.Empty : Text(element.Value, property);

    private static string Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
        return DockerInventoryService.Sanitize(text);
    }

    private static int Integer(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return Math.Max(0, number);
        }

        return int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? Math.Max(0, number)
            : 0;
    }

    private static long Long(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(0, number);
        }

        return long.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? Math.Max(0, number)
            : 0;
    }

    private static bool? Boolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return bool.TryParse(Text(element, property), out var parsed) ? parsed : null;
    }
}

public static class DockerInventoryProjection
{
    public static IReadOnlyList<DockerContainerInfo> FilterContainers(
        IEnumerable<DockerContainerInfo> rows,
        string search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = search.Trim();
        return rows.Where(row => text.Length == 0 ||
                Contains(text, row.Id, row.Name, row.Image, row.State, row.Status, row.Ports, row.Mounts, row.Networks))
            .ToArray();
    }

    public static IReadOnlyList<DockerImageInfo> FilterImages(
        IEnumerable<DockerImageInfo> rows,
        string search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = search.Trim();
        return rows.Where(row => text.Length == 0 || Contains(text, row.Id, row.Repository, row.Tag, row.Digest, row.Size))
            .ToArray();
    }

    public static IReadOnlyList<DockerVolumeInfo> FilterVolumes(
        IEnumerable<DockerVolumeInfo> rows,
        string search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = search.Trim();
        return rows.Where(row => text.Length == 0 || Contains(text, row.Name, row.Driver, row.Scope, row.Mountpoint, row.Labels))
            .ToArray();
    }

    public static IReadOnlyList<DockerNetworkInfo> FilterNetworks(
        IEnumerable<DockerNetworkInfo> rows,
        string search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = search.Trim();
        return rows.Where(row => text.Length == 0 || Contains(text, row.Id, row.Name, row.Driver, row.Scope, row.Labels))
            .ToArray();
    }

    private static bool Contains(string search, params string[] values) =>
        values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
}
