using System.Globalization;
using System.Text;
using System.Text.Json;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Logs;

public enum ServerLogSource
{
    Journal,
    File,
}

public enum LogSeverity
{
    Emergency,
    Alert,
    Critical,
    Error,
    Warning,
    Notice,
    Info,
    Debug,
    Unknown,
}

public sealed record LogEntry(
    DateTimeOffset? Timestamp,
    string? Cursor,
    LogSeverity Severity,
    string Message,
    string Identifier,
    string SystemdUnit,
    int? ProcessId,
    string Hostname,
    ServerLogSource Source);

public sealed record ServerLogReadResult(
    IReadOnlyList<LogEntry> Entries,
    RemoteError? Error,
    string? LastCursor = null)
{
    public bool IsSuccess => Error is null;
}

public sealed record ServerLogFilter(
    string Text = "",
    LogSeverity? Severity = null,
    string Identifier = "",
    string SystemdUnit = "",
    ServerLogSource? Source = null);

public sealed record ServerLogOptions(
    string JournalExecutable,
    string TailExecutable,
    int DefaultRecentRows,
    int MaxRetainedRows,
    TimeSpan FollowPollInterval,
    TimeSpan CommandTimeout)
{
    public static ServerLogOptions Default { get; } = new(
        "journalctl",
        "tail",
        200,
        5_000,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(20));
}

public interface IServerLogService
{
    ServerLogOptions Options { get; }

    Task<ServerLogReadResult> ReadJournalAsync(
        ServerProfile profile,
        int? count = null,
        string? unit = null,
        CancellationToken cancellationToken = default);

    Task<ServerLogReadResult> ReadJournalAfterCursorAsync(
        ServerProfile profile,
        string cursor,
        int? count = null,
        string? unit = null,
        CancellationToken cancellationToken = default);

    Task<ServerLogReadResult> ReadFileTailAsync(
        ServerProfile profile,
        string path,
        int? count = null,
        CancellationToken cancellationToken = default);
}

public sealed class ServerLogService : IServerLogService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;

    public ServerLogService(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        ServerLogOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public ServerLogOptions Options { get; }

    public Task<ServerLogReadResult> ReadJournalAsync(
        ServerProfile profile,
        int? count = null,
        string? unit = null,
        CancellationToken cancellationToken = default) =>
        ReadJournalCoreAsync(profile, null, count, unit, cancellationToken);

    public Task<ServerLogReadResult> ReadJournalAfterCursorAsync(
        ServerProfile profile,
        string cursor,
        int? count = null,
        string? unit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        if (cursor.Contains('\0') || cursor.Length > 8_192)
        {
            throw new ArgumentException("Journal cursor is invalid or unreasonably long.", nameof(cursor));
        }

        return ReadJournalCoreAsync(profile, cursor, count, unit, cancellationToken);
    }

    public async Task<ServerLogReadResult> ReadFileTailAsync(
        ServerProfile profile,
        string path,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalizedPath = RemotePath.Parse(path);
        if (!normalizedPath.IsAbsolute)
        {
            throw new ArgumentException("Log file path must be an absolute remote path.", nameof(path));
        }

        var requestedRows = NormalizeCount(count);
        var command = new RemoteCommandSpec(
            Options.TailExecutable,
            ["-n", requestedRows.ToString(CultureInfo.InvariantCulture), "--", normalizedPath.Value],
            Options.CommandTimeout,
            OperationRisk.ReadOnly,
            StableEnvironment);

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        var failure = ReadFailure(execution, $"Unable to read log file '{normalizedPath.Value}'.");
        if (failure is not null)
        {
            return new ServerLogReadResult([], failure);
        }

        return new ServerLogReadResult(
            LinuxLogParser.ParseFileTail(execution.Command!.StandardOutput),
            null);
    }

    private async Task<ServerLogReadResult> ReadJournalCoreAsync(
        ServerProfile profile,
        string? afterCursor,
        int? count,
        string? unit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var requestedRows = NormalizeCount(count);
        if (!string.IsNullOrWhiteSpace(unit))
        {
            SystemdServiceManager.ValidateUnitName(unit);
        }

        var arguments = new List<string>
        {
            "--no-pager",
            "--output=json",
            "--utc",
            "--lines",
            requestedRows.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(afterCursor))
        {
            arguments.Add("--after-cursor");
            arguments.Add(afterCursor);
        }

        if (!string.IsNullOrWhiteSpace(unit))
        {
            arguments.Add("--unit");
            arguments.Add(unit);
        }

        var command = new RemoteCommandSpec(
            Options.JournalExecutable,
            arguments,
            Options.CommandTimeout,
            OperationRisk.ReadOnly,
            StableEnvironment);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        var failure = ReadFailure(execution, "Unable to read the system journal.");
        if (failure is not null)
        {
            return new ServerLogReadResult([], failure);
        }

        try
        {
            var entries = LinuxLogParser.ParseJournalJsonLines(execution.Command!.StandardOutput);
            return new ServerLogReadResult(entries, null, entries.LastOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Cursor))?.Cursor);
        }
        catch (FormatException exception)
        {
            return new ServerLogReadResult(
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse structured journal output.",
                    exception.Message));
        }
    }

    private int NormalizeCount(int? count)
    {
        var requested = count ?? Options.DefaultRecentRows;
        if (requested <= 0 || requested > Options.MaxRetainedRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"Log row count must be between 1 and {Options.MaxRetainedRows:N0}.");
        }

        return requested;
    }

    private static RemoteError? ReadFailure(RemoteExecutionResult execution, string fallback)
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
        var code = detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.PermissionDenied
            : detail.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("cannot open", StringComparison.OrdinalIgnoreCase) &&
              detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.PathNotFound
                : detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                  detail.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.CommandNotFound
                    : detail.Contains("not been booted with systemd", StringComparison.OrdinalIgnoreCase) ||
                      detail.Contains("failed to connect to bus", StringComparison.OrdinalIgnoreCase) ||
                      detail.Contains("journal has been rotated", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.CapabilityUnavailable
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

    private static void ValidateOptions(ServerLogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.JournalExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TailExecutable);
        if (options.DefaultRecentRows <= 0 || options.MaxRetainedRows < options.DefaultRecentRows)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Log retention options are inconsistent.");
        }

        if (options.FollowPollInterval <= TimeSpan.Zero || options.CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Log timing options must be positive.");
        }
    }
}

