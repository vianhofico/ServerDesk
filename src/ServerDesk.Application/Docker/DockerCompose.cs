using System.Globalization;
using System.Text;
using System.Text.Json;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public enum DockerComposeRuntimeStatus
{
    Available,
    CliUnavailable,
    EngineUnavailable,
    PermissionDenied,
    Unsupported,
    Unknown,
}

public sealed record DockerComposeRuntime(
    DockerComposeRuntimeStatus Status,
    string Version,
    string Detail)
{
    public bool IsUsable => Status == DockerComposeRuntimeStatus.Available;
}

public sealed record DockerComposeProject(
    string Name,
    string Status,
    IReadOnlyList<RemotePath> ConfigFiles,
    RemotePath WorkingDirectory)
{
    public RemotePath PrimaryConfigFile => ConfigFiles[0];
}

public sealed record DockerComposeServiceInfo(
    string Name,
    string Service,
    string State,
    string Status,
    string Health,
    string Image,
    string Ports);

public sealed record DockerComposeProjectState(
    DockerComposeProject Project,
    IReadOnlyList<DockerComposeServiceInfo> Services,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record DockerComposeProjectListResult(
    DockerComposeRuntime Runtime,
    IReadOnlyList<DockerComposeProject> Projects,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public enum DockerComposeAction
{
    Up,
    Down,
    Restart,
    Pull,
    Build,
}

public sealed record DockerComposeActionResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    DockerComposeProjectState? VerifiedState = null);

public sealed record DockerComposeLogResult(
    IReadOnlyList<string> Lines,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record DockerComposeConfigSaveResult(
    bool IsSuccess,
    bool ValidationFailed,
    string Message,
    RemoteError? Error = null);

public sealed record DockerComposeOptions(
    TimeSpan ReadTimeout,
    TimeSpan MutationTimeout,
    int DefaultLogRows,
    int MaxLogRows)
{
    public static DockerComposeOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromMinutes(2),
        200,
        5_000);

    public void Validate()
    {
        if (ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(1) ||
            MutationTimeout <= TimeSpan.Zero || MutationTimeout > TimeSpan.FromMinutes(10) ||
            DefaultLogRows <= 0 || MaxLogRows < DefaultLogRows)
        {
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
        }
    }
}

public interface IDockerComposeService
{
    DockerComposeOptions Options { get; }

    Task<DockerComposeRuntime> DetectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<DockerComposeProjectListResult> ListProjectsAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<DockerComposeProjectState> ReadProjectAsync(
        ServerProfile profile,
        DockerComposeProject project,
        CancellationToken cancellationToken = default);

    Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default);

    Task<DockerComposeLogResult> ReadLogsAsync(
        ServerProfile profile,
        DockerComposeProject project,
        int? count = null,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteEditorDocument> LoadConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemotePath path,
        CancellationToken cancellationToken = default);

    Task<DockerComposeConfigSaveResult> SaveConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemoteEditorDocument original,
        string editedText,
        bool privileged,
        CancellationToken cancellationToken = default);
}

