using System.Globalization;
using System.Text.RegularExpressions;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Processes;

public sealed record ServerProcessInfo(
    int ProcessId,
    int ParentProcessId,
    string User,
    string State,
    double CpuPercent,
    long ResidentBytes,
    long ElapsedSeconds,
    string Command,
    string Arguments)
{
    public TimeSpan Elapsed => TimeSpan.FromSeconds(ElapsedSeconds);
}

public sealed record ServerProcessQueryResult(
    IReadOnlyList<ServerProcessInfo> Processes,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public enum ServerProcessSignal
{
    Terminate,
    ForceKill,
}

public sealed record ServerProcessActionResult(bool IsSuccess, RemoteError? Error, string Message);

public interface IServerProcessService
{
    Task<ServerProcessQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<ServerProcessQueryResult> GetAsync(
        ServerProfile profile,
        int processId,
        CancellationToken cancellationToken = default);

    Task<ServerProcessActionResult> SignalAsync(
        ServerProfile profile,
        int processId,
        ServerProcessSignal signal,
        CancellationToken cancellationToken = default);
}

public sealed class ServerProcessService : IServerProcessService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;

    public ServerProcessService(IRemoteCommandExecutorFactory commandExecutorFactory)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
    }

    public async Task<ServerProcessQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);
        var result = await executor.ExecuteAsync(
                BuildPsCommand(processId: null),
                cancellationToken)
            .ConfigureAwait(false);
        return ToQueryResult(result);
    }

    public async Task<ServerProcessQueryResult> GetAsync(
        ServerProfile profile,
        int processId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProcessId(processId);
        await using var executor = _commandExecutorFactory.Create(profile);
        var result = await executor.ExecuteAsync(BuildPsCommand(processId), cancellationToken).ConfigureAwait(false);
        return ToQueryResult(result);
    }

    public async Task<ServerProcessActionResult> SignalAsync(
        ServerProfile profile,
        int processId,
        ServerProcessSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProcessId(processId);
        var signalName = signal switch
        {
            ServerProcessSignal.Terminate => "-TERM",
            ServerProcessSignal.ForceKill => "-KILL",
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };
        var risk = signal == ServerProcessSignal.ForceKill
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "kill",
                    [signalName, "--", processId.ToString(CultureInfo.InvariantCulture)],
                    TimeSpan.FromSeconds(10),
                    risk,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);

        if (execution.Error is not null)
        {
            var error = IsAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"ServerDesk lost a reliable completion signal while sending {signalName} to PID {processId}. Refresh the process list before deciding whether to retry.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new ServerProcessActionResult(false, error, error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return new ServerProcessActionResult(
                true,
                null,
                signal == ServerProcessSignal.ForceKill
                    ? $"SIGKILL sent to PID {processId}."
                    : $"SIGTERM sent to PID {processId}.");
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, "Process signal failed.");
        var errorCode = ClassifySignalFailure(detail);
        var remoteError = new RemoteError(errorCode, detail);
        return new ServerProcessActionResult(false, remoteError, detail);
    }

    public static void ValidateProcessId(int processId)
    {
        if (processId <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "ServerDesk will not signal PID 1 or non-positive process identifiers.");
        }
    }

    private static RemoteCommandSpec BuildPsCommand(int? processId)
    {
        var arguments = new List<string>
        {
            "-ww",
            "-o",
            "pid=,ppid=,user=,stat=,pcpu=,rss=,etimes=,comm=,args=",
        };
        if (processId is { } pid)
        {
            arguments.Add("-p");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            arguments.Add("-e");
            arguments.Add("--sort=-pcpu");
        }

        return new RemoteCommandSpec(
            "ps",
            arguments,
            TimeSpan.FromSeconds(15),
            OperationRisk.ReadOnly,
            StableEnvironment);
    }

    private static ServerProcessQueryResult ToQueryResult(RemoteExecutionResult execution)
    {
        if (execution.Error is not null)
        {
            return new ServerProcessQueryResult([], execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, "Unable to read the process list.");
            return new ServerProcessQueryResult(
                [],
                new RemoteError(
                    detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.CommandNotFound
                        : RemoteErrorCode.CommandFailed,
                    detail));
        }

        try
        {
            return new ServerProcessQueryResult(ServerProcessParser.Parse(command.StandardOutput), null);
        }
        catch (FormatException exception)
        {
            return new ServerProcessQueryResult(
                [],
                new RemoteError(RemoteErrorCode.ParseFailed, "ServerDesk could not parse the process list.", exception.Message));
        }
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled;

    private static RemoteErrorCode ClassifySignalFailure(string detail)
    {
        if (detail.Contains("No such process", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        return RemoteErrorCode.CommandFailed;
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

public static partial class ServerProcessParser
{
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\S+)\s+(\S+)\s+([0-9]+(?:\.[0-9]+)?)\s+(\d+)\s+(\d+)\s+(\S+)(?:\s+(.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessLineRegex();

    public static IReadOnlyList<ServerProcessInfo> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var processes = new List<ServerProcessInfo>();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var match = ProcessLineRegex().Match(rawLine);
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ppid) ||
                !double.TryParse(match.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu) ||
                !long.TryParse(match.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rssKib) ||
                !long.TryParse(match.Groups[7].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var elapsed))
            {
                throw new FormatException($"Malformed ps process row: '{rawLine}'.");
            }

            processes.Add(new ServerProcessInfo(
                pid,
                ppid,
                match.Groups[3].Value,
                match.Groups[4].Value,
                cpu,
                checked(rssKib * 1024),
                elapsed,
                match.Groups[8].Value,
                match.Groups[9].Success ? match.Groups[9].Value : string.Empty));
        }

        return processes;
    }
}
