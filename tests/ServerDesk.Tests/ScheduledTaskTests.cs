using System.Text;
using System.Text.Json;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.ScheduledTasks;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ScheduledTaskTests
{
    [Theory]
    [InlineData("ubuntu-24.04.json")]
    [InlineData("ubuntu-26.04.json")]
    [InlineData("debian-13.json")]
    public void CertifiedFixturesNormalizeCronAndSystemdTimers(string fixtureName)
    {
        using var document = JsonDocument.Parse(ReadFixture(fixtureName));
        var root = document.RootElement;
        var cron = ScheduledTaskParser.ParseCrontab(root.GetProperty("crontab").GetString()!);
        var units = ScheduledTaskParser.ParseTimerUnitFiles(root.GetProperty("unitFiles").GetString()!, 100);
        var timerUnit = root.GetProperty("timerShow").GetString()!
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("Id=", StringComparison.Ordinal))[3..];
        var unitState = units.Single(unit => unit.Unit == timerUnit).State;
        var timer = ScheduledTaskParser.ParseTimerShow(timerUnit, unitState, root.GetProperty("timerShow").GetString()!);

        Assert.NotEmpty(cron);
        Assert.NotEmpty(units);
        Assert.Equal(ScheduledTaskKind.SystemdTimer, timer.Kind);
        Assert.Equal(timerUnit, timer.Unit);
        Assert.False(string.IsNullOrWhiteSpace(timer.Schedule));
    }

    [Fact]
    public void CronParserPreservesDisabledAndAdvancedEntries()
    {
        const string raw = "MAILTO=ops@example.invalid\n# keep me\n*/5 * * * * /srv/check\n# serverdesk-disabled 0 2 * * * /srv/nightly\n@reboot /srv/bootstrap\n";
        var tasks = ScheduledTaskParser.ParseCrontab(raw);

        Assert.Equal(3, tasks.Count);
        Assert.True(tasks[0].Enabled);
        Assert.True(tasks[0].IsSimpleEditable);
        Assert.False(tasks[1].Enabled);
        Assert.True(tasks[1].IsSimpleEditable);
        Assert.Equal("@reboot", tasks[2].Schedule);
        Assert.False(tasks[2].IsSimpleEditable);
    }

    [Fact]
    public async Task RawCrontabApplyStagesValidatesAppliesAndVerifiesExactContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState { RawCrontab = "0 1 * * * /old\n" };
        var service = CreateService(state);
        var profile = Profile();

        var result = await service.ApplyRawCrontabAsync(
            profile,
            "MAILTO=ops@example.invalid\n0 2 * * * /new\n@reboot /bootstrap\n",
            state.RawCrontab,
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("MAILTO=ops@example.invalid\n0 2 * * * /new\n@reboot /bootstrap\n", state.RawCrontab);
        Assert.Contains(state.Commands, command => command.Executable == "crontab" && command.Arguments.Count == 2 && command.Arguments[0] == "-T");
        var apply = Assert.Single(state.Commands, command => command.Executable == "crontab" && command.Arguments.Count == 1 && command.Arguments[0].StartsWith("/tmp/serverdesk-crontab-", StringComparison.Ordinal));
        Assert.Equal(OperationRisk.Mutating, apply.Risk);
        Assert.DoesNotContain(state.Commands, command => command.Executable is "sh" or "bash");
    }

    [Fact]
    public async Task ChangedCrontabBlocksMutationBeforeUpload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState { RawCrontab = "0 3 * * * /current\n" };
        var service = CreateService(state);

        var result = await service.ApplyRawCrontabAsync(Profile(), "0 4 * * * /new\n", "0 2 * * * /stale\n", cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Empty(state.TempFiles);
        Assert.DoesNotContain(state.Commands, command => command.Risk != OperationRisk.ReadOnly);
    }

    [Fact]
    public async Task CronMutationTransportDropIsAmbiguousAndNotRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState
        {
            RawCrontab = "0 1 * * * /old\n",
            FailCronApply = new RemoteError(RemoteErrorCode.NetworkInterrupted, "channel dropped"),
        };
        var service = CreateService(state);

        var result = await service.ApplyRawCrontabAsync(Profile(), "0 2 * * * /new\n", state.RawCrontab, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(state.Commands, command => command.Executable == "crontab" && command.Arguments.Count == 1 && command.Risk == OperationRisk.Mutating);
    }

    [Fact]
    public async Task SimpleCronUpdatePreservesEnvironmentCommentsAndAdvancedSyntax()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState
        {
            RawCrontab = "MAILTO=ops@example.invalid\n# keep this comment\n0 1 * * * /old\n@reboot /bootstrap\n",
        };
        var service = CreateService(state);
        var task = ScheduledTaskParser.ParseCrontab(state.RawCrontab).Single(item => item.CommandOrUnit == "/old");
        var draft = new CronTaskDraft(task.Id, "30", "2", "*", "*", "*", "/new", true);

        var result = await service.SaveCronAsync(Profile(), draft, state.RawCrontab, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains("MAILTO=ops@example.invalid\n", state.RawCrontab, StringComparison.Ordinal);
        Assert.Contains("# keep this comment\n", state.RawCrontab, StringComparison.Ordinal);
        Assert.Contains("@reboot /bootstrap\n", state.RawCrontab, StringComparison.Ordinal);
        Assert.Contains("30 2 * * * /new\n", state.RawCrontab, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemdEnableVerifiesActualUnitFileAndActiveState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState
        {
            RawCrontab = string.Empty,
            TimerEnabled = false,
            TimerActive = false,
        };
        var service = CreateService(state);
        var task = TimerTask(state);

        var result = await service.SetEnabledAsync(Profile(), task, true, string.Empty, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(state.TimerEnabled);
        Assert.True(state.TimerActive);
        var command = Assert.Single(state.Commands, item => item.Executable == "systemctl" && item.Arguments.Contains("enable"));
        Assert.Equal(["enable", "--now", "demo.timer"], command.Arguments);
        Assert.Equal(OperationRisk.Mutating, command.Risk);
    }

    [Fact]
    public async Task PackagedSystemdTimerDeleteIsRefusedBeforeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeState();
        var service = CreateService(state);
        var task = TimerTask(state) with { SourcePath = "/usr/lib/systemd/system/demo.timer" };

        var result = await service.DeleteAsync(Profile(), task, string.Empty, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.CapabilityUnavailable, result.Error?.Code);
        Assert.DoesNotContain(state.Commands, command => command.Risk != OperationRisk.ReadOnly);
    }

    [Fact]
    public async Task AuditedDeleteIsDestructiveAndSecretSafe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var audit = new RecordingAudit();
        var inner = new StubService(new ScheduledTaskMutationResult(false, new RemoteError(RemoteErrorCode.AmbiguousState, "unknown"), "unknown"));
        var service = new AuditedScheduledTaskService(inner, audit);
        var task = new ScheduledTaskInfo("cron:1:ABC", ScheduledTaskKind.Cron, "task", "0 1 * * *", "/job", true, null, null, null, true, "0 1 * * * /job", 1);

        await service.DeleteAsync(Profile(), task, "", cancellationToken);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("scheduled-task", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.DoesNotContain("password", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static ScheduledTaskService CreateService(FakeState state) =>
        new(new FakeCommandFactory(state), new FakeFileSystemFactory(state), ScheduledTaskOptions.Default);

    private static ServerProfile Profile() => ServerProfile.Create("Tasks", "example.invalid", 22, "dev");

    private static ScheduledTaskInfo TimerTask(FakeState state) =>
        ScheduledTaskParser.ParseTimerShow(
            "demo.timer",
            state.TimerEnabled ? "enabled" : "disabled",
            state.TimerShow());

    private static string ReadFixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScheduledTasks", file));

    private static RemoteExecutionResult Success(string output = "", string error = "", int exitCode = 0) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, output, error, TimeSpan.Zero));

    private sealed class FakeState
    {
        public string RawCrontab { get; set; } = string.Empty;
        public Dictionary<string, byte[]> TempFiles { get; } = new(StringComparer.Ordinal);
        public List<RemoteCommandSpec> Commands { get; } = [];
        public RemoteError? FailCronApply { get; set; }
        public bool TimerEnabled { get; set; }
        public bool TimerActive { get; set; }

        public string TimerShow() =>
            $"Id=demo.timer\nLoadState=loaded\nActiveState={(TimerActive ? "active" : "inactive")}\nUnitFileState={(TimerEnabled ? "enabled" : "disabled")}\nNextElapseUSecRealtime=Sat 2026-08-29 07:00:00 UTC\nLastTriggerUSec=Fri 2026-08-28 07:00:00 UTC\nTimersCalendar={{ OnCalendar=*-*-* 07:00:00 }}\nTriggers=demo.service\nFragmentPath=/etc/systemd/system/demo.timer\n";
    }

    private sealed class FakeCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly FakeState _state;
        public FakeCommandFactory(FakeState state) => _state = state;
        public IRemoteCommandExecutor Create(ServerProfile profile) => new FakeCommandExecutor(profile.Id, _state);
    }

    private sealed class FakeCommandExecutor : IRemoteCommandExecutor
    {
        private readonly FakeState _state;
        public FakeCommandExecutor(Guid id, FakeState state) { ServerProfileId = id; _state = state; }
        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Commands.Add(command);
            if (command.Executable == "crontab")
            {
                if (command.Arguments.SequenceEqual(["-l"]))
                {
                    return Task.FromResult(Success(_state.RawCrontab));
                }

                if (command.Arguments.Count == 2 && command.Arguments[0] == "-T")
                {
                    return Task.FromResult(Success());
                }

                if (command.Arguments.Count == 1)
                {
                    if (_state.FailCronApply is not null)
                    {
                        return Task.FromResult(RemoteExecutionResult.Failure(_state.FailCronApply));
                    }

                    _state.RawCrontab = Encoding.UTF8.GetString(_state.TempFiles[command.Arguments[0]]);
                    return Task.FromResult(Success());
                }
            }

            if (command.Executable == "systemctl")
            {
                if (command.Arguments.Contains("list-unit-files"))
                {
                    return Task.FromResult(Success($"demo.timer {(_state.TimerEnabled ? "enabled" : "disabled")} enabled\n"));
                }

                if (command.Arguments.Contains("show"))
                {
                    return Task.FromResult(Success(_state.TimerShow()));
                }

                if (command.Arguments.Contains("enable"))
                {
                    _state.TimerEnabled = true;
                    _state.TimerActive = true;
                    return Task.FromResult(Success());
                }

                if (command.Arguments.Contains("disable"))
                {
                    _state.TimerEnabled = false;
                    _state.TimerActive = false;
                    return Task.FromResult(Success());
                }
            }

            if (command.Executable == "journalctl")
            {
                return Task.FromResult(Success("one\ntwo\n"));
            }

            return Task.FromResult(Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFileSystemFactory : IRemoteFileSystemFactory
    {
        private readonly FakeState _state;
        public FakeFileSystemFactory(FakeState state) => _state = state;
        public IRemoteFileSystem Create(ServerProfile profile) => new FakeFileSystem(profile.Id, _state);
    }

    private sealed class FakeFileSystem : IRemoteFileSystem
    {
        private readonly FakeState _state;
        public FakeFileSystem(Guid id, FakeState state) { ServerProfileId = id; _state = state; }
        public Guid ServerProfileId { get; }
        public bool IsConnected { get; private set; }
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) { IsConnected = true; return ValueTask.CompletedTask; }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; return ValueTask.CompletedTask; }
        public ValueTask UploadAsync(Stream source, RemotePath destination, long? totalBytes = null, bool overwrite = false, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            _state.TempFiles[destination.Value] = memory.ToArray();
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default) { _state.TempFiles.Remove(path.Value); return ValueTask.CompletedTask; }
        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];
        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class StubService : IScheduledTaskService
    {
        private readonly ScheduledTaskMutationResult _result;
        public StubService(ScheduledTaskMutationResult result) => _result = result;
        public Task<ScheduledTaskSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskSnapshotResult(null, new RemoteError(RemoteErrorCode.CommandFailed, "unused")));
        public Task<ScheduledTaskTextResult> ReadHistoryAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskTextResult("", null));
        public Task<ScheduledTaskTextResult> ReadRawSourceAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) => Task.FromResult(new ScheduledTaskTextResult("", null));
        public Task<ScheduledTaskMutationResult> SaveCronAsync(ServerProfile profile, CronTaskDraft draft, string expectedRawCrontab, CancellationToken cancellationToken = default) => Task.FromResult(_result);
        public Task<ScheduledTaskMutationResult> SetEnabledAsync(ServerProfile profile, ScheduledTaskInfo task, bool enabled, string expectedRawCrontab, CancellationToken cancellationToken = default) => Task.FromResult(_result);
        public Task<ScheduledTaskMutationResult> DeleteAsync(ServerProfile profile, ScheduledTaskInfo task, string expectedRawCrontab, CancellationToken cancellationToken = default) => Task.FromResult(_result);
        public Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(ServerProfile profile, string candidate, string expectedRawCrontab, CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }
}
