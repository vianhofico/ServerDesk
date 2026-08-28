using ServerDesk.Application.RemoteEditing;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Docker;

public enum DockerComposeRuntimeStatus
{
    Available,
    CliUnavailable,
    DaemonUnavailable,
    PermissionDenied,
    Unsupported,
    Unknown,
}

public sealed record DockerComposeRuntimeState(
    DockerComposeRuntimeStatus Status,
    string? Version,
    string Detail)
{
    public bool IsUsable => Status == DockerComposeRuntimeStatus.Available;
}

public sealed record DockerComposeProject(
    string Name,
    string Status,
    IReadOnlyList<string> ConfigFiles)
{
    public string PrimaryConfigFile => ConfigFiles.FirstOrDefault() ?? string.Empty;
}

public sealed record DockerComposeServiceInfo(
    string Id,
    string Name,
    string Service,
    string Image,
    string State,
    string Status,
    string Publishers);

public sealed record DockerComposeProjectDetails(
    DockerComposeProject Project,
    IReadOnlyList<DockerComposeServiceInfo> Services,
    string NormalizedConfigJson);

public sealed record DockerComposeSnapshot(
    DockerComposeRuntimeState Runtime,
    IReadOnlyList<DockerComposeProject> Projects);

public sealed record DockerComposeSnapshotResult(
    DockerComposeSnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null && Snapshot is not null;
}

public sealed record DockerComposeProjectResult(
    DockerComposeProjectDetails? Details,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null && Details is not null;
}

public sealed record DockerComposeLogsResult(
    IReadOnlyList<string> Lines,
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
    DockerComposeProjectDetails? VerifiedDetails = null);

public sealed record DockerComposeOptions(
    TimeSpan ReadTimeout,
    TimeSpan MutationTimeout,
    int DefaultLogRows,
    int MaximumLogRows)
{
    public static DockerComposeOptions Default { get; } = new(
        TimeSpan.FromSeconds(25),
        TimeSpan.FromMinutes(2),
        250,
        5000);

    public void Validate()
    {
        if (ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
        }

        if (MutationTimeout <= TimeSpan.Zero || MutationTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(MutationTimeout));
        }

        if (DefaultLogRows is < 1 or > 5000 || MaximumLogRows is < 1 or > 20000 || DefaultLogRows > MaximumLogRows)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultLogRows));
        }
    }
}

public interface IDockerComposeService
{
    Task<DockerComposeSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<DockerComposeProjectResult> InspectProjectAsync(
        ServerProfile profile,
        DockerComposeProject project,
        CancellationToken cancellationToken = default);

    Task<DockerComposeLogsResult> ReadLogsAsync(
        ServerProfile profile,
        DockerComposeProject project,
        int tail,
        CancellationToken cancellationToken = default);

    Task<DockerComposeActionResult> ExecuteAsync(
        ServerProfile profile,
        DockerComposeProject project,
        DockerComposeAction action,
        CancellationToken cancellationToken = default);

    RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project);
}

public static class DockerComposeIdentity
{
    public static string NormalizeProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
            normalized.Length > 128 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new FormatException("Docker Compose project name must not contain surrounding whitespace, must start with a letter or digit, and may contain only letters, digits, '-' or '_'.");
        }

        return normalized;
    }

    public static string NormalizeConfigPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
            !normalized.StartsWith('/', StringComparison.Ordinal) ||
            normalized.Length > 4096 ||
            normalized.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new FormatException("Docker Compose configuration path must be an absolute Linux path without surrounding whitespace or control characters.");
        }

        return normalized;
    }

    public static DockerComposeProject Normalize(DockerComposeProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var name = NormalizeProjectName(project.Name);
        var configFiles = project.ConfigFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeConfigPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (configFiles.Length == 0)
        {
            throw new FormatException("Docker Compose project does not expose a configuration file path.");
        }

        return project with { Name = name, ConfigFiles = configFiles };
    }
}

public static class DockerComposeProjection
{
    public static IReadOnlyList<DockerComposeProject> FilterProjects(
        IReadOnlyList<DockerComposeProject> projects,
        string? search)
    {
        var query = search?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return projects;
        }

        return projects
            .Where(project =>
                project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                project.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                project.ConfigFiles.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static OperationRisk Risk(DockerComposeAction action) =>
        action is DockerComposeAction.Up or DockerComposeAction.Down or DockerComposeAction.Restart
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;

    public static string Verb(DockerComposeAction action) => action switch
    {
        DockerComposeAction.Up => "up",
        DockerComposeAction.Down => "down",
        DockerComposeAction.Restart => "restart",
        DockerComposeAction.Pull => "pull",
        DockerComposeAction.Build => "build",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