public sealed class DockerComposeService : IDockerComposeService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly IRemoteFileEditorService _editor;

    public DockerComposeService(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        IRemoteFileEditorService editor,
        DockerComposeOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
    }

    public DockerComposeOptions Options { get; }

    public async Task<DockerComposeRuntime> DetectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                ReadOnlyCommand(["compose", "version", "--short"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return RuntimeFromError(execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, "Docker Compose v2 is unavailable.");
            return RuntimeFromText(detail);
        }

        var version = DockerDiagnosticsText.Sanitize(command.StandardOutput.Trim());
        return new DockerComposeRuntime(
            DockerComposeRuntimeStatus.Available,
            version,
            string.IsNullOrWhiteSpace(version)
                ? "Docker Compose v2 responded successfully."
                : $"Docker Compose v2 {version} is available through the remote Docker CLI.");
    }

    public async Task<DockerComposeProjectListResult> ListProjectsAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var runtime = await DetectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!runtime.IsUsable)
        {
            var error = new RemoteError(
                runtime.Status == DockerComposeRuntimeStatus.PermissionDenied
                    ? RemoteErrorCode.PermissionDenied
                    : runtime.Status == DockerComposeRuntimeStatus.Unsupported
                        ? RemoteErrorCode.UnsupportedVersion
                        : RemoteErrorCode.CapabilityUnavailable,
                runtime.Detail);
            return new DockerComposeProjectListResult(runtime, [], error);
        }

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                ReadOnlyCommand(["compose", "ls", "--all", "--format", "json"]),
                cancellationToken)
            .ConfigureAwait(false);
        var errorResult = ReadError(execution, "Unable to list Docker Compose projects.");
        if (errorResult is not null)
        {
            return new DockerComposeProjectListResult(runtime, [], errorResult);
        }

        try
        {
            return new DockerComposeProjectListResult(
                runtime,
                DockerComposeParser.ParseProjects(execution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new DockerComposeProjectListResult(
                runtime,
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse Docker Compose project inventory.",
                    exception.Message));
        }
    }

    public async Task<DockerComposeProjectState> ReadProjectAsync(
        ServerProfile profile,
        DockerComposeProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProject(project);
        await using var executor = _commandExecutorFactory.Create(profile);
        var arguments = ProjectArguments(project);
        arguments.AddRange(["ps", "--all", "--format", "json"]);
        var execution = await executor.ExecuteAsync(ReadOnlyCommand(arguments), cancellationToken).ConfigureAwait(false);
        var error = ReadError(execution, $"Unable to inspect Docker Compose project '{project.Name}'.");
        if (error is not null)
        {
            return new DockerComposeProjectState(project, [], error);
        }

        try
        {
            return new DockerComposeProjectState(
                project,
                DockerComposeParser.ParseServices(execution.Command!.StandardOutput),
                null);
        }
        catch (FormatException exception)
        {
            return new DockerComposeProjectState(
                project,
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    $"ServerDesk could not parse Docker Compose service state for '{project.Name}'.",
                    exception.Message));
        }
    }

    public async Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProject(project);
        var validation = await ValidateLiveConfigAsync(profile, project, cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return new DockerComposeActionResult(false, validation, validation.Message);
        }

        var arguments = ProjectArguments(project);
        arguments.AddRange(ActionArguments(action));
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "docker",
                    arguments,
                    Options.MutationTimeout,
                    Risk(action),
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = IsAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"ServerDesk lost a reliable completion signal while running Docker Compose {Verb(action)} for '{project.Name}'. Refresh project state before deciding whether to retry.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new DockerComposeActionResult(false, error, error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(
                command.StandardError,
                command.StandardOutput,
                $"Docker Compose {Verb(action)} failed for '{project.Name}'.");
            var error = new RemoteError(ClassifyFailure(detail), detail);
            return new DockerComposeActionResult(false, error, detail);
        }

        if (action is DockerComposeAction.Pull or DockerComposeAction.Build)
        {
            var verifyConfig = await ValidateLiveConfigAsync(profile, project, cancellationToken).ConfigureAwait(false);
            if (verifyConfig is not null)
            {
                var ambiguous = new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"Docker Compose {Verb(action)} returned success for '{project.Name}', but live configuration verification failed. {verifyConfig.Message}");
                return new DockerComposeActionResult(false, ambiguous, ambiguous.Message);
            }

            return new DockerComposeActionResult(
                true,
                null,
                $"Docker Compose {Verb(action)} completed and live configuration remains valid for '{project.Name}'.");
        }

        var state = await ReadProjectAsync(profile, project, cancellationToken).ConfigureAwait(false);
        if (!state.IsSuccess)
        {
            var ambiguous = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Docker Compose {Verb(action)} returned success for '{project.Name}', but ServerDesk could not verify project state. {state.Error?.Message}");
            return new DockerComposeActionResult(false, ambiguous, ambiguous.Message);
        }

        var matches = action switch
        {
            DockerComposeAction.Down => state.Services.Count == 0,
            DockerComposeAction.Up or DockerComposeAction.Restart => state.Services.Count > 0,
            _ => false,
        };
        if (!matches)
        {
            var ambiguous = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Docker Compose {Verb(action)} returned success for '{project.Name}', but verified service count is {state.Services.Count:N0}. Refresh before retrying.");
            return new DockerComposeActionResult(false, ambiguous, ambiguous.Message, state);
        }

        return new DockerComposeActionResult(
            true,
            null,
            $"Docker Compose {Verb(action)} completed and verified for '{project.Name}'.",
            state);
    }

    public async Task<DockerComposeLogResult> ReadLogsAsync(
        ServerProfile profile,
        DockerComposeProject project,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProject(project);
        var rows = count ?? Options.DefaultLogRows;
        if (rows <= 0 || rows > Options.MaxLogRows)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var arguments = ProjectArguments(project);
        arguments.AddRange([
            "logs",
            "--no-color",
            "--timestamps",
            "--tail",
            rows.ToString(CultureInfo.InvariantCulture),
        ]);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(ReadOnlyCommand(arguments), cancellationToken).ConfigureAwait(false);
        var error = ReadError(execution, $"Unable to read Docker Compose logs for '{project.Name}'.");
        if (error is not null)
        {
            return new DockerComposeLogResult([], error);
        }

        var text = string.Join(
            '\n',
            new[] { execution.Command!.StandardOutput, execution.Command.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(DockerDiagnosticsText.Sanitize)
            .TakeLast(rows)
            .ToArray();
        return new DockerComposeLogResult(lines, null);
    }

    public ValueTask<RemoteEditorDocument> LoadConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProject(project);
        EnsureProjectConfigPath(project, path);
        return _editor.LoadAsync(profile, path, cancellationToken);
    }

    public async Task<DockerComposeConfigSaveResult> SaveConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemoteEditorDocument original,
        string editedText,
        bool privileged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        ValidateProject(project);
        EnsureProjectConfigPath(project, original.Metadata.Path);

        RemoteEditValidationSpec? privilegedValidation = null;
        if (privileged)
        {
            privilegedValidation = new RemoteEditValidationSpec(
                "docker",
                BuildValidationArguments(project, original.Metadata.Path, "{file}"));
            var privilegedResult = await _editor.SavePrivilegedAsync(
                    profile,
                    original,
                    editedText,
                    privilegedValidation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!privilegedResult.IsSuccess)
            {
                return new DockerComposeConfigSaveResult(
                    false,
                    privilegedResult.ValidationFailed,
                    privilegedResult.Message,
                    privilegedResult.Error);
            }
        }
        else
        {
            var validation = await ValidateCandidateAsync(
                    profile,
                    project,
                    original.Metadata.Path,
                    editedText,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validation is not null)
            {
                return new DockerComposeConfigSaveResult(
                    false,
                    validation.Code == RemoteErrorCode.ParseFailed,
                    validation.Message,
                    validation);
            }

            var save = await _editor.SaveWritableAsync(
                    profile,
                    original,
                    editedText,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!save.IsSuccess)
            {
                return new DockerComposeConfigSaveResult(false, save.ValidationFailed, save.Message, save.Error);
            }
        }

        var liveValidation = await ValidateLiveConfigAsync(profile, project, cancellationToken).ConfigureAwait(false);
        if (liveValidation is not null)
        {
            var ambiguous = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"The Compose file was saved, but live configuration validation no longer succeeds. Reload the file before another change. {liveValidation.Message}");
            return new DockerComposeConfigSaveResult(false, false, ambiguous.Message, ambiguous);
        }

        return new DockerComposeConfigSaveResult(
            true,
            false,
            "Compose YAML validated, saved without semantic rewriting, and the live multi-file configuration was verified.");
    }

    public static OperationRisk Risk(DockerComposeAction action) =>
        action is DockerComposeAction.Up or DockerComposeAction.Down or DockerComposeAction.Restart
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;

    public static string Verb(DockerComposeAction action) => action.ToString().ToLowerInvariant();

    public static void ValidateProject(DockerComposeProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateProjectName(project.Name);
        if (project.ConfigFiles.Count == 0)
        {
            throw new ArgumentException("Compose project must include at least one configuration file.", nameof(project));
        }

        foreach (var file in project.ConfigFiles)
        {
            if (!file.IsAbsolute)
            {
                throw new ArgumentException("Compose configuration paths must be absolute remote paths.", nameof(project));
            }
        }

        if (!project.WorkingDirectory.IsAbsolute)
        {
            throw new ArgumentException("Compose project working directory must be absolute.", nameof(project));
        }
    }

    public static void ValidateProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException(
                "Compose project names must be lowercase ASCII alphanumeric identifiers with optional '-' or '_' separators.",
                nameof(value));
        }
    }

    private async Task<RemoteError?> ValidateCandidateAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemotePath target,
        string text,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var stage = RemotePath.Parse($"/tmp/serverdesk-compose-{token}.yaml");
        var payload = StrictUtf8.GetBytes(text ?? string.Empty);
        await using var fileSystem = _fileSystemFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = new MemoryStream(payload, writable: false);
            await fileSystem.UploadAsync(
                    stream,
                    stage,
                    payload.Length,
                    overwrite: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await fileSystem.SetPermissionsAsync(stage, RemoteUnixPermissions.FromMode(600), cancellationToken)
                .ConfigureAwait(false);

            await using var executor = _commandExecutorFactory.Create(profile);
            var execution = await executor.ExecuteAsync(
                    ReadOnlyCommand(BuildValidationArguments(project, target, stage.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.Error is not null)
            {
                return execution.Error;
            }

            if (execution.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(
                    execution.Command.StandardError,
                    execution.Command.StandardOutput,
                    "Docker Compose rejected the staged YAML configuration.");
                return new RemoteError(RemoteErrorCode.ParseFailed, detail);
            }

            return null;
        }
        catch (RemoteFileSystemException exception)
        {
            return exception.Error;
        }
        finally
        {
            try
            {
                if (!fileSystem.IsConnected)
                {
                    await fileSystem.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await fileSystem.DeleteFileAsync(stage, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task<RemoteError?> ValidateLiveConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                ReadOnlyCommand(BuildValidationArguments(project, null, null)),
                cancellationToken)
            .ConfigureAwait(false);
        var error = ReadError(execution, $"Docker Compose configuration for '{project.Name}' is not usable.");
        if (error is not null)
        {
            return error;
        }

        return null;
    }

    private static List<string> ProjectArguments(DockerComposeProject project)
    {
        ValidateProject(project);
        var result = new List<string>
        {
            "compose",
            "--project-name",
            project.Name,
            "--project-directory",
            project.WorkingDirectory.Value,
        };
        foreach (var file in project.ConfigFiles)
        {
            result.Add("--file");
            result.Add(file.Value);
        }

        return result;
    }

    private static IReadOnlyList<string> BuildValidationArguments(
        DockerComposeProject project,
        RemotePath? replacedPath,
        string? stagedPath)
    {
        var result = new List<string>
        {
            "compose",
            "--project-name",
            project.Name,
            "--project-directory",
            project.WorkingDirectory.Value,
        };
        foreach (var file in project.ConfigFiles)
        {
            result.Add("--file");
            result.Add(replacedPath is not null && file == replacedPath.Value && stagedPath is not null
                ? stagedPath
                : file.Value);
        }

        result.Add("config");
        result.Add("--quiet");
        return result;
    }

    private static IReadOnlyList<string> ActionArguments(DockerComposeAction action) =>
        action switch
        {
            DockerComposeAction.Up => ["up", "--detach"],
            DockerComposeAction.Down => ["down"],
            DockerComposeAction.Restart => ["restart"],
            DockerComposeAction.Pull => ["pull"],
            DockerComposeAction.Build => ["build"],
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private RemoteCommandSpec ReadOnlyCommand(IReadOnlyList<string> arguments) =>
        new("docker", arguments, Options.ReadTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private static void EnsureProjectConfigPath(DockerComposeProject project, RemotePath path)
    {
        if (!project.ConfigFiles.Contains(path))
        {
            throw new ArgumentException("The requested file is not part of the selected Compose project.", nameof(path));
        }
    }

    private static RemoteError? ReadError(RemoteExecutionResult execution, string fallback)
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
        return new RemoteError(ClassifyFailure(detail), detail);
    }

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.CapabilityUnavailable;
        }

        if (detail.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not a docker command", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("compose is not a docker command", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.UnsupportedVersion;
        }

        if (detail.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathConflict;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static DockerComposeRuntime RuntimeFromError(RemoteError error) =>
        new(
            error.Code == RemoteErrorCode.PermissionDenied
                ? DockerComposeRuntimeStatus.PermissionDenied
                : error.Code is RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable
                    ? DockerComposeRuntimeStatus.CliUnavailable
                    : error.Code == RemoteErrorCode.UnsupportedVersion
                        ? DockerComposeRuntimeStatus.Unsupported
                        : DockerComposeRuntimeStatus.Unknown,
            string.Empty,
            error.Message);

    private static DockerComposeRuntime RuntimeFromText(string detail)
    {
        var status = detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            ? DockerComposeRuntimeStatus.PermissionDenied
            : detail.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase)
                ? DockerComposeRuntimeStatus.EngineUnavailable
                : detail.Contains("not a docker command", StringComparison.OrdinalIgnoreCase) ||
                  detail.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                    ? DockerComposeRuntimeStatus.CliUnavailable
                    : detail.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)
                        ? DockerComposeRuntimeStatus.Unsupported
                        : DockerComposeRuntimeStatus.Unknown;
        return new DockerComposeRuntime(status, string.Empty, DockerDiagnosticsText.Sanitize(detail));
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

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

public static class DockerComposeParser
{
    public static IReadOnlyList<DockerComposeProject> ParseProjects(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Docker Compose ls output is not a JSON array.");
            }

            var result = new List<DockerComposeProject>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Docker Compose project entry is not an object.");
                }

                var name = Text(item, "Name");
                DockerComposeService.ValidateProjectName(name);
                var files = ParseConfigFiles(item);
                if (files.Count == 0)
                {
                    throw new FormatException($"Compose project '{name}' has no configuration file identity.");
                }

                var workingDirectory = files[0].Parent;
                result.Add(new DockerComposeProject(
                    name,
                    Text(item, "Status"),
                    files,
                    workingDirectory));
            }

            return result.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker Compose project output is not valid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Docker Compose project identity is unsafe or malformed.", exception);
        }
    }

    public static IReadOnlyList<DockerComposeServiceInfo> ParseServices(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var trimmed = output.Trim();
        try
        {
            if (trimmed.StartsWith('[', StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(trimmed);
                return document.RootElement.EnumerateArray().Select(ParseService).ToArray();
            }

            var result = new List<DockerComposeServiceInfo>();
            foreach (var line in trimmed.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                result.Add(ParseService(document.RootElement));
            }

            return result.OrderBy(service => service.Service, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException exception)
        {
            throw new FormatException("Docker Compose ps output is not valid JSON/JSON Lines.", exception);
        }
    }

    private static DockerComposeServiceInfo ParseService(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Docker Compose service row is not a JSON object.");
        }

        return new DockerComposeServiceInfo(
            Text(item, "Name"),
            Text(item, "Service"),
            Text(item, "State"),
            Text(item, "Status"),
            Text(item, "Health"),
            Text(item, "Image"),
            Publishers(item));
    }

    private static IReadOnlyList<RemotePath> ParseConfigFiles(JsonElement item)
    {
        if (!item.TryGetProperty("ConfigFiles", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        IEnumerable<string> raw = value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString() ?? string.Empty),
            _ => throw new FormatException("Docker Compose ConfigFiles is neither a string nor an array."),
        };

        return raw.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(RemotePath.Parse)
            .Where(path => path.IsAbsolute)
            .Distinct()
            .ToArray();
    }

    private static string Publishers(JsonElement item)
    {
        if (!item.TryGetProperty("Publishers", out var publishers) || publishers.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (publishers.ValueKind != JsonValueKind.Array)
        {
            return DockerDiagnosticsText.Sanitize(publishers.GetRawText());
        }

        return string.Join(", ", publishers.EnumerateArray().Select(publisher =>
        {
            var published = Text(publisher, "PublishedPort");
            var target = Text(publisher, "TargetPort");
            var protocol = Text(publisher, "Protocol");
            return string.IsNullOrWhiteSpace(published)
                ? target
                : $"{published}->{target}/{protocol}";
        }).Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string Text(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return DockerDiagnosticsText.Sanitize(value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText());
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

    public DockerComposeOptions Options => _inner.Options;

    public Task<DockerComposeRuntime> DetectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
        _inner.DetectAsync(profile, cancellationToken);

    public Task<DockerComposeProjectListResult> ListProjectsAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
        _inner.ListProjectsAsync(profile, cancellationToken);

    public Task<DockerComposeProjectState> ReadProjectAsync(ServerProfile profile, DockerComposeProject project, CancellationToken cancellationToken = default) =>
        _inner.ReadProjectAsync(profile, project, cancellationToken);

    public Task<DockerComposeLogResult> ReadLogsAsync(ServerProfile profile, DockerComposeProject project, int? count = null, CancellationToken cancellationToken = default) =>
        _inner.ReadLogsAsync(profile, project, count, cancellationToken);

    public ValueTask<RemoteEditorDocument> LoadConfigAsync(ServerProfile profile, DockerComposeProject project, RemotePath path, CancellationToken cancellationToken = default) =>
        _inner.LoadConfigAsync(profile, project, path, cancellationToken);

    public async Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default)
    {
        var risk = DockerComposeService.Risk(action);
        try
        {
            var result = await _inner.ExecuteAsync(profile, project, action, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(
                profile,
                project,
                $"compose-{DockerComposeService.Verb(action)}",
                $"Docker Compose {DockerComposeService.Verb(action)} requested for project {project.Name}",
                risk,
                outcome,
                cancellationToken).ConfigureAwait(false);
            return persisted ? result : result with { Message = result.Message + " Audit record could not be persisted; do not retry solely for that reason." };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(
                profile,
                project,
                $"compose-{DockerComposeService.Verb(action)}",
                $"Docker Compose {DockerComposeService.Verb(action)} requested for project {project.Name}",
                risk,
                OperationOutcome.Cancelled,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DockerComposeConfigSaveResult> SaveConfigAsync(
        ServerProfile profile,
        DockerComposeProject project,
        RemoteEditorDocument original,
        string editedText,
        bool privileged,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _inner.SaveConfigAsync(
                profile,
                project,
                original,
                editedText,
                privileged,
                cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(
                profile,
                project,
                "compose-config-save",
                $"Compose raw YAML save requested for {original.Metadata.Path.Value}",
                OperationRisk.Mutating,
                outcome,
                cancellationToken).ConfigureAwait(false);
            return persisted ? result : result with { Message = result.Message + " Audit record could not be persisted; do not retry solely for that reason." };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(
                profile,
                project,
                "compose-config-save",
                $"Compose raw YAML save requested for {original.Metadata.Path.Value}",
                OperationRisk.Mutating,
                OperationOutcome.Cancelled,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        DockerComposeProject project,
        string category,
        string summary,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} compose:{project.Name}";
            await _audit.AppendAsync(
                OperationAuditEntry.Create(category, summary, risk, outcome, target),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