public static class LinuxLogParser
{
    public static IReadOnlyList<LogEntry> ParseJournalJsonLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var entries = new List<LogEntry>();
        using var reader = new StringReader(output);
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
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"Journal line {lineNumber} is not a JSON object.");
                }

                var root = document.RootElement;
                entries.Add(new LogEntry(
                    ParseTimestamp(Field(root, "__REALTIME_TIMESTAMP"), lineNumber),
                    EmptyToNull(Field(root, "__CURSOR")),
                    ParseSeverity(Field(root, "PRIORITY")),
                    SanitizeDisplay(Field(root, "MESSAGE")),
                    SanitizeDisplay(Field(root, "SYSLOG_IDENTIFIER")),
                    SanitizeDisplay(Field(root, "_SYSTEMD_UNIT")),
                    ParseProcessId(Field(root, "_PID")),
                    SanitizeDisplay(Field(root, "_HOSTNAME")),
                    ServerLogSource.Journal));
            }
            catch (JsonException exception)
            {
                throw new FormatException($"Malformed journal JSON on line {lineNumber}: {exception.Message}", exception);
            }
        }

        return entries;
    }

    public static IReadOnlyList<LogEntry> ParseFileTail(string output)
    {
        if (output.Length == 0)
        {
            return [];
        }

        var entries = new List<LogEntry>();
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            entries.Add(new LogEntry(
                null,
                null,
                LogSeverity.Unknown,
                SanitizeDisplay(line),
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                ServerLogSource.File));
        }

        return entries;
    }

    public static string SanitizeDisplay(string? value)
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

    private static string Field(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
    }

    private static DateTimeOffset? ParseTimestamp(string raw, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds) || microseconds < 0)
        {
            throw new FormatException($"Journal line {lineNumber} contains invalid __REALTIME_TIMESTAMP '{raw}'.");
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddTicks(checked(microseconds * 10));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException($"Journal line {lineNumber} contains out-of-range timestamp '{raw}'.", exception);
        }
        catch (OverflowException exception)
        {
            throw new FormatException($"Journal line {lineNumber} contains out-of-range timestamp '{raw}'.", exception);
        }
    }

    private static int? ParseProcessId(string raw)
    {
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    private static LogSeverity ParseSeverity(string raw) => raw switch
    {
        "0" => LogSeverity.Emergency,
        "1" => LogSeverity.Alert,
        "2" => LogSeverity.Critical,
        "3" => LogSeverity.Error,
        "4" => LogSeverity.Warning,
        "5" => LogSeverity.Notice,
        "6" => LogSeverity.Info,
        "7" => LogSeverity.Debug,
        _ => LogSeverity.Unknown,
    };

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class LogRetentionBuffer
{
    private readonly int _maxRows;
    private readonly List<LogEntry> _entries = [];

    public LogRetentionBuffer(int maxRows)
    {
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        }

        _maxRows = maxRows;
    }

    public IReadOnlyList<LogEntry> Entries => _entries;

    public void AddRange(IEnumerable<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var incoming = entries as IReadOnlyCollection<LogEntry> ?? entries.ToArray();
        if (incoming.Count >= _maxRows)
        {
            _entries.Clear();
            _entries.AddRange(incoming.TakeLast(_maxRows));
            return;
        }

        _entries.AddRange(incoming);
        var overflow = _entries.Count - _maxRows;
        if (overflow > 0)
        {
            _entries.RemoveRange(0, overflow);
        }
    }

    public void Reset(IEnumerable<LogEntry> entries)
    {
        _entries.Clear();
        AddRange(entries);
    }

    public void Clear() => _entries.Clear();
}

public static class ServerLogProjection
{
    public static IReadOnlyList<LogEntry> Filter(
        IEnumerable<LogEntry> entries,
        ServerLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(filter);
        var text = filter.Text.Trim();
        var identifier = filter.Identifier.Trim();
        var unit = filter.SystemdUnit.Trim();

        return entries.Where(entry =>
                (filter.Source is null || entry.Source == filter.Source) &&
                (filter.Severity is null || entry.Severity == filter.Severity) &&
                (identifier.Length == 0 || entry.Identifier.Contains(identifier, StringComparison.OrdinalIgnoreCase)) &&
                (unit.Length == 0 || entry.SystemdUnit.Contains(unit, StringComparison.OrdinalIgnoreCase)) &&
                (text.Length == 0 || SearchableText(entry).Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string SearchableText(LogEntry entry) =>
        $"{entry.Message} {entry.Identifier} {entry.SystemdUnit} {entry.Hostname} {entry.ProcessId?.ToString(CultureInfo.InvariantCulture)} {entry.Source} {entry.Severity}";
}
