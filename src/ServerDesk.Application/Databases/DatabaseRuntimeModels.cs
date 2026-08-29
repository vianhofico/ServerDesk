using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Databases;

public enum DatabaseEngineKind
{
    PostgreSql,
    MySql,
    MariaDb,
    Redis,
}

public enum DatabaseEngineRuntimeStatus
{
    Active,
    Inactive,
    Installed,
    CliUnavailable,
    PermissionDenied,
    ProbeFailed,
    Unknown,
}

public sealed record DatabaseEngineObservation(
    DatabaseEngineKind Engine,
    DatabaseEngineRuntimeStatus Status,
    string Executable,
    string? Version,
    string? ServiceUnit,
    string? ActiveState,
    string? SubState,
    string? JournalUnit,
    string Detail)
{
    public bool IsInstalled => Status is not DatabaseEngineRuntimeStatus.CliUnavailable;

    public bool IsActive => Status == DatabaseEngineRuntimeStatus.Active;
}

public sealed record DatabaseRuntimeSnapshot(
    IReadOnlyList<DatabaseEngineObservation> Engines,
    DateTimeOffset ObservedAtUtc)
{
    public bool HasSupportedEngine => Engines.Any(engine => engine.IsInstalled);

    public int ActiveEngineCount => Engines.Count(engine => engine.IsActive);
}

public sealed record DatabaseRuntimeResult(
    DatabaseRuntimeSnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record DatabaseRuntimeOptions(
    TimeSpan CommandTimeout,
    int MaximumOutputBytes)
{
    public static DatabaseRuntimeOptions Default { get; } = new(
        TimeSpan.FromSeconds(15),
        256 * 1024);

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumOutputBytes is <= 0 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes));
        }
    }
}
