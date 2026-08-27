using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Storage;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class StorageIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task InspectAndDirectoryAnalyzerWorkThroughRealSshChannel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var service = new ServerStorageService(fixture.CommandFactory);
        var root = RemotePath.Parse($"{Home}/serverdesk-storage-{Guid.NewGuid():N}");
        var child = root.Combine("child folder");
        var file = root.Combine("fixture.txt");
        var childFile = child.Combine("nested.txt");

        await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
        {
            await fileSystem.ConnectAsync(cancellationToken);
            await fileSystem.CreateDirectoryAsync(root, cancellationToken);
            await fileSystem.CreateDirectoryAsync(child, cancellationToken);
            await UploadAsync(fileSystem, file, new string('a', 4096), cancellationToken);
            await UploadAsync(fileSystem, childFile, new string('b', 8192), cancellationToken);
        }

        try
        {
            var snapshot = await service.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(snapshot.IsSuccess, snapshot.Error?.Message);
            Assert.NotEmpty(snapshot.Filesystems);
            Assert.Contains(snapshot.Filesystems, filesystem => filesystem.MountPoint == "/");

            var analysis = await service.AnalyzeDirectoryAsync(
                fixture.Profile,
                root.Value,
                cancellationToken);
            Assert.True(analysis.IsSuccess, analysis.Error?.Message);
            Assert.Contains(analysis.Entries, entry => entry.Path == root.Value);
            Assert.Contains(analysis.Entries, entry => entry.Path == child.Value);
            Assert.All(analysis.Entries, entry => Assert.True(entry.SizeBytes >= 0));
        }
        finally
        {
            await CleanupAsync(fixture, root, child, [file, childFile], CancellationToken.None);
        }
    }

    private static async Task UploadAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes, writable: false);
        await fileSystem.UploadAsync(
            stream,
            path,
            bytes.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
    }

    private static async Task CleanupAsync(
        StorageFixture fixture,
        RemotePath root,
        RemotePath child,
        IReadOnlyList<RemotePath> files,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            foreach (var file in files.Reverse())
            {
                try
                {
                    await fileSystem.DeleteFileAsync(file, cancellationToken);
                }
                catch
                {
                }
            }

            try
            {
                await fileSystem.DeleteDirectoryAsync(child, cancellationToken);
            }
            catch
            {
            }

            try
            {
                await fileSystem.DeleteDirectoryAsync(root, cancellationToken);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static StorageFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Storage fixture",
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
        return new StorageFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record StorageFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

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
            return ValueTask.FromResult(new HostTrustVerification(
                HostTrustOutcome.TrustedOnce,
                observation,
                []));
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
