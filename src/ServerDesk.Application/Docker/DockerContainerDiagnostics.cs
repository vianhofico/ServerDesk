using System.Globalization;
using System.Text;
using System.Text.Json;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public sealed record DockerContainerStateDetails(
    string Status,
    bool Running,
    bool Paused,
    bool Restarting,
    bool OomKilled,
    bool Dead,
    int? ProcessId,
    int? ExitCode,
    string StartedAt,
    string FinishedAt,
    string HealthStatus);

public sealed record DockerEnvironmentVariable(
    string Name,
    string DisplayValue,
    bool IsSensitive);

public sealed record DockerContainerMountInfo(
    string Type,
    string Source,
    string Destination,
    string Mode,
    bool? ReadWrite,
    string Propagation);

public sealed record DockerContainerNetworkInfo(
    string Name,
    string IpAddress,
    string Gateway,
    string MacAddress);

public sealed record DockerContainerDetails(
    string Id,
    string Name,
    string Image,
    string CreatedAt,
    string Path,
    IReadOnlyList<string> Arguments,
    string User,
    string WorkingDirectory,
    int RestartCount,
    DockerContainerStateDetails State,
    IReadOnlyList<DockerEnvironmentVariable> Environment,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<DockerContainerMountInfo> Mounts,
    IReadOnlyList<DockerContainerNetworkInfo> Networks);

public sealed record DockerContainerStats(
    DateTimeOffset CapturedAtUtc,
    double? CpuPercent,
    long? MemoryUsageBytes,
    long? MemoryLimitBytes,
    double? MemoryPercent,
    long? NetworkInputBytes,
    long? NetworkOutputBytes,
    long? BlockReadBytes,
    long? BlockWriteBytes,
    int? ProcessCount);

public enum DockerLogStream
{
    Stdout,
    Stderr,
}

public sealed record DockerContainerLogEntry(
    DateTimeOffset? Timestamp,
    string TimestampToken,
    DockerLogStream Stream,
    string Message);

