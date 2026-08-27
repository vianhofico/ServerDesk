using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Remote;

public sealed record RemoteCommandSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    OperationRisk Risk = OperationRisk.ReadOnly,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null)
{
    public static RemoteCommandSpec ReadOnly(
        string executable,
        params string[] arguments) =>
        new(executable, arguments, TimeSpan.FromSeconds(15));
}

public sealed record RemoteCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

public sealed record RemoteExecutionResult(
    RemoteCommandResult? Command,
    RemoteError? Error)
{
    public bool IsSuccess => Command is not null && Error is null;

    public static RemoteExecutionResult Success(RemoteCommandResult command) => new(command, null);

    public static RemoteExecutionResult Failure(RemoteError error) => new(null, error);
}

public interface IRemoteCommandExecutor : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    Task<RemoteExecutionResult> ExecuteAsync(
        RemoteCommandSpec command,
        CancellationToken cancellationToken = default);
}

public interface IRemoteCommandExecutorFactory
{
    IRemoteCommandExecutor Create(ServerProfile profile);
}
