using System.Globalization;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Deployment;

public sealed class DeploymentHealthCheckRunner : IDeploymentHealthCheckRunner
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;
    private readonly IServerServiceManager _serviceManager;
    private readonly IDockerInventoryService _dockerInventory;
    private readonly DeploymentOptions _options;

    public DeploymentHealthCheckRunner(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        IServerServiceManager serviceManager,
        IDockerInventoryService dockerInventory,
        DeploymentOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
        _dockerInventory = dockerInventory ?? throw new ArgumentNullException(nameof(dockerInventory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DeploymentHealthCheckResult> RunAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = DeploymentTargetPolicy.NormalizeHealthCheck(check);
        DeploymentHealthCheckResult? last = null;

        for (var attempt = 1; attempt <= _options.HealthAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await RunOnceAsync(profile, normalized, cancellationToken).ConfigureAwait(false);
            if (last.IsSuccess || attempt == _options.HealthAttempts || !ShouldRetry(last.Error))
            {
                return last;
            }

            if (_options.HealthRetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.HealthRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return last ?? Failure(normalized.Name, RemoteErrorCode.CommandFailed, "Health verification did not produce a result.");
    }

    private Task<DeploymentHealthCheckResult> RunOnceAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        CancellationToken cancellationToken) =>
        check.Kind switch
        {
            DeploymentHealthCheckKind.Http => RunCommandAsync(
                profile,
                check,
                "curl",
                [
                    "--fail",
                    "--silent",
                    "--show-error",
                    "--location",
                    "--max-time",
                    Math.Max(1, (int)Math.Ceiling(_options.HealthCommandTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                    "--output",
                    "/dev/null",
                    "--",
                    check.Target,
                ],
                cancellationToken),
            DeploymentHealthCheckKind.Tcp => RunCommandAsync(
                profile,
                check,
                "nc",
                [
                    "-z",
                    "-w",
                    Math.Max(1, (int)Math.Ceiling(_options.HealthCommandTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                    check.Target,
                    check.Port!.Value.ToString(CultureInfo.InvariantCulture),
                ],
                cancellationToken),
            DeploymentHealthCheckKind.Process => RunCommandAsync(
                profile,
                check,
                "ps",
                ["-p", check.Target, "-o", "pid="],
                cancellationToken,
                requireOutput: true),
            DeploymentHealthCheckKind.SystemdService => RunServiceAsync(profile, check, cancellationToken),
            DeploymentHealthCheckKind.DockerContainer => RunContainerAsync(profile, check, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };

    private async Task<DeploymentHealthCheckResult> RunCommandAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool requireOutput = false)
    {
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    executable,
                    arguments,
                    _options.HealthCommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new DeploymentHealthCheckResult(false, $"Health check '{check.Name}' could not complete.", execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0 || requireOutput && string.IsNullOrWhiteSpace(command.StandardOutput))
        {
            return Failure(check.Name, RemoteErrorCode.CommandFailed, $"Health check '{check.Name}' did not reach its required state.");
        }

        return new DeploymentHealthCheckResult(true, $"Health check '{check.Name}' passed.");
    }

    private async Task<DeploymentHealthCheckResult> RunServiceAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        CancellationToken cancellationToken)
    {
        var result = await _serviceManager.GetAsync(profile, check.Target, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new DeploymentHealthCheckResult(false, $"Health check '{check.Name}' could not inspect its service.", result.Error);
        }

        var service = result.Services.Count == 1 ? result.Services[0] : null;
        return service?.IsActive == true
            ? new DeploymentHealthCheckResult(true, $"Health check '{check.Name}' passed.")
            : Failure(check.Name, RemoteErrorCode.CommandFailed, $"Health check '{check.Name}' found the service outside the required active state.");
    }

    private async Task<DeploymentHealthCheckResult> RunContainerAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        CancellationToken cancellationToken)
    {
        var result = await _dockerInventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Snapshot is null)
        {
            return new DeploymentHealthCheckResult(false, $"Health check '{check.Name}' could not inspect Docker state.", result.Error);
        }

        var container = result.Snapshot.Containers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, check.Target, StringComparison.Ordinal) ||
            string.Equals(candidate.Id, check.Target, StringComparison.Ordinal));
        if (container is null)
        {
            return Failure(check.Name, RemoteErrorCode.PathNotFound, $"Health check '{check.Name}' could not find the configured container identity.");
        }

        return string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase)
            ? new DeploymentHealthCheckResult(true, $"Health check '{check.Name}' passed.")
            : Failure(check.Name, RemoteErrorCode.CommandFailed, $"Health check '{check.Name}' found the container outside the required running state.");
    }

    private static bool ShouldRetry(RemoteError? error) =>
        error?.Code is RemoteErrorCode.CommandFailed or
            RemoteErrorCode.PathNotFound or
            RemoteErrorCode.ConnectionFailed or
            RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout;

    private static DeploymentHealthCheckResult Failure(string name, RemoteErrorCode code, string message)
    {
        _ = name;
        var error = new RemoteError(code, message);
        return new DeploymentHealthCheckResult(false, message, error);
    }
}