public sealed record DockerContainerDetailsResult(
    DockerContainerDetails? Details,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record DockerContainerStatsResult(
    DockerContainerStats? Stats,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record DockerContainerLogReadResult(
    IReadOnlyList<DockerContainerLogEntry> Entries,
    string? LastTimestampToken,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record DockerContainerDiagnosticsOptions(
    int DefaultRecentLogRows,
    int MaxRetainedLogRows,
    TimeSpan StatsPollInterval,
    TimeSpan LogPollInterval,
    TimeSpan CommandTimeout)
{
    public static DockerContainerDiagnosticsOptions Default { get; } = new(
        200,
        5_000,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(20));

    public void Validate()
    {
        if (DefaultRecentLogRows <= 0 || MaxRetainedLogRows < DefaultRecentLogRows)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultRecentLogRows));
        }

        if (StatsPollInterval <= TimeSpan.Zero || StatsPollInterval > TimeSpan.FromMinutes(1) ||
            LogPollInterval <= TimeSpan.Zero || LogPollInterval > TimeSpan.FromMinutes(1) ||
            CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
    }
}

public interface IDockerContainerDiagnosticsService
{
    DockerContainerDiagnosticsOptions Options { get; }

    Task<DockerContainerDetailsResult> InspectAsync(
        ServerProfile profile,
        string containerId,
        CancellationToken cancellationToken = default);

    Task<DockerContainerStatsResult> ReadStatsAsync(
        ServerProfile profile,
        string containerId,
        CancellationToken cancellationToken = default);

    Task<DockerContainerLogReadResult> ReadRecentLogsAsync(
        ServerProfile profile,
        string containerId,
        int? count = null,
        CancellationToken cancellationToken = default);

    Task<DockerContainerLogReadResult> ReadLogsSinceAsync(
        ServerProfile profile,
        string containerId,
        string timestampToken,
        int? count = null,
        CancellationToken cancellationToken = default);
}

public sealed class DockerContainerDiagnosticsService : IDockerContainerDiagnosticsService
{
    private const string JsonTemplate = "{{json .}}";
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;

    public DockerContainerDiagnosticsService(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        DockerContainerDiagnosticsOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
    }

    public DockerContainerDiagnosticsOptions Options { get; }

    public async Task<DockerContainerDetailsResult> InspectAsync(
        ServerProfile profile,
        string containerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await ExecuteAsync(
                executor,
                ["container", "inspect", "--", id],
                cancellationToken)
            .ConfigureAwait(false);
        var error = DockerDiagnosticsErrorMapper.ToReadError(execution, "Unable to inspect the Docker container.");
        if (error is not null)
        {
            return new DockerContainerDetailsResult(null, error);
        }

        try
        {
            return new DockerContainerDetailsResult(
                DockerContainerDiagnosticsParser.ParseInspect(execution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new DockerContainerDetailsResult(
                null,
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse Docker container inspect output.",
                    exception.Message));
        }
    }

    public async Task<DockerContainerStatsResult> ReadStatsAsync(
        ServerProfile profile,
        string containerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await ExecuteAsync(
                executor,
                ["stats", "--no-stream", "--format", JsonTemplate, "--", id],
                cancellationToken)
            .ConfigureAwait(false);
        var error = DockerDiagnosticsErrorMapper.ToReadError(execution, "Unable to read Docker container stats.");
        if (error is not null)
        {
            return new DockerContainerStatsResult(null, error);
        }

        try
        {
            return new DockerContainerStatsResult(
                DockerContainerDiagnosticsParser.ParseStats(execution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new DockerContainerStatsResult(
                null,
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse Docker container stats output.",
                    exception.Message));
        }
    }

    public Task<DockerContainerLogReadResult> ReadRecentLogsAsync(
        ServerProfile profile,
        string containerId,
        int? count = null,
        CancellationToken cancellationToken = default) =>
        ReadLogsCoreAsync(profile, containerId, null, count, cancellationToken);

    public Task<DockerContainerLogReadResult> ReadLogsSinceAsync(
        ServerProfile profile,
        string containerId,
        string timestampToken,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampToken);
        if (timestampToken.Length > 128 || timestampToken.Contains('\0'))
        {
            throw new ArgumentException("Docker log timestamp token is invalid.", nameof(timestampToken));
        }

        if (!DateTimeOffset.TryParse(
                timestampToken,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out _))
        {
            throw new ArgumentException("Docker log timestamp token is not a valid RFC3339 timestamp.", nameof(timestampToken));
        }

        return ReadLogsCoreAsync(profile, containerId, timestampToken, count, cancellationToken);
    }

    private async Task<DockerContainerLogReadResult> ReadLogsCoreAsync(
        ServerProfile profile,
        string containerId,
        string? since,
        int? count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);
        var rows = NormalizeLogCount(count);
        var arguments = new List<string>
        {
            "container",
            "logs",
            "--timestamps",
            "--tail",
            rows.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(since))
        {
            arguments.Add("--since");
            arguments.Add(since);
        }

        arguments.Add("--");
        arguments.Add(id);

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await ExecuteAsync(executor, arguments, cancellationToken).ConfigureAwait(false);
        var error = DockerDiagnosticsErrorMapper.ToReadError(execution, "Unable to read Docker container logs.");
        if (error is not null)
        {
            return new DockerContainerLogReadResult([], null, error);
        }

        var entries = DockerContainerDiagnosticsParser.ParseLogs(
            execution.Command!.StandardOutput,
            execution.Command.StandardError);
        return new DockerContainerLogReadResult(
            entries,
            entries.LastOrDefault(entry => !string.IsNullOrWhiteSpace(entry.TimestampToken))?.TimestampToken,
            null);
    }

    private Task<RemoteExecutionResult> ExecuteAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            new RemoteCommandSpec(
                "docker",
                arguments,
                Options.CommandTimeout,
                OperationRisk.ReadOnly,
                StableEnvironment),
            cancellationToken);

    private int NormalizeLogCount(int? count)
    {
        var rows = count ?? Options.DefaultRecentLogRows;
        if (rows <= 0 || rows > Options.MaxRetainedLogRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"Docker log row count must be between 1 and {Options.MaxRetainedLogRows:N0}.");
        }

        return rows;
    }
}

