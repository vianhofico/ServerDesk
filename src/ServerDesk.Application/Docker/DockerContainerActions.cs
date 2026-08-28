using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public enum DockerContainerAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Unpause,
    Kill,
    Remove,
}

public sealed record DockerContainerActionResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    DockerContainerDetails? VerifiedDetails = null);

public interface IDockerContainerActionService
{
    Task<DockerContainerActionResult> ExecuteAsync(
        ServerProfile profile,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken = default);
}

public sealed record DockerContainerActionOptions(TimeSpan CommandTimeout)
{
    public static DockerContainerActionOptions Default { get; } = new(TimeSpan.FromSeconds(30));

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
    }
}

public sealed class DockerContainerActionService : IDockerContainerActionService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly IDockerContainerDiagnosticsService _diagnostics;
    private readonly DockerContainerActionOptions _options;

    public DockerContainerActionService(
        IRemoteCommandExecutorFactory executorFactory,
        IDockerContainerDiagnosticsService diagnostics,
        DockerContainerActionOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DockerContainerActionResult> ExecuteAsync(
        ServerProfile profile,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);

        var beforeResult = await _diagnostics.InspectAsync(profile, id, cancellationToken).ConfigureAwait(false);
        if (!beforeResult.IsSuccess || beforeResult.Details is null)
        {
            var error = beforeResult.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "ServerDesk could not read the container before changing it.");
            return new DockerContainerActionResult(false, error, error.Message);
        }

        var before = beforeResult.Details;
        var precondition = CheckPrecondition(before, action);
        if (precondition is not null)
        {
            return precondition;
        }

        if (IsNoOp(before, action))
        {
            return new DockerContainerActionResult(
                true,
                null,
                $"Container '{before.Name}' already matches the requested {Verb(action)} state; no Docker mutation was sent.",
                before);
        }

        var arguments = BuildArguments(action, id);
        await using var executor = _executorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "docker",
                    arguments,
                    _options.CommandTimeout,
                    Risk(action),
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);

        if (execution.Error is not null)
        {
            var error = IsAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"ServerDesk lost a reliable completion signal while requesting Docker {Verb(action)} for '{before.Name}'. Refresh the container state before deciding whether to retry.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new DockerContainerActionResult(false, error, error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(
                command.StandardError,
                command.StandardOutput,
                $"Docker {Verb(action)} failed for '{before.Name}'.");
            var error = new RemoteError(ClassifyFailure(detail), detail);
            return new DockerContainerActionResult(false, error, detail);
        }

        return await VerifyAsync(profile, before, action, cancellationToken).ConfigureAwait(false);
    }

    public static OperationRisk Risk(DockerContainerAction action) =>
        action is DockerContainerAction.Stop or DockerContainerAction.Restart or DockerContainerAction.Kill or DockerContainerAction.Remove
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;

    public static string Verb(DockerContainerAction action) =>
        action switch
        {
            DockerContainerAction.Start => "start",
            DockerContainerAction.Stop => "stop",
            DockerContainerAction.Restart => "restart",
            DockerContainerAction.Pause => "pause",
            DockerContainerAction.Unpause => "unpause",
            DockerContainerAction.Kill => "kill",
            DockerContainerAction.Remove => "remove",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private async Task<DockerContainerActionResult> VerifyAsync(
        ServerProfile profile,
        DockerContainerDetails before,
        DockerContainerAction action,
        CancellationToken cancellationToken)
    {
        var verification = await _diagnostics.InspectAsync(profile, before.Id, cancellationToken).ConfigureAwait(false);
        if (action == DockerContainerAction.Remove)
        {
            if (!verification.IsSuccess && verification.Error?.Code == RemoteErrorCode.PathNotFound)
            {
                return new DockerContainerActionResult(
                    true,
                    null,
                    $"Docker remove completed and container '{before.Name}' is no longer present.");
            }

            var detail = verification.Error?.Message ?? "The container is still present after Docker reported successful removal.";
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Docker remove returned success for '{before.Name}', but ServerDesk could not verify removal. {detail} Refresh before retrying.");
            return new DockerContainerActionResult(false, error, error.Message, verification.Details);
        }

        if (!verification.IsSuccess || verification.Details is null)
        {
            var detail = verification.Error?.Message ?? "The container could not be re-read after mutation.";
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Docker {Verb(action)} returned success for '{before.Name}', but ServerDesk could not verify the resulting state. {detail} Refresh before retrying.");
            return new DockerContainerActionResult(false, error, error.Message);
        }

        var after = verification.Details;
        if (!MatchesExpectedState(before, after, action))
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Docker {Verb(action)} returned success for '{before.Name}', but verified state is status={after.State.Status}, running={after.State.Running}, paused={after.State.Paused}. Refresh before retrying.");
            return new DockerContainerActionResult(false, error, error.Message, after);
        }

        return new DockerContainerActionResult(
            true,
            null,
            $"Docker {Verb(action)} completed and verified for '{before.Name}'.",
            after);
    }

    private static DockerContainerActionResult? CheckPrecondition(
        DockerContainerDetails details,
        DockerContainerAction action)
    {
        if (action == DockerContainerAction.Restart && !details.State.Running)
        {
            return Conflict(details, "restart", "The container is not running. Use Start instead of treating restart as an implicit start.");
        }

        if (action == DockerContainerAction.Kill && !details.State.Running)
        {
            return Conflict(details, "kill", "The container is not running, so SIGKILL would have no valid running target.");
        }

        if (action == DockerContainerAction.Pause && !details.State.Running)
        {
            return Conflict(details, "pause", "Only a running container can be paused.");
        }

        if (action == DockerContainerAction.Unpause && !details.State.Paused)
        {
            return details.State.Running
                ? new DockerContainerActionResult(true, null, $"Container '{details.Name}' is already unpaused; no Docker mutation was sent.", details)
                : Conflict(details, "unpause", "The container is not paused/running.");
        }

        if (action == DockerContainerAction.Remove && details.State.Running)
        {
            return Conflict(
                details,
                "remove",
                "ServerDesk does not force-remove running containers. Stop the container first, verify it stopped, then remove it explicitly.");
        }

        return null;
    }

    private static DockerContainerActionResult Conflict(DockerContainerDetails details, string verb, string reason)
    {
        var error = new RemoteError(RemoteErrorCode.PathConflict, $"Cannot {verb} container '{details.Name}'. {reason}");
        return new DockerContainerActionResult(false, error, error.Message, details);
    }

    private static bool IsNoOp(DockerContainerDetails details, DockerContainerAction action) =>
        action switch
        {
            DockerContainerAction.Start => details.State.Running && !details.State.Paused,
            DockerContainerAction.Stop => !details.State.Running,
            DockerContainerAction.Pause => details.State.Paused,
            _ => false,
        };

    private static IReadOnlyList<string> BuildArguments(DockerContainerAction action, string id)
    {
        var verb = action == DockerContainerAction.Remove ? "rm" : Verb(action);
        return action == DockerContainerAction.Kill
            ? ["container", verb, "--signal", "KILL", "--", id]
            : ["container", verb, "--", id];
    }

    private static bool MatchesExpectedState(
        DockerContainerDetails before,
        DockerContainerDetails after,
        DockerContainerAction action) =>
        action switch
        {
            DockerContainerAction.Start => after.State.Running && !after.State.Paused,
            DockerContainerAction.Stop or DockerContainerAction.Kill => !after.State.Running,
            DockerContainerAction.Restart => after.State.Running &&
                !string.Equals(before.State.StartedAt, after.State.StartedAt, StringComparison.Ordinal),
            DockerContainerAction.Pause => after.State.Running && after.State.Paused,
            DockerContainerAction.Unpause => after.State.Running && !after.State.Paused,
            _ => false,
        };

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("no such container", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("container not found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("is running", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("is not running", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("already paused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not paused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathConflict;
        }

        if (detail.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.CapabilityUnavailable;
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

public sealed class AuditedDockerContainerActionService : IDockerContainerActionService
{
    private readonly IDockerContainerActionService _inner;
    private readonly IOperationAudit _audit;

    public AuditedDockerContainerActionService(IDockerContainerActionService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<DockerContainerActionResult> ExecuteAsync(
        ServerProfile profile,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);
        var risk = DockerContainerActionService.Risk(action);
        try
        {
            var result = await _inner.ExecuteAsync(profile, id, action, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(profile, id, action, risk, outcome, cancellationToken).ConfigureAwait(false);
            return persisted
                ? result
                : result with
                {
                    Message = result.Message + " Audit record could not be persisted; do not retry the Docker action solely for that reason.",
                };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(
                    profile,
                    id,
                    action,
                    risk,
                    OperationOutcome.Cancelled,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        string containerId,
        DockerContainerAction action,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} container:{containerId}";
            var entry = OperationAuditEntry.Create(
                "docker-container-action",
                $"Docker {DockerContainerActionService.Verb(action)} requested for container {containerId}",
                risk,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public interface IDockerExecTerminalSessionFactory
{
    IRemoteTerminalSession Create(ServerProfile profile, string containerId);
}

public sealed class DockerExecTerminalSessionFactory : IDockerExecTerminalSessionFactory
{
    private readonly IRemoteTerminalSessionFactory _inner;

    public DockerExecTerminalSessionFactory(IRemoteTerminalSessionFactory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IRemoteTerminalSession Create(ServerProfile profile, string containerId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = DockerContainerIdentifier.Normalize(containerId);
        return new DockerExecTerminalSession(_inner.Create(profile), id);
    }
}

internal sealed class DockerExecTerminalSession : IRemoteTerminalSession
{
    private readonly IRemoteTerminalSession _inner;
    private readonly string _containerId;

    public DockerExecTerminalSession(IRemoteTerminalSession inner, string containerId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _containerId = DockerContainerIdentifier.Normalize(containerId);
    }

    public Guid ServerProfileId => _inner.ServerProfileId;
    public TerminalSessionState State => _inner.State;
    public RemoteError? LastError => _inner.LastError;

    public event Action<TerminalSessionState>? StateChanged
    {
        add => _inner.StateChanged += value;
        remove => _inner.StateChanged -= value;
    }

    public event Action<string>? OutputReceived
    {
        add => _inner.OutputReceived += value;
        remove => _inner.OutputReceived -= value;
    }

    public async ValueTask ConnectAsync(
        TerminalSize initialSize,
        CancellationToken cancellationToken = default)
    {
        await _inner.ConnectAsync(initialSize, cancellationToken).ConfigureAwait(false);
        await _inner.SendAsync(
                $"docker exec -it -- {_containerId} /bin/sh\r",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask SendAsync(string input, CancellationToken cancellationToken = default) =>
        _inner.SendAsync(input, cancellationToken);

    public ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken = default) =>
        _inner.ResizeAsync(size, cancellationToken);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
        _inner.DisconnectAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
