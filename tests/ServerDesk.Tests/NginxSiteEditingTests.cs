using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class NginxSiteEditingTests
{
    [Fact]
    public void SimplePatchPreservesUnsupportedDirectivesAndOtherServerBlocks()
    {
        const string source = """
            server {
                listen 80;
                server_name old.example.test;
                add_header X-Keep yes;
                location / {
                    proxy_set_header Host $host;
                    proxy_pass http://127.0.0.1:5000;
                }
            }

            server {
                listen 8080;
                server_name untouched.example.test;
                location / { proxy_pass http://127.0.0.1:9000; }
            }
            """;

        var result = NginxSimpleSiteEditor.Apply(
            source,
            0,
            new NginxSimpleSitePatch(["app.example.test", "www.example.test"], "443 ssl", "http://127.0.0.1:7000"));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains("server_name app.example.test www.example.test;", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("listen 443 ssl;", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("proxy_pass http://127.0.0.1:7000;", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("add_header X-Keep yes;", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("proxy_set_header Host $host;", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("untouched.example.test", result.CandidateText, StringComparison.Ordinal);
        Assert.Contains("proxy_pass http://127.0.0.1:9000;", result.CandidateText, StringComparison.Ordinal);
    }

    [Fact]
    public void SimplePatchRefusesAmbiguousAdvancedLayout()
    {
        const string source = """
            server {
                listen 80;
                server_name app.example.test;
                location /api { proxy_pass http://127.0.0.1:5000; }
                location /admin { proxy_pass http://127.0.0.1:5001; }
            }
            """;

        var result = NginxSimpleSiteEditor.Apply(
            source,
            0,
            new NginxSimpleSitePatch(["new.example.test"], "80", "http://127.0.0.1:7000"));

        Assert.False(result.IsSuccess);
        Assert.Equal(source, result.CandidateText);
        Assert.Contains("raw mode", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadResolvesSymlinkTargetBeforeOpeningEditor()
    {
        var state = State.Create();
        var service = CreateService(state);

        var result = await service.LoadAsync(Profile(), RemotePath.Parse("/etc/nginx/sites-enabled/app"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("/etc/nginx/sites-available/app", result.Document!.CanonicalPath.Value);
        Assert.Equal("/etc/nginx/sites-available/app", state.Editor.LastLoadedPath?.Value);
        var command = Assert.Single(state.Commands);
        Assert.Equal("readlink", command.Executable);
        Assert.Equal(["-f", "--", "/etc/nginx/sites-enabled/app"], command.Arguments);
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
    }

    [Fact]
    public async Task InvalidCandidateNeverReplacesLiveFileOrCreatesBackup()
    {
        var state = State.Create();
        state.ValidationExitCode = 1;
        var service = CreateService(state);
        var loaded = await service.LoadAsync(Profile(), RemotePath.Parse("/etc/nginx/sites-enabled/app"), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(Profile(), loaded.Document!, "server { INVALID; }", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.ValidationFailed);
        Assert.Equal(0, state.Editor.SaveCalls);
        Assert.DoesNotContain(state.Commands, command =>
            command.Arguments.Contains("/etc/nginx/sites-available/app") &&
            command.Arguments.Contains("640"));
        Assert.DoesNotContain(state.Commands, command => command.Arguments.Contains("mv"));
    }

    [Fact]
    public async Task SuccessfulApplyPreservesMetadataAndReloadsAfterValidation()
    {
        var state = State.Create();
        var service = CreateService(state);
        var loaded = await service.LoadAsync(Profile(), RemotePath.Parse("/etc/nginx/sites-enabled/app"), TestContext.Current.CancellationToken);
        const string candidate = "server { listen 8080; server_name app.example.test; location / { proxy_pass http://127.0.0.1:7000; } }";

        var result = await service.ApplyAsync(Profile(), loaded.Document!, candidate, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, state.Editor.SaveCalls);
        Assert.Equal(candidate, state.Editor.Current.Text);
        var backup = Assert.Single(state.Commands, command =>
            command.Arguments.Count > 2 &&
            command.Arguments[1] == "install" &&
            command.Arguments.Contains("/etc/nginx/sites-available/app"));
        Assert.Contains("640", backup.Arguments);
        Assert.Contains("1001", backup.Arguments);
        Assert.Contains("1002", backup.Arguments);
        var reload = Assert.Single(state.Commands, command => command.Arguments.Contains("reload"));
        Assert.Equal(OperationRisk.Destructive, reload.Risk);
        Assert.True(state.Commands.Count(command => command.Arguments.Contains("-t")) >= 2);
    }

    [Fact]
    public async Task NetworkLossDuringReloadReturnsAmbiguousAndDoesNotRollbackBlindly()
    {
        var state = State.Create();
        state.ReloadTransportError = new RemoteError(RemoteErrorCode.NetworkInterrupted, "connection dropped");
        var service = CreateService(state);
        var loaded = await service.LoadAsync(Profile(), RemotePath.Parse("/etc/nginx/sites-enabled/app"), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(Profile(), loaded.Document!, State.CandidateText, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.NotNull(result.RecoveryBackupPath);
        Assert.DoesNotContain(state.Commands, command => command.Arguments.Contains("mv"));
    }

    [Fact]
    public async Task DeterministicPostReplaceValidationFailureRestoresOriginalFile()
    {
        var state = State.Create();
        state.NginxTestExitCodes.Enqueue(1);
        var service = CreateService(state);
        var loaded = await service.LoadAsync(Profile(), RemotePath.Parse("/etc/nginx/sites-enabled/app"), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(Profile(), loaded.Document!, State.CandidateText, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.RolledBack);
        Assert.Equal(State.OriginalText, state.Editor.Current.Text);
        Assert.Contains(state.Commands, command => command.Arguments.Contains("mv"));
        Assert.DoesNotContain(state.Commands, command => command.Arguments.Contains("reload"));
    }

    private static NginxSiteEditingService CreateService(State state) =>
        new(
            new FakeCommandFactory(state),
            new FakeFileSystemFactory(state),
            state.Editor,
            NginxSiteEditingOptions.Default);

    private static ServerProfile Profile() => ServerProfile.Create("nginx", "example.invalid", 22, "dev");

    private sealed class State
    {
        public const string OriginalText = "server { listen 80; server_name app.example.test; location / { proxy_pass http://127.0.0.1:5000; } }";
        public const string CandidateText = "server { listen 8080; server_name app.example.test; location / { proxy_pass http://127.0.0.1:7000; } }";

        public List<RemoteCommandSpec> Commands { get; } = [];
        public FakeEditor Editor { get; private set; } = null!;
        public int ValidationExitCode { get; set; }
        public Queue<int> NginxTestExitCodes { get; } = new();
        public RemoteError? ReloadTransportError { get; set; }

        public static State Create()
        {
            var state = new State();
            state.Editor = new FakeEditor(state, Document(OriginalText));
            return state;
        }

        private static RemoteEditorDocument Document(string text) =>
            new(
                new RemoteFileEntry(
                    RemotePath.Parse("/etc/nginx/sites-available/app"),
                    "app",
                    RemoteFileKind.File,
                    text.Length,
                    DateTimeOffset.UtcNow,
                    1001,
                    1002,
                    RemoteUnixPermissions.FromMode(640)),
                text);
    }

    private sealed class FakeEditor : IRemoteFileEditorService
    {
        private readonly State _state;
        private readonly RemoteEditorDocument _original;

        public FakeEditor(State state, RemoteEditorDocument original)
        {
            _state = state;
            _original = original;
            Current = original;
        }

        public RemoteEditorDocument Current { get; private set; }
        public int SaveCalls { get; private set; }
        public RemotePath? LastLoadedPath { get; private set; }

        public ValueTask<RemoteEditorDocument> LoadAsync(ServerProfile profile, RemotePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLoadedPath = path;
            return ValueTask.FromResult(Current);
        }

        public ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            RemoteEditValidationSpec? validation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            Current = Current with { Text = editedText };
            return ValueTask.FromResult(new RemoteEditorSaveResult(true, false, "saved"));
        }

        public void RestoreOriginal() => Current = _original;
    }

    private sealed class FakeCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly State _state;
        public FakeCommandFactory(State state) => _state = state;
        public IRemoteCommandExecutor Create(ServerProfile profile) => new FakeExecutor(profile.Id, _state);
    }

    private sealed class FakeExecutor : IRemoteCommandExecutor
    {
        private readonly State _state;
        public FakeExecutor(Guid serverProfileId, State state)
        {
            ServerProfileId = serverProfileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Commands.Add(command);
            if (command.Executable == "readlink")
            {
                return Success("/etc/nginx/sites-available/app\n");
            }

            if (command.Arguments.Contains("unshare"))
            {
                return Command(_state.ValidationExitCode, _state.ValidationExitCode == 0 ? string.Empty : "nginx: configuration test failed");
            }

            if (command.Arguments.Contains("reload"))
            {
                return _state.ReloadTransportError is not null
                    ? Task.FromResult(RemoteExecutionResult.Failure(_state.ReloadTransportError))
                    : Success();
            }

            if (command.Arguments.Contains("mv"))
            {
                _state.Editor.RestoreOriginal();
                return Success();
            }

            if (command.Arguments.Contains("-t"))
            {
                var exitCode = _state.NginxTestExitCodes.Count == 0 ? 0 : _state.NginxTestExitCodes.Dequeue();
                return Command(exitCode, exitCode == 0 ? string.Empty : "nginx: configuration test failed");
            }

            return Success();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<RemoteExecutionResult> Success(string output = "") =>
            Task.FromResult(RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero)));

        private static Task<RemoteExecutionResult> Command(int exitCode, string error) =>
            Task.FromResult(RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, string.Empty, error, TimeSpan.Zero)));
    }

    private sealed class FakeFileSystemFactory : IRemoteFileSystemFactory
    {
        private readonly State _state;
        public FakeFileSystemFactory(State state) => _state = state;
        public IRemoteFileSystem Create(ServerProfile profile) => new FakeFileSystem(profile.Id, _state);
    }

    private sealed class FakeFileSystem : IRemoteFileSystem
    {
        public FakeFileSystem(Guid serverProfileId, State state) => ServerProfileId = serverProfileId;
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

        public ValueTask UploadAsync(Stream source, RemotePath destination, long? totalBytes = null, bool overwrite = false,
            IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