public static class DockerContainerIdentifier
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var id = value.Trim();
        if (id.Length is < 12 or > 64 || !id.All(IsHex))
        {
            throw new ArgumentException(
                "Docker container diagnostics require a 12-64 character hexadecimal container ID.",
                nameof(value));
        }

        return id.ToLowerInvariant();
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

internal static class DockerDiagnosticsErrorMapper
{
    public static RemoteError? ToReadError(RemoteExecutionResult execution, string fallback)
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

        var text = $"{command.StandardError}\n{command.StandardOutput}";
        var detail = FirstUseful(command.StandardError, command.StandardOutput, fallback);
        var code = LooksLikePermissionDenied(text)
            ? RemoteErrorCode.PermissionDenied
            : LooksLikeContainerMissing(text)
                ? RemoteErrorCode.PathNotFound
                : LooksLikeDaemonUnavailable(text)
                    ? RemoteErrorCode.CapabilityUnavailable
                    : LooksLikeUnsupported(text)
                        ? RemoteErrorCode.UnsupportedVersion
                        : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, detail);
    }

    private static bool LooksLikePermissionDenied(string value) =>
        value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeContainerMissing(string value) =>
        value.Contains("no such container", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("container not found", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDaemonUnavailable(string value) =>
        value.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("error during connect", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUnsupported(string value) =>
        value.Contains("minimum supported api version", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api version is too old", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("client version", StringComparison.OrdinalIgnoreCase) &&
        value.Contains("too new", StringComparison.OrdinalIgnoreCase);

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return DockerDiagnosticsText.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second)
            ? DockerDiagnosticsText.Sanitize(second.Trim())
            : fallback;
    }
}

internal static class DockerDiagnosticsText
{
    public static string Sanitize(string? value)
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
}

public static class DockerContainerDiagnosticsParser
{
    private static readonly string[] SensitiveMarkers =
    [
        "PASSWORD",
        "PASSWD",
        "TOKEN",
        "SECRET",
        "API_KEY",
        "APIKEY",
        "PRIVATE_KEY",
        "ACCESS_KEY",
        "CREDENTIAL",
        "AUTH",
        "COOKIE",
        "SESSION",
        "CONNECTION_STRING",
        "DATABASE_URL",
        "REDIS_URL",
        "DSN",
    ];

    public static DockerContainerDetails ParseInspect(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
            {
                throw new FormatException("Docker inspect output must contain exactly one container object.");
            }

            var root = document.RootElement[0];
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Docker inspect container entry is not an object.");
            }

            var config = Object(root, "Config");
            var state = Object(root, "State");
            var networkSettings = Object(root, "NetworkSettings");
            var id = RequiredText(root, "Id", "container");
            var name = Text(root, "Name").TrimStart('/');
            var health = ObjectOrNull(state, "Health");
            return new DockerContainerDetails(
                id,
                name,
                Text(config, "Image"),
                Text(root, "Created"),
                Text(root, "Path"),
                StringArray(root, "Args"),
                Text(config, "User"),
                Text(config, "WorkingDir"),
                Integer(root, "RestartCount") ?? 0,
                new DockerContainerStateDetails(
                    Text(state, "Status"),
                    Boolean(state, "Running") ?? false,
                    Boolean(state, "Paused") ?? false,
                    Boolean(state, "Restarting") ?? false,
                    Boolean(state, "OOMKilled") ?? false,
                    Boolean(state, "Dead") ?? false,
                    Integer(state, "Pid"),
                    Integer(state, "ExitCode"),
                    Text(state, "StartedAt"),
                    Text(state, "FinishedAt"),
                    health is null ? string.Empty : Text(health.Value, "Status")),
                ParseEnvironment(config),
                ParseLabels(config),
                ParseMounts(root),
                ParseNetworks(networkSettings));
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker inspect output is not valid JSON.", exception);
        }
    }

    public static DockerContainerStats ParseStats(string output)
    {
        var line = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
        {
            throw new FormatException("Docker stats output is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Docker stats row is not a JSON object.");
            }

            var memory = ParseBytePair(Text(root, "MemUsage"));
            var network = ParseBytePair(Text(root, "NetIO"));
            var block = ParseBytePair(Text(root, "BlockIO"));
            return new DockerContainerStats(
                DateTimeOffset.UtcNow,
                ParsePercent(Text(root, "CPUPerc")),
                memory.Left,
                memory.Right,
                ParsePercent(Text(root, "MemPerc")),
                network.Left,
                network.Right,
                block.Left,
                block.Right,
                ParsePositiveInt(Text(root, "PIDs")));
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker stats output is not valid JSON.", exception);
        }
    }

    public static IReadOnlyList<DockerContainerLogEntry> ParseLogs(string stdout, string stderr)
    {
        var result = new List<DockerContainerLogEntry>();
        AddLogLines(result, stdout, DockerLogStream.Stdout);
        AddLogLines(result, stderr, DockerLogStream.Stderr);
        return result
            .OrderBy(entry => entry.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.Stream)
            .ToArray();
    }

    public static bool IsSensitiveEnvironmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = new string(name.Trim().ToUpperInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray());
        return SensitiveMarkers.Any(marker =>
            normalized.Equals(marker, StringComparison.Ordinal) ||
            normalized.Contains($"_{marker}_", StringComparison.Ordinal) ||
            normalized.StartsWith($"{marker}_", StringComparison.Ordinal) ||
            normalized.EndsWith($"_{marker}", StringComparison.Ordinal));
    }

    private static IReadOnlyList<DockerEnvironmentVariable> ParseEnvironment(JsonElement config)
    {
        if (!config.TryGetProperty("Env", out var values) || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Docker Config.Env is not an array.");
        }

        var result = new List<DockerEnvironmentVariable>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Docker Config.Env contains a non-string value.");
            }

            var raw = item.GetString() ?? string.Empty;
            var separator = raw.IndexOf('=');
            var name = DockerDiagnosticsText.Sanitize(separator < 0 ? raw : raw[..separator]);
            var value = separator < 0 ? string.Empty : raw[(separator + 1)..];
            var sensitive = IsSensitiveEnvironmentName(name);
            result.Add(new DockerEnvironmentVariable(
                name,
                sensitive ? "••••••" : DockerDiagnosticsText.Sanitize(value),
                sensitive));
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ParseLabels(JsonElement config)
    {
        if (!config.TryGetProperty("Labels", out var labels) || labels.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (labels.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Docker Config.Labels is not an object.");
        }

        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in labels.EnumerateObject())
        {
            result[DockerDiagnosticsText.Sanitize(property.Name)] = DockerDiagnosticsText.Sanitize(
                property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText());
        }

        return result;
    }

    private static IReadOnlyList<DockerContainerMountInfo> ParseMounts(JsonElement root)
    {
        if (!root.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (mounts.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Docker Mounts is not an array.");
        }

        return mounts.EnumerateArray()
            .Select(mount => new DockerContainerMountInfo(
                Text(mount, "Type"),
                Text(mount, "Source"),
                Text(mount, "Destination"),
                Text(mount, "Mode"),
                Boolean(mount, "RW"),
                Text(mount, "Propagation")))
            .OrderBy(mount => mount.Destination, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DockerContainerNetworkInfo> ParseNetworks(JsonElement networkSettings)
    {
        if (!networkSettings.TryGetProperty("Networks", out var networks) || networks.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (networks.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Docker NetworkSettings.Networks is not an object.");
        }

        return networks.EnumerateObject()
            .Select(network => new DockerContainerNetworkInfo(
                DockerDiagnosticsText.Sanitize(network.Name),
                Text(network.Value, "IPAddress"),
                Text(network.Value, "Gateway"),
                Text(network.Value, "MacAddress")))
            .OrderBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> StringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"Docker property '{property}' is not an array.");
        }

        return values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? DockerDiagnosticsText.Sanitize(value.GetString())
                : DockerDiagnosticsText.Sanitize(value.GetRawText()))
            .ToArray();
    }

    private static void AddLogLines(
        ICollection<DockerContainerLogEntry> target,
        string output,
        DockerLogStream stream)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                continue;
            }

            var separator = rawLine.IndexOf(' ');
            var token = separator > 0 ? rawLine[..separator] : string.Empty;
            DateTimeOffset? timestamp = null;
            if (token.Length > 0 && DateTimeOffset.TryParse(
                    token,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                token = string.Empty;
            }

            var message = token.Length > 0 && separator >= 0
                ? rawLine[(separator + 1)..]
                : rawLine;
            target.Add(new DockerContainerLogEntry(
                timestamp,
                token,
                stream,
                DockerDiagnosticsText.Sanitize(message)));
        }
    }

    private static (long? Left, long? Right) ParseBytePair(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (ParseBytes(parts[0]), ParseBytes(parts[1]))
            : (ParseBytes(parts[0]), null);
    }

    private static long? ParseBytes(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text == "--")
        {
            return null;
        }

        var index = 0;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or ','))
        {
            index++;
        }

        if (index == 0 || !double.TryParse(
                text[..index].Replace(',', '.', StringComparison.Ordinal),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) || number < 0)
        {
            return null;
        }

        var unit = text[index..].Trim();
        var multiplier = unit switch
        {
            "B" or "" => 1d,
            "kB" or "KB" => 1_000d,
            "MB" => 1_000_000d,
            "GB" => 1_000_000_000d,
            "TB" => 1_000_000_000_000d,
            "KiB" => 1_024d,
            "MiB" => 1_048_576d,
            "GiB" => 1_073_741_824d,
            "TiB" => 1_099_511_627_776d,
            _ => double.NaN,
        };
        if (double.IsNaN(multiplier))
        {
            return null;
        }

        var bytes = number * multiplier;
        return bytes > long.MaxValue ? null : (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
    }

    private static double? ParsePercent(string value)
    {
        var text = value.Trim().TrimEnd('%');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : null;
    }

    private static int? ParsePositiveInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : null;

    private static JsonElement Object(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Docker inspect property '{property}' is missing or not an object.");
        }

        return value;
    }

    private static JsonElement? ObjectOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FormatException($"Docker inspect property '{property}' is not an object.");
    }

    private static string RequiredText(JsonElement element, string property, string resource)
    {
        var value = Text(element, property);
        return value.Length > 0
            ? value
            : throw new FormatException($"Docker {resource} is missing '{property}'.");
    }

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
        return DockerDiagnosticsText.Sanitize(text);
    }

    private static int? Integer(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer))
        {
            return integer;
        }

        return int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
            ? integer
            : null;
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

