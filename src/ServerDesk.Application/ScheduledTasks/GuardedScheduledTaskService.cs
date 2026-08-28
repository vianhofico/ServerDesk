using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.ScheduledTasks;

/// <summary>
/// Production safety boundary for scheduled-task mutations that need stronger
/// filesystem scoping than the portable core service can infer on its own.
/// </summary>
public sealed class GuardedScheduledTaskService : IScheduledTaskService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IScheduledTaskService _inner;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly ScheduledTaskOptions _options;

    public GuardedScheduledTaskService(
        IScheduledTaskService inner,
        IRemoteCommandExecutorFactory commandFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        ScheduledTaskOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ScheduledTaskSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public Task<ScheduledTaskTextResult> ReadHistoryAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(profile, task, cancellationToken);

    public Task<ScheduledTaskTextResult> ReadRawSourceAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        CancellationToken cancellationToken = default) =>
        _inner.ReadRawSourceAsync(profile, task, cancellationToken);

    public Task<ScheduledTaskMutationResult> SaveCronAsync(
        ServerProfile profile,
        CronTaskDraft draft,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default) =>
        _inner.SaveCronAsync(profile, draft, expectedRawCrontab, cancellationToken);

    public Task<ScheduledTaskMutationResult> SetEnabledAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        bool enabled,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default) =>
        _inner.SetEnabledAsync(profile, task, enabled, expectedRawCrontab, cancellationToken);

    public Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(
        ServerProfile profile,
        string candidate,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default) =>
        _inner.ApplyRawCrontabAsync(profile, candidate, expectedRawCrontab, cancellationToken);

    public async Task<ScheduledTaskMutationResult> DeleteAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(task);

        if (task.Kind != ScheduledTaskKind.SystemdTimer)
        {
            return await _inner.DeleteAsync(profile, task, expectedRawCrontab, cancellationToken)
                .ConfigureAwait(false);
        }

        var unit = ScheduledTaskIdentity.NormalizeTimerUnit(task.Unit ?? task.Name);
        var expectedSourcePath = $"/etc/systemd/system/{unit}";
        if (!string.Equals(task.SourcePath, expectedSourcePath, StringComparison.Ordinal))
        {
            var error = new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                $"ServerDesk only deletes the selected locally managed timer file '{expectedSourcePath}'. Packaged, generated, aliased or differently located units must be handled explicitly in the terminal.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var disabled = await _inner.SetEnabledAsync(
                profile,
                task,
                false,
                expectedRawCrontab,
                cancellationToken)
            .ConfigureAwait(false);
        if (!disabled.IsSuccess)
        {
            return disabled;
        }

        var mutationStarted = false;
        try
        {
            await using var fileSystem = _fileSystemFactory.Create(profile);
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            mutationStarted = true;
            await fileSystem.DeleteFileAsync(RemotePath.Parse(expectedSourcePath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous(
                $"Deletion of systemd timer '{unit}' was cancelled after the timer-file mutation started. Refresh before deciding whether to retry.");
        }
        catch (RemoteFileSystemException exception) when (mutationStarted)
        {
            return Ambiguous(
                $"systemd timer '{unit}' was disabled, but deletion of its timer file could not be completed reliably. Refresh before retrying.",
                exception.Error.Message);
        }
        catch (RemoteFileSystemException exception)
        {
            return new ScheduledTaskMutationResult(false, exception.Error, exception.Error.Message);
        }

        await using var executor = _commandFactory.Create(profile);
        var reload = await executor.ExecuteAsync(
                Command("systemctl", ["daemon-reload"], OperationRisk.Mutating),
                cancellationToken)
            .ConfigureAwait(false);
        if (reload.Error is not null)
        {
            return IsAmbiguousTransport(reload.Error.Code)
                ? Ambiguous(
                    $"Timer file '{expectedSourcePath}' was removed, but ServerDesk lost a reliable completion signal during systemd daemon-reload. Refresh before retrying.",
                    reload.Error.TechnicalDetails)
                : Ambiguous(
                    $"Timer file '{expectedSourcePath}' was removed, but systemd daemon-reload failed. Refresh systemd state before retrying.",
                    reload.Error.Message);
        }

        if (reload.Command!.ExitCode != 0)
        {
            return Ambiguous(
                $"Timer file '{expectedSourcePath}' was removed, but systemd daemon-reload returned exit code {reload.Command.ExitCode}. Refresh systemd state before retrying.");
        }

        var show = await executor.ExecuteAsync(
                Command(
                    "systemctl",
                    ["show", unit, "--no-pager", "--property=LoadState"],
                    OperationRisk.ReadOnly),
                cancellationToken)
            .ConfigureAwait(false);
        if (show.Error is not null)
        {
            return Ambiguous(
                $"Timer file '{expectedSourcePath}' was removed, but ServerDesk could not verify that systemd forgot '{unit}'. Refresh before retrying.",
                show.Error.Message);
        }

        var absent = show.Command!.ExitCode == 0 &&
                     show.Command.StandardOutput.Contains(
                         "LoadState=not-found",
                         StringComparison.OrdinalIgnoreCase);
        if (!absent)
        {
            return Ambiguous(
                $"Timer file '{expectedSourcePath}' was removed, but systemd still reports '{unit}' as present. Refresh before retrying.");
        }

        var snapshot = await _inner.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        return new ScheduledTaskMutationResult(
            true,
            null,
            $"systemd timer '{unit}' was deleted and verified absent. Its trigger service was left unchanged.",
            snapshot.Snapshot);
    }

    private RemoteCommandSpec Command(
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk) =>
        new(executable, arguments, _options.CommandTimeout, risk, StableEnvironment);

    private static ScheduledTaskMutationResult Ambiguous(string message, string? technicalDetails = null)
    {
        var error = new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails);
        return new ScheduledTaskMutationResult(false, error, error.Message);
    }

    private static bool IsAmbiguousTransport(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled;
}
