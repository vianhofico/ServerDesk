using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public sealed class DockerComposeService : IDockerComposeService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly DockerComposeOptions _options;

    public DockerComposeService(IRemoteCommandExecutorFactory executorFactory, DockerComposeOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DockerComposeSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _executorFactory.Create(profile);

        var versionExecution = await ExecuteAsync(
                executor,
                ["compose", "version", "--short"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (versionExecution.Error is not null)
        {
            return new DockerComposeSnapshotResult(
                new DockerComposeSnapshot(MapRuntime(versionExecution.Error), Array.Empty<DockerComposeProject>()),
                null);
        }

        var versionCommand = versionExecution.Command!;
        if (versionCommand.ExitCode != 0)
        {
            var detail = FirstUseful(versionCommand.StandardError, versionCommand.StandardOutput, "Docker Compose v2 is unavailable.");
            return new DockerComposeSnapshotResult(
                new DockerComposeSnapshot(MapRuntime(detail), Array.Empty<DockerComposeProject>()),
                null);
        }

        var version = DockerComposeParser.Sanitize(versionCommand.StandardOutput.Trim());
        var listExecution = await ExecuteAsync(
                executor,
                ["compose", "ls", "--all", "--format", "json"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (listExecution.Error is not null)
        {
            return new DockerComposeSnapshotResult(
                new DockerComposeSnapshot(MapRuntime(listExecution.Error, version), Array.Empty<DockerComposeProject>()),
                null);
        }

        var listCommand = listExecution.Command!;
        if (listCommand.ExitCode != 0)
        {
            var detail = FirstUseful(listCommand.StandardError, listCommand.StandardOutput, "Docker Compose could not list projects.");
            return new DockerComposeSnapshotResult(
                new DockerComposeSnapshot(MapRuntime(detail, version), Array.Empty<DockerComposeProject>()),
                null);
        }

        try
        {
            var projects = DockerComposeParser.ParseProjects(listCommand.StandardOutput);
            return new DockerComposeSnapshotResult(
                new DockerComposeSnapshot(
                    new DockerComposeRuntimeState(DockerComposeRuntimeStatus.Available, version, "Docker Compose v2 is usable through the existing SSH command channel."),
                    projects),
                null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return new DockerComposeSnapshotResult(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "Docker Compose returned malformed structured project output.", exception.Message));
        }
    }

    public async Task<DockerComposeProjectResult> InspectProjectAsync(
        ServerProfile profile,
        DockerComposeProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = DockerComposeIdentity.Normalize(project);
        await using var executor = _executorFactory.Create(profile);

        var psExecution = await ExecuteAsync(
                executor,
                BuildProjectArguments(identity, "ps", "--all", "--format", "json"),
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var psFailure = GetFailure(psExecution, "Docker Compose could not inspect project services.");
        if (psFailure is not null)
        {
            return new DockerComposeProjectResult(null, psFailure);
        }

        var configExecution = await ExecuteAsync(
                executor,
                BuildProjectArguments(identity, "config", "--format", "json"),
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var configFailure = GetFailure(configExecution, "Docker Compose could not normalize project configuration.");
        if (configFailure is not null)
        {
            return new DockerComposeProjectResult(null, configFailure);
        }

        try
        {
            var services = DockerComposeParser.ParseServices(psExecution.Command!.StandardOutput);
            var config = DockerComposeParser.NormalizeConfigJson(configExecution.Command!.StandardOutput);
            return new DockerComposeProjectResult(new DockerComposeProjectDetails(identity, services, config), null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return new DockerComposeProjectResult(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "Docker Compose returned malformed structured project details.", exception.Message));
        }
    }

    public async Task<DockerComposeLogsResult> ReadLogsAsync(
        ServerProfile profile,
        DockerComposeProject project,
        int tail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = DockerComposeIdentity.Normalize(project);
        if (tail < 1 || tail > _options.MaximumLogRows)
        {
            throw new ArgumentOutOfRangeException(nameof(tail));
        }

        await using var executor = _executorFactory.Create(profile);
        var execution = await ExecuteAsync(
                executor,
                BuildProjectArguments(identity, "logs", "--no-color", "--timestamps", "--tail", tail.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var failure = GetFailure(execution, "Docker Compose could not read project logs.");
        if (failure is not null)
        {
            return new DockerComposeLogsResult(Array.Empty<string>(), failure);
        }

        var combined = string.Join('\n', new[]
        {
            execution.Command!.StandardOutput,
            execution.Command.StandardError,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new DockerComposeLogsResult(DockerComposeParser.ParseLogLines(combined, _options.MaximumLogRows), null);
    }

    public async Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = DockerComposeIdentity.Normalize(project);

        var before = await InspectProjectAsync(profile, identity, cancellationToken).ConfigureAwait(false);
        if (!before.IsSuccess && before.Error?.Code is not RemoteErrorCode.PathNotFound)
        {
            var error = before.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "ServerDesk could not inspect the Compose project before mutation.");
            return new DockerComposeActionResult(false, error, error.Message);
        }

        await using var executor = _executorFactory.Create(profile);
        var arguments = action switch
        {
            DockerComposeAction.Up => BuildProjectArguments(identity, "up", "--detach"),
            DockerComposeAction.Down => BuildProjectArguments(identity, "down"),
            DockerComposeAction.Restart => BuildProjectArguments(identity, "restart"),
            DockerComposeAction.Pull => BuildProjectArguments(identity, "pull"),
            DockerComposeAction.Build => BuildProjectArguments(identity, "build"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var execution = await ExecuteAsync(
                executor,
                arguments,
                DockerComposeProjection.Risk(action),
                _options.MutationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = IsAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"ServerDesk lost a reliable completion signal while running Compose {DockerComposeProjection.Verb(action)} for '{identity.Name}'. Refresh project state before deciding whether to retry.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new DockerComposeActionResult(false, error, error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, $"Docker Compose {DockerComposeProjection.Verb(action)} failed.");
            var error = DockerComposeParser.MapFailure(detail);
            return new DockerComposeActionResult(false, error, error.Message);
        }

        return await VerifyMutationAsync(profile, identity, action, cancellationToken).ConfigureAwait(false);
    }

    public RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project)
    {
        var identity = DockerComposeIdentity.Normalize(project);
        var primary = identity.PrimaryConfigFile;
        var lastSlash = primary.LastIndexOf('/');
        var projectDirectory = lastSlash <= 0 ? "/" : primary[..lastSlash];
        var arguments = new List<string>
        {
            "compose",
            "--project-name",
            identity.Name,
            "--project-directory",
            projectDirectory,
            "--file",
            "{file}",
        };
        foreach (var configFile in identity.ConfigFiles.Skip(1))
        {
            arguments.Add("--file");
            arguments.Add(configFile);
        }

        arguments.Add("config");
        arguments.Add("--quiet");
        return new RemoteEditValidationSpec("docker", arguments);
    }

    private async Task<DockerComposeActionResult> VerifyMutationAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken)
    {
        if (action == DockerComposeAction.Down)
        {
            var snapshot = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsSuccess || snapshot.Snapshot is null)
            {
                return Ambiguous(project, action, snapshot.Error?.Message ?? "Compose project list could not be refreshed.");
            }

            if (snapshot.Snapshot.Projects.All(candidate => !string.Equals(candidate.Name, project.Name, StringComparison.Ordinal)))
            {
                return Success(project, action, null);
            }

            return Ambiguous(project, action, "The project is still present after Compose reported successful down.");
        }

        var verification = await InspectProjectAsync(profile, project, cancellationToken).ConfigureAwait(false);
        if (!verification.IsSuccess || verification.Details is null)
        {
            return Ambiguous(project, action, verification.Error?.Message ?? "Project verification did not return details.");
        }

        if (action is DockerComposeAction.Up or DockerComposeAction.Restart && verification.Details.Services.Count == 0)
        {
            return Ambiguous(project, action, "No Compose service rows were visible after the workload operation.");
        }

        return Success(project, action, verification.Details);
    }

    private static DockerComposeActionResult Success(
        DockerComposeProject project,
        DockerComposeAction action,
        DockerComposeProjectDetails? details) =>
        new(true, null, $"Docker Compose {DockerComposeProjection.Verb(action)} completed and verification succeeded for '{project.Name}'.", details);

    private static DockerComposeActionResult Ambiguous(
        DockerComposeProject project,
        DockerComposeAction action,
        string detail)
    {
        var error = new RemoteError(
            RemoteErrorCode.AmbiguousState,
            $"Docker Compose {DockerComposeProjection.Verb(action)} returned success for '{project.Name}', but ServerDesk could not verify the resulting state. {detail} Refresh before retrying.");
        return new DockerComposeActionResult(false, error, error.Message);
    }

    private static IReadOnlyList<string> BuildProjectArguments(
        DockerComposeProject project,
        params string[] commandArguments)
    {
        var arguments = new List<string> { "compose", "--project-name", project.Name };
        foreach (var configFile in project.ConfigFiles)
        {
            arguments.Add("--file");
            arguments.Add(configFile);
        }

        arguments.AddRange(commandArguments);
        return arguments;
    }

    private static ValueTask<RemoteExecutionResult> ExecuteAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            new RemoteCommandSpec("docker", arguments, timeout, risk, StableEnvironment),
            cancellationToken);

    private static RemoteError? GetFailure(RemoteExecutionResult execution, string fallback)
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

        return DockerComposeParser.MapFailure(FirstUseful(command.StandardError, command.StandardOutput, fallback));
    }

    private static DockerComposeRuntimeState MapRuntime(RemoteError error, string? version = null) =>
        error.Code switch
        {
            RemoteErrorCode.CommandNotFound => new(DockerComposeRuntimeStatus.CliUnavailable, version, error.Message),
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => new(DockerComposeRuntimeStatus.PermissionDenied, version, error.Message),
            RemoteErrorCode.UnsupportedVersion => new(DockerComposeRuntimeStatus.Unsupported, version, error.Message),
            RemoteErrorCode.CapabilityUnavailable => new(DockerComposeRuntimeStatus.DaemonUnavailable, version, error.Message),
            _ => new(DockerComposeRuntimeStatus.Unknown, version, error.Message),
        };

    private static DockerComposeRuntimeState MapRuntime(string detail, string? version = null)
    {
        var error = DockerComposeParser.MapFailure(detail);
        if (detail.Contains("docker: 'compose' is not a docker command", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unknown command", StringComparison.OrdinalIgnoreCase) && detail.Contains("compose", StringComparison.OrdinalIgnoreCase))
        {
            return new DockerComposeRuntimeState(DockerComposeRuntimeStatus.CliUnavailable, version, DockerComposeParser.Sanitize(detail));
        }

        return MapRuntime(error, version);
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return DockerComposeParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second) ? DockerComposeParser.Sanitize(second.Trim()) : fallback;
    }
}

public sealed class AuditedDockerComposeService : IDockerComposeService
{
    private readonly IDockerComposeService _inner;
    private readonly IOperationAudit _audit;

    public AuditedDockerComposeService(IDockerComposeService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<DockerComposeSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public Task<DockerComposeProjectResult> InspectProjectAsync(ServerProfile profile, DockerComposeProject project, CancellationToken cancellationToken = default) =>
        _inner.InspectProjectAsync(profile, project, cancellationToken);

    public Task<DockerComposeLogsResult> ReadLogsAsync(ServerProfile profile, DockerComposeProject project, int tail, CancellationToken cancellationToken = default) =>
        _inner.ReadLogsAsync(profile, project, tail, cancellationToken);

    public RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project) => _inner.BuildConfigValidation(project);

    public async Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = DockerComposeIdentity.Normalize(project);
        var risk = DockerComposeProjection.Risk(action);
        try
        {
            var result = await _inner.ExecuteAsync(profile, identity, action, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            await TryAuditAsync(profile, identity, action, risk, outcome, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryAuditAsync(profile, identity, action, risk, OperationOutcome.Cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask TryAuditAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = OperationAuditEntry.Create(
                "docker-compose-action",
                $"Docker Compose {DockerComposeProjection.Verb(action)} requested for project {project.Name}",
                risk,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} compose:{project.Name}");
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit persistence failure must never cause a remote mutation retry.
        }
    }
}
