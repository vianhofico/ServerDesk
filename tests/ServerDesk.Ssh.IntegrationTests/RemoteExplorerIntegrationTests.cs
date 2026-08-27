using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class RemoteExplorerIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task ExplorerProjectionConsumesRealSftpMetadataAfterCrud()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);
        var root = RemotePath.Parse($"{Home}/serverdesk-explorer-{Guid.NewGuid():N}");
        var folder = root.Combine("folder");
        var file = root.Combine("sample.txt");

        await fileSystem.CreateDirectoryAsync(root, cancellationToken);
        try
        {
            await fileSystem.CreateDirectoryAsync(folder, cancellationToken);
            var payload = Encoding.UTF8.GetBytes("explorer integration\n");
            await using (var stream = new MemoryStream(payload, writable: false))
            {
                await fileSystem.UploadAsync(stream, file, payload.Length, cancellationToken: cancellationToken);
            }

            await fileSystem.SetPermissionsAsync(file, RemoteUnixPermissions.FromMode(640), cancellationToken);
            var rows = RemoteExplorerProjection.Project(await fileSystem.ListAsync(root, cancellationToken));

            Assert.Equal(2, rows.Count);
            Assert.Equal("folder", rows[0].Name);
            Assert.True(rows[0].IsDirectory);
            Assert.Equal("sample.txt", rows[1].Name);
            Assert.Equal("640", rows[1].PermissionsText);
            Assert.NotEqual("—", rows[1].OwnerText);
            Assert.NotEqual("—", rows[1].GroupText);

            await fileSystem.DeleteFileAsync(file, cancellationToken);
            await fileSystem.DeleteDirectoryAsync(folder, cancellationToken);
        }
        finally
        {
            try
            {
                await fileSystem.DeleteDirectoryAsync(root, cancellationToken);
            }
            catch
            {
                // Best-effort fixture cleanup; the assertions above are the certification target.
            }
        }
    }

    private static IRemoteFileSystem CreateFileSystem()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Explorer fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        return new SftpRemoteFileSystemFactory(
                new MemorySecretStore(reference, Password),
                new TrustOnceHostTrustService(),
                new RejectInteractivePrompt(),
                new SshSessionOptions(
                    TimeSpan.FromSeconds(8),
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(250)))
            .Create(profile);
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
            throw new InvalidOperationException("Password fixture must not request interactive authentication.");
    }
}
