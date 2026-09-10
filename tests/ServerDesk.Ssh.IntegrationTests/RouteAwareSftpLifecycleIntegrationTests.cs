using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class RouteAwareSftpLifecycleIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly int HttpProxyPort = ReadPort("SERVERDESK_HTTP_PROXY_PORT", 18081);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task DisposeCancelsActiveUploadThroughProxyBeforeTearingDownRoute()
    {
        const long totalBytes = 16L * 1024 * 1024;
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = CreateProfile();
        var root = RemotePath.Parse($"{Home}/serverdesk-routed-sftp-{Guid.NewGuid():N}");
        var file = root.Combine("dispose-through-proxy.bin");
        var fileSystem = CreateFileSystem(profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await fileSystem.CreateDirectoryAsync(root, cancellationToken);

        try
        {
            await using var source = new SlowPatternReadStream(totalBytes, TimeSpan.FromMilliseconds(12));
            var progress = new SignalingProgress();
            var uploadTask = fileSystem.UploadAsync(
                    source,
                    file,
                    totalBytes,
                    progress: progress,
                    cancellationToken: cancellationToken)
                .AsTask();

            await progress.Started.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            var disposeTask = fileSystem.DisposeAsync().AsTask();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploadTask);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.False(fileSystem.IsConnected);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                fileSystem.ListAsync(root, cancellationToken).AsTask());
            await fileSystem.DisposeAsync();
        }
        finally
        {
            await fileSystem.DisposeAsync();
        }

        await using var verifier = CreateFileSystem(profile);
        await verifier.ConnectAsync(cancellationToken);
        try
        {
            var listed = await verifier.ListAsync(root, cancellationToken);
            Assert.DoesNotContain(listed, entry => entry.Name == file.Name);
            Assert.DoesNotContain(
                listed,
                entry => entry.Name.StartsWith(".serverdesk-upload-", StringComparison.Ordinal));
        }
        finally
        {
            await TryDeleteDirectoryAsync(verifier, root, cancellationToken);
        }
    }

    private static IRemoteFileSystem CreateFileSystem(ServerProfile profile)
    {
        var routes = new MemoryRouteRepository(
            ServerConnectionRoute.Proxy(
                profile.Id,
                ServerConnectionRouteKind.HttpProxy,
                "127.0.0.1",
                HttpProxyPort));
        var profiles = new MemoryProfileRepository(profile);
        var factory = new RouteAwareSftpRemoteFileSystemFactory(
            new MemorySecretStore(profile.CredentialReference!.Value, Password),
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)),
            routes,
            profiles);
        return factory.Create(profile);
    }

    private static ServerProfile CreateProfile()
    {
        var profileId = Guid.NewGuid();
        return ServerProfile.Create(
            profileId,
            "Routed SFTP lifecycle",
            Host,
            Port,
            Username,
            credentialReference: SecretReference.ForServerProfile(profileId),
            authenticationKind: ServerAuthenticationKind.Password);
    }

    private static async ValueTask TryDeleteDirectoryAsync(
        IRemoteFileSystem fileSystem,
        RemotePath root,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.IsConnected)
        {
            return;
        }

        try
        {
            var entries = await fileSystem.ListAsync(root, cancellationToken);
            foreach (var entry in entries)
            {
                if (entry.Kind != RemoteFileKind.Directory)
                {
                    await fileSystem.DeleteFileAsync(entry.Path, cancellationToken);
                }
            }

            await fileSystem.DeleteDirectoryAsync(root, cancellationToken);
        }
        catch (RemoteFileSystemException exception) when (exception.Error.Code == RemoteErrorCode.PathNotFound)
        {
        }
    }

    private static int ReadPort(string variable, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(variable),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port)
            ? port
            : fallback;

    private sealed class MemoryRouteRepository : IConnectionRouteRepository
    {
        private readonly Dictionary<Guid, ServerConnectionRoute> _routes;

        public MemoryRouteRepository(params ServerConnectionRoute[] routes)
        {
            _routes = routes.ToDictionary(route => route.ServerProfileId);
        }

        public ValueTask<ServerConnectionRoute?> GetAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_routes.GetValueOrDefault(serverProfileId));
        }

        public ValueTask UpsertAsync(
            ServerConnectionRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes[route.ServerProfileId] = route;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid serverProfileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes.Remove(serverProfileId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles;

        public MemoryProfileRepository(params ServerProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Id);
        }

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ServerProfile>>(_profiles.Values.ToArray());
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profiles.GetValueOrDefault(id));
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
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

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
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
            throw new InvalidOperationException("Password routed-SFTP fixture must not request interactive authentication.");
    }

    private sealed class SignalingProgress : IProgress<RemoteTransferProgress>
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Report(RemoteTransferProgress value)
        {
            if (value.BytesTransferred > 0)
            {
                _started.TrySetResult();
            }
        }
    }

    private sealed class SlowPatternReadStream : Stream
    {
        private readonly long _length;
        private readonly TimeSpan _delay;
        private long _position;

        public SlowPatternReadStream(long length, TimeSpan delay)
        {
            _length = length;
            _delay = delay;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = unchecked((byte)((_position + index) % 251));
            }

            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
