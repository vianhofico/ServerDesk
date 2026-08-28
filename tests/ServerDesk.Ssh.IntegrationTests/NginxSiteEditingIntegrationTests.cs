using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class NginxSiteEditingIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222", CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task InvalidSuccessAndRollbackPathsCrossRealOpenSshAndSftp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var directory = RemotePath.Parse($"{Home}/serverdesk-nginx-edit-{Guid.NewGuid():N}");
        await InstallFixtureAsync(fixture, directory, cancellationToken);

        try
        {
            var livePath = directory.Combine("live.conf");
            var sudoPath = directory.Combine("nginx-edit-sudo.sh");
            var namespacePath = directory.Combine("nginx-edit-unshare.sh");
            var nginxPath = directory.Combine("nginx-edit-runtime.sh");
            var editor = new WritableFixtureEditor(fixture.FileSystemFactory, fixture.CommandFactory);
            var service = new NginxSiteEditingService(
                fixture.CommandFactory,
                fixture.FileSystemFactory,
                editor,
                new NginxSiteEditingOptions(
                    TimeSpan.FromSeconds(15),
                    1024 * 1024,
                    sudoPath.Value,
                    nginxPath.Value,
                    namespacePath.Value,
                    "/bin/sh",
                    "readlink"));

            var loaded = await service.LoadAsync(fixture.Profile, livePath, cancellationToken);
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);
            var original = loaded.Document!.Document.Text;

            var invalid = await service.ApplyAsync(
                fixture.Profile,
                loaded.Document,
                original + "\n# INVALID_CANDIDATE",
                cancellationToken);

            Assert.False(invalid.IsSuccess);
            Assert.True(invalid.ValidationFailed);
            Assert.Equal(original, await ReadTextAsync(fixture, livePath, cancellationToken));
            Assert.Equal(string.Empty, await ReadOptionalTextAsync(fixture, directory.Combine("reload.log"), cancellationToken));

            const string validCandidate = "server { listen 8080; server_name ssh-edit.example.test; location / { proxy_pass http://127.0.0.1:7000; } }\n";
            var success = await service.ApplyAsync(
                fixture.Profile,
                loaded.Document,
                validCandidate,
                cancellationToken);

            Assert.True(success.IsSuccess, success.Message);
            Assert.Equal(validCandidate, await ReadTextAsync(fixture, livePath, cancellationToken));
            Assert.Equal("reload\n", await ReadTextAsync(fixture, directory.Combine("reload.log"), cancellationToken));

            var afterSuccess = await service.LoadAsync(fixture.Profile, livePath, cancellationToken);
            Assert.True(afterSuccess.IsSuccess, afterSuccess.Error?.Message);
            var liveOnlyFailure = validCandidate + "# INVALID_LIVE_ONLY\n";
            var rollback = await service.ApplyAsync(
                fixture.Profile,
                afterSuccess.Document!,
                liveOnlyFailure,
                cancellationToken);

            Assert.False(rollback.IsSuccess);
            Assert.True(rollback.RolledBack);
            Assert.False(rollback.AmbiguousState);
            Assert.Equal(validCandidate, await ReadTextAsync(fixture, livePath, cancellationToken));
            Assert.Equal("reload\n", await ReadTextAsync(fixture, directory.Combine("reload.log"), cancellationToken));
        }
        finally
        {
            await CleanupAsync(fixture, directory, CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(Fixture fixture, RemotePath directory, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await fileSystem.CreateDirectoryAsync(directory, cancellationToken);

        await UploadTextAsync(
            fileSystem,
            directory.Combine("live.conf"),
            "server { listen 80; server_name ssh-edit.example.test; location / { proxy_pass http://127.0.0.1:5000; } }\n",
            RemoteUnixPermissions.FromMode(640),
            cancellationToken);
        await UploadTextAsync(
            fileSystem,
            directory.Combine("target-path"),
            directory.Combine("live.conf").Value + "\n",
            RemoteUnixPermissions.FromMode(600),
            cancellationToken);

        foreach (var script in new[] { "nginx-edit-sudo.sh", "nginx-edit-unshare.sh", "nginx-edit-runtime.sh" })
        {
            var content = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", script), cancellationToken);
            await UploadTextAsync(
                fileSystem,
                directory.Combine(script),
                content,
                RemoteUnixPermissions.FromMode(700),
                cancellationToken);
        }
    }

    private static async Task UploadTextAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        string text,
        RemoteUnixPermissions permissions,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var stream = new MemoryStream(bytes, writable: false);
        await fileSystem.UploadAsync(stream, path, bytes.Length, overwrite: false, cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(path, permissions, cancellationToken);
    }

    private static async Task<string> ReadTextAsync(Fixture fixture, RemotePath path, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await fileSystem.DownloadAsync(path, buffer, cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> ReadOptionalTextAsync(Fixture fixture, RemotePath path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTextAsync(fixture, path, cancellationToken);
        }
        catch (RemoteFileSystemException)
        {
            return string.Empty;
        }
    }

    private static async Task CleanupAsync(Fixture fixture, RemotePath directory, CancellationToken cancellationToken)
    {
        await using var executor = fixture.CommandFactory.Create(fixture.Profile);
        try
        {
            _ = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "rm",
                    ["-rf", "--", directory.Value],
                    TimeSpan.FromSeconds(10),
                    OperationRisk.Destructive),
                cancellationToken);
        }
        catch
        {
        }
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "nginx edit fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new Fixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class WritableFixtureEditor : IRemoteFileEditorService
    {
        private readonly RemoteFileEditorService _inner;

        public WritableFixtureEditor(
            IRemoteFileSystemFactory fileSystemFactory,
            IRemoteCommandExecutorFactory commandFactory)
        {
            _inner = new RemoteFileEditorService(fileSystemFactory, commandFactory);
        }

        public ValueTask<RemoteEditorDocument> LoadAsync(
            ServerProfile profile,
            RemotePath path,
            CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(profile, path, cancellationToken);

        public ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            CancellationToken cancellationToken = default) =>
            _inner.SaveWritableAsync(profile, original, editedText, cancellationToken);

        public ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            RemoteEditValidationSpec? validation,
            CancellationToken cancellationToken = default) =>
            _inner.SaveWritableAsync(profile, original, editedText, cancellationToken);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public MemorySecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, []));
        }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
