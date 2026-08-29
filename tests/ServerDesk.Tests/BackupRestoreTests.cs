using ServerDesk.Application.Backups;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class BackupRestoreTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void NormalizeRejectsRelativeTraversalAndRootTargets()
    {
        Assert.Throws<ArgumentException>(() => BackupRestoreService.NormalizeCreateRequest(new("etc/nginx/nginx.conf", "/tmp/backups")));
        Assert.Throws<ArgumentException>(() => BackupRestoreService.NormalizeCreateRequest(new("/etc/nginx/../passwd", "/tmp/backups")));
        Assert.Throws<ArgumentException>(() => BackupRestoreService.NormalizeCreateRequest(new("/", "/tmp/backups")));
    }

    [Fact]
    public async Task BackupMustVerifyBeforeExactTargetRestoreIsAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeRemoteState();
        state.SetDirectory("/backups", 1000, 1000, 755);
        state.SetFile("/etc/app.conf", 12, 0, 0, 640, HashA);
        var profile = Profile();
        var service = new BackupRestoreService(new FakeFactory(state), BackupRestoreOptions.Default);

        var created = await service.CreateBackupAsync(
            profile,
            new BackupCreateRequest("/etc/app.conf", "/backups"),
            cancellationToken);

        Assert.True(created.IsSuccess, created.Error?.Message);
        var manifest = Assert.IsType<BackupManifest>(created.Manifest);
        Assert.True(manifest.IsVerified);
        Assert.Equal(HashA, manifest.Sha256);
        Assert.Equal((short)640, manifest.Permissions.Mode);

        state.SetFile("/etc/app.conf", 8, 0, 0, 600, HashB);
        var previewResult = await service.PreviewRestoreAsync(profile, manifest, cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<RestorePreview>(previewResult.Preview);
        Assert.Equal("/etc/app.conf", preview.Impact.ExactOverwriteTarget.Value);
        Assert.False(preview.Impact.RollbackAvailable);
        Assert.Contains("exactly", preview.Impact.Message, StringComparison.OrdinalIgnoreCase);

        var restored = await service.ExecuteRestoreAsync(profile, preview, cancellationToken);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        var target = state.Get("/etc/app.conf");
        Assert.Equal(HashA, target.Hash);
        Assert.Equal(12, target.Size);
        Assert.Equal((short)640, target.Mode);
        Assert.Equal(0, target.UserId);
        Assert.Equal(0, target.GroupId);
    }

    [Fact]
    public async Task CorruptedCopyIsNeverMarkedVerifiedOrSafeForRestore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new FakeRemoteState { CorruptNextInstall = true };
        state.SetDirectory("/backups", 1000, 1000, 755);
        state.SetFile("/etc/app.conf", 12, 0, 0, 640, HashA);
        var service = new BackupRestoreService(new FakeFactory(state), BackupRestoreOptions.Default);

        var result = await service.CreateBackupAsync(
            Profile(),
            new BackupCreateRequest("/etc/app.conf", "/backups"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Manifest);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
    }

    private static ServerProfile Profile() =>
        ServerProfile.Create(Guid.NewGuid(), "Backup fixture", "localhost", 22, "operator");

    private sealed class FakeFactory : IRemoteCommandExecutorFactory
    {
        private readonly FakeRemoteState _state;

        public FakeFactory(FakeRemoteState state) => _state = state;

        public IRemoteCommandExecutor Create(ServerProfile profile) => new FakeExecutor(profile.Id, _state);
    }

    private sealed class FakeExecutor : IRemoteCommandExecutor
    {
        private readonly FakeRemoteState _state;

        public FakeExecutor(Guid profileId, FakeRemoteState state)
        {
            ServerProfileId = profileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Executable != "sudo" || command.Arguments.Count < 2 || command.Arguments[0] != "-n")
            {
                throw new InvalidOperationException($"Unexpected executable: {command.Executable}");
            }

            var verb = command.Arguments[1];
            return Task.FromResult(verb switch
            {
                "stat" => Stat(command.Arguments[^1]),
                "sha256sum" => Hash(command.Arguments[^1]),
                "install" => Install(command.Arguments),
                "mv" => Move(command.Arguments[^2], command.Arguments[^1]),
                "rm" => Remove(command.Arguments[^1]),
                _ => throw new InvalidOperationException($"Unexpected fake command: {string.Join(' ', command.Arguments)}"),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private RemoteExecutionResult Stat(string path)
        {
            if (!_state.TryGet(path, out var node))
            {
                return Command(1, string.Empty, $"stat: cannot statx '{path}': No such file or directory");
            }

            return Command(0, $"{node.Kind}\t{node.Size}\t{node.UserId}\t{node.GroupId}\t{node.Mode}", string.Empty);
        }

        private RemoteExecutionResult Hash(string path)
        {
            if (!_state.TryGet(path, out var node) || node.Kind != "regular file")
            {
                return Command(1, string.Empty, "sha256sum: No such file or directory");
            }

            return Command(0, $"{node.Hash}  {path}\n", string.Empty);
        }

        private RemoteExecutionResult Install(IReadOnlyList<string> arguments)
        {
            var source = arguments[^2];
            var destination = arguments[^1];
            var sourceNode = _state.Get(source);
            var mode = short.Parse(arguments[arguments.IndexOf("-m") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var uid = int.Parse(arguments[arguments.IndexOf("-o") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var gid = int.Parse(arguments[arguments.IndexOf("-g") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var hash = _state.CorruptNextInstall ? HashB : sourceNode.Hash;
            _state.CorruptNextInstall = false;
            _state.SetFile(destination, sourceNode.Size, uid, gid, mode, hash);
            return Command(0, string.Empty, string.Empty);
        }

        private RemoteExecutionResult Move(string source, string destination)
        {
            var node = _state.Get(source);
            _state.Set(destination, node);
            _state.Remove(source);
            return Command(0, string.Empty, string.Empty);
        }

        private RemoteExecutionResult Remove(string path)
        {
            _state.Remove(path);
            return Command(0, string.Empty, string.Empty);
        }

        private static RemoteExecutionResult Command(int exitCode, string output, string error) =>
            RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, output, error, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class FakeRemoteState
    {
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

        public bool CorruptNextInstall { get; set; }

        public void SetDirectory(string path, int uid, int gid, short mode) =>
            _nodes[path] = new Node("directory", 0, uid, gid, mode, string.Empty);

        public void SetFile(string path, long size, int uid, int gid, short mode, string hash) =>
            _nodes[path] = new Node("regular file", size, uid, gid, mode, hash);

        public void Set(string path, Node node) => _nodes[path] = node;

        public Node Get(string path) => _nodes[path];

        public bool TryGet(string path, out Node node) => _nodes.TryGetValue(path, out node!);

        public void Remove(string path) => _nodes.Remove(path);
    }

    private sealed record Node(string Kind, long Size, int UserId, int GroupId, short Mode, string Hash);
}

internal static class BackupRestoreTestListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
