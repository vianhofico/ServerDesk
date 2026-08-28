using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.ScheduledTasks;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class GuardedScheduledTaskServiceTests
{
    [Fact]
    public async Task SystemdDeleteRemovesOnlySelectedTimerFileAndLeavesTriggerService()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new InnerStub();
        var commands = new RecordingCommandFactory();
        var files = new RecordingFileSystemFactory();
        var service = new GuardedScheduledTaskService(inner, commands, files, ScheduledTaskOptions.Default);
        var task = TimerTask("/etc/systemd/system/demo.timer");

        var result = await service.DeleteAsync(Profile(), task, string.Empty, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(["/etc/systemd/system/demo.timer"], files.DeletedPaths);
        Assert.DoesNotContain("/etc/systemd/system/demo.service", files.DeletedPaths);
        Assert.Equal(1, inner.SetEnabledCalls);
        Assert.Equal(1, inner.InspectCalls);
        Assert.Contains(
            commands.Commands,
            command => command.Executable == "systemctl" &&
                       command.Arguments.SequenceEqual(["daemon-reload"]) &&
                       command.Risk == OperationRisk.Mutating);
        Assert.Contains(
            commands.Commands,
            command => command.Executable == "systemctl" &&
                       command.Arguments.SequenceEqual(["show", "demo.timer", "--no-pager", "--property=LoadState"]) &&
                       command.Risk == OperationRisk.ReadOnly);
    }

    [Fact]
    public async Task SystemdDeleteRejectsPathThatDoesNotExactlyMatchSelectedUnit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new InnerStub();
        var commands = new RecordingCommandFactory();
        var files = new RecordingFileSystemFactory();
        var service = new GuardedScheduledTaskService(inner, commands, files, ScheduledTaskOptions.Default);
        var task = TimerTask("/etc/systemd/system/other.timer");

        var result = await service.DeleteAsync(Profile(), task, string.Empty, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.CapabilityUnavailable, result.Error?.Code);
        Assert.Equal(0, inner.SetEnabledCalls);
        Assert.Empty(files.DeletedPaths);
        Assert.Empty(commands.Commands);
    }

    private static ScheduledTaskInfo TimerTask(string sourcePath) =>
        new(
            "systemd:demo.timer",
            ScheduledTaskKind.SystemdTimer,
            "demo.timer",
            "*-*-* 07:00:00",
            "demo.service",
            true,
            true,
            null,
            null,
            false,
            string.Empty,
            Unit: "demo.timer",
            SourcePath: sourcePath,
            TriggerUnit: "demo.service");

    private static ServerProfile Profile() =>
        ServerProfile.Create("Tasks", "example.invalid", 22, "dev");

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private sealed class InnerStub : IScheduledTaskService
    {
        private readonly ScheduledTaskSnapshot _snapshot = new([], string.Empty, true, true, []);

        public int SetEnabledCalls { get; private set; }
        public int InspectCalls { get; private set; }

        public Task<ScheduledTaskSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            InspectCalls++;
            return Task.FromResult(new ScheduledTaskSnapshotResult(_snapshot, null));
        }

        public Task<ScheduledTaskMutationResult> SetEnabledAsync(ServerProfile profile, ScheduledTaskInfo task, bool enabled, string expectedRawCrontab, CancellationToken cancellationToken = default)
        {
            SetEnabledCalls++;
            Assert.False(enabled);
            return Task.FromResult(new ScheduledTaskMutationResult(true, null, "disabled", _snapshot));
        }

        public Task<ScheduledTaskSnapshotResult> UnusedSnapshot() =>
            Task.FromResult(new ScheduledTaskSnapshotResult(_snapshot, null));

        public Task<ScheduledTaskTextResult> ReadHistoryAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduledTaskTextResult(string.Empty, null));

        public Task<ScheduledTaskTextResult> ReadRawSourceAsync(ServerProfile profile, ScheduledTaskInfo task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduledTaskTextResult(string.Empty, null));

        public Task<ScheduledTaskMutationResult> SaveCronAsync(ServerProfile profile, CronTaskDraft draft, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduledTaskMutationResult(true, null, "unused", _snapshot));

        public Task<ScheduledTaskMutationResult> DeleteAsync(ServerProfile profile, ScheduledTaskInfo task, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduledTaskMutationResult(true, null, "unused", _snapshot));

        public Task<ScheduledTaskMutationResult> ApplyRawCrontabAsync(ServerProfile profile, string candidate, string expectedRawCrontab, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduledTaskMutationResult(true, null, "unused", _snapshot));
    }

    private sealed class RecordingCommandFactory : IRemoteCommandExecutorFactory
    {
        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new RecordingCommandExecutor(profile.Id, Commands);
    }

    private sealed class RecordingCommandExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;

        public RecordingCommandExecutor(Guid serverProfileId, List<RemoteCommandSpec> commands)
        {
            ServerProfileId = serverProfileId;
            _commands = commands;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(
                command.Arguments.Count > 0 && command.Arguments[0] == "show"
                    ? Success("LoadState=not-found\n")
                    : Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingFileSystemFactory : IRemoteFileSystemFactory
    {
        public List<string> DeletedPaths { get; } = [];

        public IRemoteFileSystem Create(ServerProfile profile) =>
            new RecordingFileSystem(profile.Id, DeletedPaths);
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        private readonly List<string> _deletedPaths;

        public RecordingFileSystem(Guid serverProfileId, List<string> deletedPaths)
        {
            ServerProfileId = serverProfileId;
            _deletedPaths = deletedPaths;
        }

        public Guid ServerProfileId { get; }
        public bool IsConnected { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deletedPaths.Add(path.Value);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask UploadAsync(Stream source, RemotePath destination, long? totalBytes = null, bool overwrite = false, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