public sealed class DockerLogRetentionBuffer
{
    private readonly int _maxRows;
    private readonly List<DockerContainerLogEntry> _entries = [];

    public DockerLogRetentionBuffer(int maxRows)
    {
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        }

        _maxRows = maxRows;
    }

    public IReadOnlyList<DockerContainerLogEntry> Entries => _entries;

    public void Reset(IEnumerable<DockerContainerLogEntry> entries)
    {
        _entries.Clear();
        AddRange(entries);
    }

    public void AddRange(IEnumerable<DockerContainerLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var existing = new HashSet<string>(_entries.Select(Key), StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (existing.Add(Key(entry)))
            {
                _entries.Add(entry);
            }
        }

        var overflow = _entries.Count - _maxRows;
        if (overflow > 0)
        {
            _entries.RemoveRange(0, overflow);
        }
    }

    public void Clear() => _entries.Clear();

    private static string Key(DockerContainerLogEntry entry) =>
        $"{entry.TimestampToken}\u001f{entry.Stream}\u001f{entry.Message}";
}

public static class DockerContainerLogProjection
{
    public static IReadOnlyList<DockerContainerLogEntry> Filter(
        IEnumerable<DockerContainerLogEntry> entries,
        string search,
        DockerLogStream? stream)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = search.Trim();
        return entries.Where(entry =>
                (stream is null || entry.Stream == stream) &&
                (text.Length == 0 || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
