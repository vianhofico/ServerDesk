using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.ScheduledTasks;

public interface IScheduledTaskService
{
    Task<ScheduledTaskSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default);

    Task<ScheduledTaskTextResult> ReadHistoryAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default);

    Task<ScheduledTaskTextResult> ReadRawSourceAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default);

    Task<ScheduledTaskMutationResult> SaveCronAsync(ServerProfile profile, CronTaskDraft draft, string expectedRawCrontab, CancellationToken cancellationToken = default);

    Task<ScheduledTaskMutationResult> SetEnabledAsync(ServerProfile profile, ScheduledTaskInfo task, bool enabled, string expectedRawCrontab, CancellationToken cancellationToken = default);

    Task<ScheduledTaskMutationResult> DeleteAsync(ServerProfile profile, ScheduledTaskInfo task, string expectedRawCrontab, CancellationToken cancellationToken = default);

    Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(ServerProfile profile, string candidate, string expectedRawCrontab, CancellationToken cancellationToken = default);
}

public sealed class ScheduledTaskService : IScheduledTaskService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly ScheduledTaskOptions _options;

    public ScheduledTaskService(
        IRemoteCommandExecutorFactory commandFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        ScheduledTaskOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ScheduledTaskSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var warnings = new List<string>();
        var tasks = new List<ScheduledTaskInfo>();

        var cron = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
        if (cron.Error is not null)
        {
            warnings.Add(cron.Error.Message);
        }
        else if (cron.Available)
        {
            tasks.AddRange(ScheduledTaskParser.ParseCrontab(cron.Raw));
        }

        var timers = await ReadTimersAsync(profile, cancellationToken).ConfigureAwait(false);
        if (timers.Error is not null)
        {
            warnings.Add(timers.Error.Message);
        }
        else
        {
            tasks.AddRange(timers.Tasks);
        }

        if (!cron.Available && !timers.Available && cron.Error is not null && timers.Error is not null)
        {
            return new ScheduledTaskSnapshotResult(
                null,
                new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    "Neither crontab nor systemd timers could be inspected for this server.",
                    string.Join(" | ", warnings)));
        }

        var snapshot = new ScheduledTaskSnapshot(
            tasks.OrderBy(task => task.Kind).ThenBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ScheduledTaskParser.NormalizeRawCron(cron.Raw),
            cron.Available,
            timers.Available,
            warnings);
        return new ScheduledTaskSnapshotResult(snapshot, null);
    }

    public async Task<ScheduledTaskTextResult> ReadHistoryAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Kind != ScheduledTaskKind.SystemdTimer || string.IsNullOrWhiteSpace(task.Unit))
        {
            return new ScheduledTaskTextResult(
                string.Empty,
                new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    "Structured history is available for systemd timers only; cron execution history depends on the server's logging configuration."));
        }

        var unit = ScheduledTaskIdentity.NormalizeTimerUnit(task.Unit);
        await using var executor = _commandFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "journalctl",
                    ["--unit", unit, "--no-pager", "--lines", _options.MaximumHistoryLines.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    _options.CommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var failure = Failure(execution, $"Unable to read history for '{unit}'.");
        if (failure is not null)
        {
            return new ScheduledTaskTextResult(string.Empty, failure);
        }

        var lines = ScheduledTaskParser.ParseHistory(execution.Command!.StandardOutput, _options.MaximumHistoryLines);
        return new ScheduledTaskTextResult(string.Join(Environment.NewLine, lines), null);
    }

    public async Task<ScheduledTaskTextResult> ReadRawSourceAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Kind == ScheduledTaskKind.Cron)
        {
            var cron = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
            return cron.Error is null
                ? new ScheduledTaskTextResult(ScheduledTaskParser.NormalizeRawCron(cron.Raw), null)
                : new ScheduledTaskTextResult(string.Empty, cron.Error);
        }

        var unit = ScheduledTaskIdentity.NormalizeTimerUnit(task.Unit ?? task.Name);
        await using var executor = _commandFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                ReadOnly("systemctl", ["cat", unit, "--no-pager"]),
                cancellationToken)
            .ConfigureAwait(false);
        var failure = Failure(execution, $"Unable to read raw systemd source for '{unit}'.");
        return failure is null
            ? new ScheduledTaskTextResult(ScheduledTaskParser.Sanitize(execution.Command!.StandardOutput), null)
            : new ScheduledTaskTextResult(string.Empty, failure);
    }

    public async Task<ScheduledTaskMutationResult> SaveCronAsync(
        ServerProfile profile,
        CronTaskDraft draft,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();
        var expected = ScheduledTaskParser.NormalizeRawCron(expectedRawCrontab);
        var current = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || !current.Available)
        {
            var error = current.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "crontab is unavailable.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var raw = ScheduledTaskParser.NormalizeRawCron(current.Raw);
        if (!string.Equals(raw, expected, StringComparison.Ordinal))
        {
            return Conflict("The crontab changed after it was loaded. Refresh before editing to avoid overwriting another change.");
        }

        string candidate;
        if (string.IsNullOrWhiteSpace(draft.ExistingTaskId))
        {
            candidate = AppendCronLine(raw, draft.ToCronLine());
        }
        else
        {
            var target = ScheduledTaskParser.ParseCrontab(raw)
                .SingleOrDefault(task => task.Kind == ScheduledTaskKind.Cron && string.Equals(task.Id, draft.ExistingTaskId, StringComparison.Ordinal));
            if (target?.SourceLineIndex is null)
            {
                return Conflict("The cron entry selected for editing no longer exists. Refresh before retrying.");
            }

            candidate = ReplaceCronLine(raw, target.SourceLineIndex.Value, draft.ToCronLine());
        }

        return await ApplyRawCrontabCoreAsync(profile, candidate, raw, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduledTaskMutationResult> SetEnabledAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        bool enabled,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Kind == ScheduledTaskKind.SystemdTimer)
        {
            return await SetSystemdEnabledAsync(profile, task, enabled, cancellationToken).ConfigureAwait(false);
        }

        var expected = ScheduledTaskParser.NormalizeRawCron(expectedRawCrontab);
        var current = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || !current.Available)
        {
            var error = current.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "crontab is unavailable.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var raw = ScheduledTaskParser.NormalizeRawCron(current.Raw);
        if (!string.Equals(raw, expected, StringComparison.Ordinal))
        {
            return Conflict("The crontab changed after it was loaded. Refresh before changing task state.");
        }

        var target = ScheduledTaskParser.ParseCrontab(raw)
            .SingleOrDefault(item => string.Equals(item.Id, task.Id, StringComparison.Ordinal));
        if (target?.SourceLineIndex is null)
        {
            return Conflict("The cron entry no longer exists. Refresh before retrying.");
        }

        if (target.Enabled == enabled)
        {
            var snapshot = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            return new ScheduledTaskMutationResult(true, null, "Cron task already has the requested enabled state.", snapshot.Snapshot);
        }

        var line = target.Enabled
            ? ScheduledTaskParser.DisabledCronPrefix + target.Raw
            : target.Raw.StartsWith(ScheduledTaskParser.DisabledCronPrefix, StringComparison.Ordinal)
                ? target.Raw[ScheduledTaskParser.DisabledCronPrefix.Length..]
                : target.Raw;
        var candidate = ReplaceCronLine(raw, target.SourceLineIndex.Value, line);
        return await ApplyRawCrontabCoreAsync(profile, candidate, raw, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduledTaskMutationResult> DeleteAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Kind == ScheduledTaskKind.SystemdTimer)
        {
            return await DeleteSystemdTimerAsync(profile, task, cancellationToken).ConfigureAwait(false);
        }

        var expected = ScheduledTaskParser.NormalizeRawCron(expectedRawCrontab);
        var current = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || !current.Available)
        {
            var error = current.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "crontab is unavailable.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var raw = ScheduledTaskParser.NormalizeRawCron(current.Raw);
        if (!string.Equals(raw, expected, StringComparison.Ordinal))
        {
            return Conflict("The crontab changed after it was loaded. Refresh before deleting a task.");
        }

        var target = ScheduledTaskParser.ParseCrontab(raw)
            .SingleOrDefault(item => string.Equals(item.Id, task.Id, StringComparison.Ordinal));
        if (target?.SourceLineIndex is null)
        {
            return Conflict("The cron entry no longer exists. Refresh before retrying.");
        }

        var candidate = DeleteCronLine(raw, target.SourceLineIndex.Value);
        return await ApplyRawCrontabCoreAsync(profile, candidate, raw, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(
        ServerProfile profile,
        string candidate,
        string expectedRawCrontab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = ScheduledTaskParser.NormalizeRawCron(candidate);
        ValidateRawCron(normalized);
        return await ApplyRawCrontabCoreAsync(
                profile,
                normalized,
                ScheduledTaskParser.NormalizeRawCron(expectedRawCrontab),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ScheduledTaskMutationResult> ApplyRawCrontabCoreAsync(
        ServerProfile profile,
        string candidate,
        string expectedRaw,
        CancellationToken cancellationToken)
    {
        ValidateRawCron(candidate);
        var current = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || !current.Available)
        {
            var error = current.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "crontab is unavailable.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        if (!string.Equals(ScheduledTaskParser.NormalizeRawCron(current.Raw), expectedRaw, StringComparison.Ordinal))
        {
            return Conflict("The crontab changed after it was loaded. Refresh before applying the candidate.");
        }

        var temporary = RemotePath.Parse($"/tmp/serverdesk-crontab-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fileSystem = _fileSystemFactory.Create(profile))
            {
                await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var payload = Encoding.UTF8.GetBytes(candidate);
                await using var stream = new MemoryStream(payload, writable: false);
                await fileSystem.UploadAsync(stream, temporary, payload.Length, overwrite: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                await fileSystem.SetPermissionsAsync(temporary, RemoteUnixPermissions.FromMode(600), cancellationToken).ConfigureAwait(false);
            }

            await using var executor = _commandFactory.Create(profile);
            var validation = await executor.ExecuteAsync(
                    ReadOnly("crontab", ["-T", temporary.Value]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (validation.Error is not null)
            {
                return new ScheduledTaskMutationResult(false, validation.Error, validation.Error.Message);
            }

            if (validation.Command!.ExitCode != 0 &&
                !ScheduledTaskParser.LooksLikeUnsupportedCrontabValidation(validation.Command.StandardError))
            {
                var detail = FirstUseful(validation.Command.StandardError, validation.Command.StandardOutput, "crontab validation failed.");
                var error = ScheduledTaskParser.MapFailure(detail);
                return new ScheduledTaskMutationResult(false, error, error.Message);
            }

            var apply = await executor.ExecuteAsync(
                    Mutation("crontab", [temporary.Value], OperationRisk.Mutating),
                    cancellationToken)
                .ConfigureAwait(false);
            if (apply.Error is not null)
            {
                var error = AmbiguousMutation(apply.Error, "crontab apply");
                return new ScheduledTaskMutationResult(false, error, error.Message);
            }

            if (apply.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(apply.Command.StandardError, apply.Command.StandardOutput, "crontab apply failed.");
                var error = ScheduledTaskParser.MapFailure(detail);
                return new ScheduledTaskMutationResult(false, error, error.Message);
            }

            var verified = await ReadCrontabAsync(profile, cancellationToken).ConfigureAwait(false);
            if (verified.Error is not null ||
                !string.Equals(ScheduledTaskParser.NormalizeRawCron(verified.Raw), candidate, StringComparison.Ordinal))
            {
                var error = new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "crontab reported success, but ServerDesk could not verify the exact candidate content. Refresh before any retry.",
                    verified.Error?.Message);
                return new ScheduledTaskMutationResult(false, error, error.Message);
            }

            var snapshot = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            return new ScheduledTaskMutationResult(true, null, "Crontab candidate applied and verified.", snapshot.Snapshot);
        }
        catch (RemoteFileSystemException exception)
        {
            return new ScheduledTaskMutationResult(false, exception.Error, exception.Error.Message);
        }
        finally
        {
            await BestEffortDeleteAsync(profile, temporary).ConfigureAwait(false);
        }
    }

    private async Task<ScheduledTaskMutationResult> SetSystemdEnabledAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var unit = ScheduledTaskIdentity.NormalizeTimerUnit(task.Unit ?? task.Name);
        var verb = enabled ? "enable" : "disable";
        await using var executor = _commandFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                Mutation("systemctl", [verb, "--now", unit], OperationRisk.Mutating),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = AmbiguousMutation(execution.Error, $"systemctl {verb}");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var failure = Failure(execution, $"systemctl {verb} failed for '{unit}'.");
        if (failure is not null)
        {
            return new ScheduledTaskMutationResult(false, failure, failure.Message);
        }

        var verification = await ReadTimerAsync(profile, unit, enabled ? "enabled" : "disabled", cancellationToken).ConfigureAwait(false);
        if (verification.Error is not null || verification.Task is null ||
            verification.Task.Enabled != enabled || verification.Task.Active != enabled)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"systemctl {verb} returned success for '{unit}', but verified timer state did not match. Refresh before retrying.",
                verification.Error?.Message);
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var snapshot = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        return new ScheduledTaskMutationResult(true, null, $"systemd timer '{unit}' {verb} completed and verified.", snapshot.Snapshot);
    }

    private async Task<ScheduledTaskMutationResult> DeleteSystemdTimerAsync(
        ServerProfile profile,
        ScheduledTaskInfo task,
        CancellationToken cancellationToken)
    {
        var unit = ScheduledTaskIdentity.NormalizeTimerUnit(task.Unit ?? task.Name);
        var sourcePath = task.SourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !sourcePath.StartsWith("/etc/systemd/system/", StringComparison.Ordinal) ||
            !sourcePath.EndsWith(".timer", StringComparison.Ordinal))
        {
            var error = new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                "ServerDesk only deletes locally managed timer units under /etc/systemd/system. Packaged or generated timers must be handled explicitly in the terminal.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var disable = await SetSystemdEnabledAsync(profile, task, false, cancellationToken).ConfigureAwait(false);
        if (!disable.IsSuccess)
        {
            return disable;
        }

        var mutationStarted = true;
        try
        {
            await using var fileSystem = _fileSystemFactory.Create(profile);
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await fileSystem.DeleteFileAsync(RemotePath.Parse(sourcePath), cancellationToken).ConfigureAwait(false);

            var trigger = task.TriggerUnit?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(trigger) && trigger.EndsWith(".service", StringComparison.Ordinal) && !trigger.Contains('/'))
            {
                var servicePath = RemotePath.Parse("/etc/systemd/system/" + trigger);
                try
                {
                    await fileSystem.DeleteFileAsync(servicePath, cancellationToken).ConfigureAwait(false);
                }
                catch (RemoteFileSystemException exception) when (exception.Error.Code == RemoteErrorCode.PathNotFound)
                {
                }
            }
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Deletion of systemd timer '{unit}' was cancelled after mutation started. Refresh before retrying.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }
        catch (RemoteFileSystemException exception)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"systemd timer '{unit}' was disabled, but unit-file deletion could not be completed reliably. Refresh before retrying.",
                exception.Error.Message);
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        await using var executor = _commandFactory.Create(profile);
        var reload = await executor.ExecuteAsync(
                Mutation("systemctl", ["daemon-reload"], OperationRisk.Mutating),
                cancellationToken)
            .ConfigureAwait(false);
        if (reload.Error is not null || reload.Command!.ExitCode != 0)
        {
            var error = reload.Error is not null
                ? AmbiguousMutation(reload.Error, "systemctl daemon-reload")
                : new RemoteError(RemoteErrorCode.AmbiguousState, "Timer unit files were removed, but systemd daemon-reload failed. Refresh systemd state before retrying.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var show = await executor.ExecuteAsync(
                ReadOnly("systemctl", ["show", unit, "--no-pager", "--property=LoadState"]),
                cancellationToken)
            .ConfigureAwait(false);
        var absent = show.Error is null && show.Command!.ExitCode == 0 &&
                     show.Command.StandardOutput.Contains("LoadState=not-found", StringComparison.OrdinalIgnoreCase);
        if (!absent)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"Timer files for '{unit}' were removed, but systemd still reports the unit as present. Refresh before retrying.");
            return new ScheduledTaskMutationResult(false, error, error.Message);
        }

        var snapshot = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        return new ScheduledTaskMutationResult(true, null, $"systemd timer '{unit}' was deleted and verified absent.", snapshot.Snapshot);
    }

    private async Task<(bool Available, string Raw, RemoteError? Error)> ReadCrontabAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        var execution = await executor.ExecuteAsync(ReadOnly("crontab", ["-l"]), cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return execution.Error.Code == RemoteErrorCode.CommandNotFound
                ? (false, string.Empty, null)
                : (false, string.Empty, execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return (true, ScheduledTaskParser.NormalizeRawCron(command.StandardOutput), null);
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, "Unable to read crontab.");
        if (detail.Contains("no crontab", StringComparison.OrdinalIgnoreCase))
        {
            return (true, string.Empty, null);
        }

        var error = ScheduledTaskParser.MapFailure(detail);
        return error.Code == RemoteErrorCode.CommandNotFound
            ? (false, string.Empty, null)
            : (false, string.Empty, error);
    }

    private async Task<(bool Available, IReadOnlyList<ScheduledTaskInfo> Tasks, RemoteError? Error)> ReadTimersAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        var list = await executor.ExecuteAsync(
                ReadOnly("systemctl", ["list-unit-files", "--type=timer", "--no-legend", "--no-pager"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (list.Error is not null)
        {
            return list.Error.Code == RemoteErrorCode.CommandNotFound
                ? (false, [], null)
                : (false, [], list.Error);
        }

        var listFailure = Failure(list, "Unable to list systemd timer unit files.");
        if (listFailure is not null)
        {
            if (listFailure.Message.Contains("not been booted with systemd", StringComparison.OrdinalIgnoreCase) ||
                listFailure.Message.Contains("failed to connect to bus", StringComparison.OrdinalIgnoreCase))
            {
                return (false, [], null);
            }

            return (false, [], listFailure);
        }

        IReadOnlyList<(string Unit, string State)> unitFiles;
        try
        {
            unitFiles = ScheduledTaskParser.ParseTimerUnitFiles(list.Command!.StandardOutput, _options.MaximumTimers);
        }
        catch (FormatException exception)
        {
            return (true, [], new RemoteError(RemoteErrorCode.ParseFailed, "ServerDesk could not parse systemd timer inventory.", exception.Message));
        }

        var tasks = new List<ScheduledTaskInfo>(unitFiles.Count);
        foreach (var (unit, state) in unitFiles)
        {
            var detail = await ReadTimerWithExecutorAsync(executor, unit, state, cancellationToken).ConfigureAwait(false);
            if (detail.Error is null && detail.Task is not null)
            {
                tasks.Add(detail.Task);
            }
        }

        return (true, tasks, null);
    }

    private async Task<(ScheduledTaskInfo? Task, RemoteError? Error)> ReadTimerAsync(
        ServerProfile profile,
        string unit,
        string unitFileState,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        return await ReadTimerWithExecutorAsync(executor, unit, unitFileState, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(ScheduledTaskInfo? Task, RemoteError? Error)> ReadTimerWithExecutorAsync(
        IRemoteCommandExecutor executor,
        string unit,
        string unitFileState,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                ReadOnly(
                    "systemctl",
                    [
                        "show", unit, "--no-pager",
                        "--property=Id,LoadState,ActiveState,UnitFileState,NextElapseUSecRealtime,LastTriggerUSec,TimersCalendar,Triggers,FragmentPath",
                    ]),
                cancellationToken)
            .ConfigureAwait(false);
        var failure = Failure(execution, $"Unable to inspect systemd timer '{unit}'.");
        if (failure is not null)
        {
            return (null, failure);
        }

        try
        {
            return (ScheduledTaskParser.ParseTimerShow(unit, unitFileState, execution.Command!.StandardOutput), null);
        }
        catch (FormatException exception)
        {
            return (null, new RemoteError(RemoteErrorCode.ParseFailed, $"Unable to parse systemd timer '{unit}'.", exception.Message));
        }
    }

    private async Task BestEffortDeleteAsync(ServerProfile profile, RemotePath path)
    {
        try
        {
            await using var fileSystem = _fileSystemFactory.Create(profile);
            await fileSystem.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void ValidateRawCron(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > _options.MaximumRawCronBytes || value.Contains('\0'))
        {
            throw new FormatException($"Raw crontab must be at most {_options.MaximumRawCronBytes} UTF-8 bytes and cannot contain NUL characters.");
        }
    }

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private RemoteCommandSpec Mutation(string executable, IReadOnlyList<string> arguments, OperationRisk risk) =>
        new(executable, arguments, _options.CommandTimeout, risk, StableEnvironment);

    private static RemoteError? Failure(RemoteExecutionResult execution, string fallback)
    {
        if (execution.Error is not null)
        {
            return execution.Error;
        }

        return execution.Command!.ExitCode == 0
            ? null
            : ScheduledTaskParser.MapFailure(FirstUseful(execution.Command.StandardError, execution.Command.StandardOutput, fallback));
    }

    private static RemoteError AmbiguousMutation(RemoteError error, string operation) =>
        error.Code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled
            ? new RemoteError(RemoteErrorCode.AmbiguousState, $"ServerDesk lost a reliable completion signal during {operation}. Refresh state before deciding whether to retry.", error.TechnicalDetails)
            : error;

    private static ScheduledTaskMutationResult Conflict(string message)
    {
        var error = new RemoteError(RemoteErrorCode.PathConflict, message);
        return new ScheduledTaskMutationResult(false, error, message);
    }

    private static string AppendCronLine(string raw, string line) =>
        ScheduledTaskParser.NormalizeRawCron(raw + line + "\n");

    private static string ReplaceCronLine(string raw, int lineIndex, string replacement)
    {
        var lines = ScheduledTaskParser.NormalizeRawCron(raw).TrimEnd('\n').Split('\n').ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            throw new FormatException("Cron source line index is out of range.");
        }

        lines[lineIndex] = replacement;
        return ScheduledTaskParser.NormalizeRawCron(string.Join('\n', lines));
    }

    private static string DeleteCronLine(string raw, int lineIndex)
    {
        var normalized = ScheduledTaskParser.NormalizeRawCron(raw);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var lines = normalized.TrimEnd('\n').Split('\n').ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            throw new FormatException("Cron source line index is out of range.");
        }

        lines.RemoveAt(lineIndex);
        return lines.Count == 0 ? string.Empty : ScheduledTaskParser.NormalizeRawCron(string.Join('\n', lines));
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return ScheduledTaskParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second) ? ScheduledTaskParser.Sanitize(second.Trim()) : fallback;
    }
}

public sealed class AuditedScheduledTaskService : IScheduledTaskService
{
    private readonly IScheduledTaskService _inner;
    private readonly IOperationAudit _audit;

    public AuditedScheduledTaskService(IScheduledTaskService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<ScheduledTaskSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public Task<ScheduledTaskTextResult> ReadHistoryAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(profile, task, cancellationToken);

    public Task<ScheduledTaskTextResult> ReadRawSourceAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) =>
        _inner.ReadRawSourceAsync(profile, task, cancellationToken);

    public Task<ScheduledTaskMutationResult> SaveCronAsync(ServerProfile profile, CronTaskDraft draft, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
        AuditAsync(
            profile,
            draft.ExistingTaskId is null ? "create-cron" : "update-cron",
            draft.ExistingTaskId ?? "new-cron",
            OperationRisk.Mutating,
            token => _inner.SaveCronAsync(profile, draft, expectedRawCrontab, token),
            cancellationToken);

    public Task<ScheduledTaskMutationResult> SetEnabledAsync(ServerProfile profile, ScheduledTaskInfo task, bool enabled, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
        AuditAsync(
            profile,
            enabled ? "enable" : "disable",
            task.Id,
            OperationRisk.Mutating,
            token => _inner.SetEnabledAsync(profile, task, enabled, expectedRawCrontab, token),
            cancellationToken);

    public Task<ScheduledTaskMutationResult> DeleteAsync(ServerProfile profile, ScheduledTaskInfo task, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
        AuditAsync(
            profile,
            "delete",
            task.Id,
            OperationRisk.Destructive,
            token => _inner.DeleteAsync(profile, task, expectedRawCrontab, token),
            cancellationToken);

    public Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(ServerProfile profile, string candidate, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
        AuditAsync(
            profile,
            "apply-raw-crontab",
            "user-crontab",
            OperationRisk.Mutating,
            token => _inner.ApplyRawCrontabAsync(profile, candidate, expectedRawCrontab, token),
            cancellationToken);

    private async Task<ScheduledTaskMutationResult> AuditAsync(
        ServerProfile profile,
        string operation,
        string target,
        OperationRisk risk,
        Func<CancellationToken, Task<ScheduledTaskMutationResult>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await execute(cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            await TryAuditAsync(profile, operation, target, risk, outcome, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryAuditAsync(profile, operation, target, risk, OperationOutcome.Cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask TryAuditAsync(
        ServerProfile profile,
        string operation,
        string target,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.AppendAsync(
                    OperationAuditEntry.Create(
                        "scheduled-task",
                        $"Scheduled task {operation} requested for {target}",
                        risk,
                        outcome,
                        $"{profile.Username}@{profile.Host}:{profile.Port} task:{target}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Audit persistence failure must never trigger a scheduled-task mutation retry.
        }
    }
}
