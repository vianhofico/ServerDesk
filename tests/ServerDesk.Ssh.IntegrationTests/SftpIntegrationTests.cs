using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class SftpIntegrationTests
{
    private const int ExpectedTransferBufferSize = 64 * 1024;
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";
    private static readonly string DeniedPath = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_DENIED_PATH") ?? "/tmp/serverdesk-sftp-denied";

    [Fact]
    public async Task CrudMetadataPermissionsAndShellLikeNamesWorkThroughSftpOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);
        var root = UniqueRoot();
        var original = root.Combine("$(not-a-shell); report [final].txt");
        var renamed = root.Combine("renamed file.txt");

        await fileSystem.CreateDirectoryAsync(root, cancellationToken);
        try
        {
            var payload = Encoding.UTF8.GetBytes("ServerDesk SFTP transport\n");
            await using var source = new MemoryStream(payload, writable: false);
            await fileSystem.UploadAsync(
                source,
                original,
                payload.Length,
                overwrite: false,
                cancellationToken: cancellationToken);

            var listed = await fileSystem.ListAsync(root, cancellationToken);
            var listedFile = Assert.Single(listed, entry => entry.Name == original.Name);
            Assert.Equal(RemoteFileKind.File, listedFile.Kind);
            Assert.Equal(payload.Length, listedFile.Size);
            Assert.NotNull(listedFile.UserId);
            Assert.NotNull(listedFile.GroupId);

            var stat = await fileSystem.StatAsync(original, cancellationToken);
            Assert.Equal(original.Value, stat.Path.Value);
            Assert.Equal(payload.Length, stat.Size);

            await fileSystem.SetPermissionsAsync(
                original,
                RemoteUnixPermissions.FromMode(640),
                cancellationToken);
            stat = await fileSystem.StatAsync(original, cancellationToken);
            Assert.Equal(640, stat.Permissions.Mode);

            await fileSystem.RenameAsync(original, renamed, cancellationToken: cancellationToken);
            var missing = await Assert.ThrowsAsync<RemoteFileSystemException>(() =>
                fileSystem.StatAsync(original, cancellationToken).AsTask());
            Assert.Equal(RemoteErrorCode.PathNotFound, missing.Error.Code);

            await using var destination = new MemoryStream();
            await fileSystem.DownloadAsync(renamed, destination, cancellationToken: cancellationToken);
            Assert.Equal(payload, destination.ToArray());

            var conflict = await Assert.ThrowsAsync<RemoteFileSystemException>(() =>
                fileSystem.CreateDirectoryAsync(root, cancellationToken).AsTask());
            Assert.Equal(RemoteErrorCode.PathConflict, conflict.Error.Code);

            await fileSystem.DeleteFileAsync(renamed, cancellationToken);
        }
        finally
        {
            await TryDeleteDirectoryAsync(fileSystem, root, cancellationToken);
        }
    }

    [Fact]
    public async Task LargeUploadAndDownloadUseBoundedStreamingWithoutBackingBuffers()
    {
        const long totalBytes = 6L * 1024 * 1024 + 123;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);
        var root = UniqueRoot();
        var file = root.Combine("large.bin");
        await fileSystem.CreateDirectoryAsync(root, cancellationToken);

        try
        {
            await using var source = new PatternReadStream(totalBytes);
            var uploadProgress = new RecordingProgress();
            await fileSystem.UploadAsync(
                source,
                file,
                totalBytes,
                progress: uploadProgress,
                cancellationToken: cancellationToken);

            Assert.InRange(source.MaxRequestedRead, 1, ExpectedTransferBufferSize);
            Assert.Equal(totalBytes, uploadProgress.Last?.BytesTransferred);
            Assert.Equal(RemoteTransferDirection.Upload, uploadProgress.Last?.Direction);

            await using var sink = new VerifyingSinkStream(totalBytes);
            var downloadProgress = new RecordingProgress();
            await fileSystem.DownloadAsync(
                file,
                sink,
                downloadProgress,
                cancellationToken);

            Assert.Equal(totalBytes, sink.BytesWritten);
            Assert.InRange(sink.MaxWriteSize, 1, ExpectedTransferBufferSize);
            Assert.Equal(totalBytes, downloadProgress.Last?.BytesTransferred);
            Assert.Equal(RemoteTransferDirection.Download, downloadProgress.Last?.Direction);

            await fileSystem.DeleteFileAsync(file, cancellationToken);
        }
        finally
        {
            await TryDeleteDirectoryAsync(fileSystem, root, cancellationToken);
        }
    }

    [Fact]
    public async Task CancelledUploadLeavesNoDestinationOrTemporaryPartFile()
    {
        const long totalBytes = 32L * 1024 * 1024;
        var testCancellation = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(testCancellation);
        var root = UniqueRoot();
        var file = root.Combine("cancelled-upload.bin");
        await fileSystem.CreateDirectoryAsync(root, testCancellation);

        try
        {
            await using var source = new PatternReadStream(totalBytes, TimeSpan.FromMilliseconds(12));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(120));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fileSystem.UploadAsync(
                        source,
                        file,
                        totalBytes,
                        cancellationToken: cancellation.Token)
                    .AsTask());

            var listed = await fileSystem.ListAsync(root, testCancellation);
            Assert.DoesNotContain(listed, entry => entry.Name == file.Name);
            Assert.DoesNotContain(
                listed,
                entry => entry.Name.StartsWith(".serverdesk-upload-", StringComparison.Ordinal));
        }
        finally
        {
            await TryDeleteDirectoryAsync(fileSystem, root, testCancellation);
        }
    }

    [Fact]
    public async Task CancelledDownloadExposesPartialCallerOwnedOutputAndNeverClaimsSuccess()
    {
        const long totalBytes = 8L * 1024 * 1024;
        var testCancellation = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(testCancellation);
        var root = UniqueRoot();
        var file = root.Combine("cancelled-download.bin");
        await fileSystem.CreateDirectoryAsync(root, testCancellation);

        try
        {
            await using (var source = new PatternReadStream(totalBytes))
            {
                await fileSystem.UploadAsync(
                    source,
                    file,
                    totalBytes,
                    cancellationToken: testCancellation);
            }

            await using var sink = new VerifyingSinkStream(totalBytes, TimeSpan.FromMilliseconds(15));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(120));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fileSystem.DownloadAsync(file, sink, cancellationToken: cancellation.Token).AsTask());

            Assert.InRange(sink.BytesWritten, 1, totalBytes - 1);
            await fileSystem.DeleteFileAsync(file, testCancellation);
        }
        finally
        {
            await TryDeleteDirectoryAsync(fileSystem, root, testCancellation);
        }
    }

    [Fact]
    public async Task PermissionDeniedAndDisconnectedChannelsMapToTypedErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);

        var denied = await Assert.ThrowsAsync<RemoteFileSystemException>(() =>
            fileSystem.ListAsync(RemotePath.Parse(DeniedPath), cancellationToken).AsTask());
        Assert.Equal(RemoteErrorCode.PermissionDenied, denied.Error.Code);

        await fileSystem.DisconnectAsync(cancellationToken);
        var disconnected = await Assert.ThrowsAsync<RemoteFileSystemException>(() =>
            fileSystem.StatAsync(RemotePath.Parse(Home), cancellationToken).AsTask());
        Assert.Equal(RemoteErrorCode.NetworkInterrupted, disconnected.Error.Code);
    }

    [Fact]
    public async Task SymlinkAndOwnerMetadataAreExposedWhenServerProvidesThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);

        var listed = await fileSystem.ListAsync(RemotePath.Parse(Home), cancellationToken);
        var link = Assert.Single(listed, entry => entry.Name == "serverdesk-fixture-link");

        Assert.Equal(RemoteFileKind.SymbolicLink, link.Kind);
        Assert.NotNull(link.UserId);
        Assert.NotNull(link.GroupId);
    }

    private static IRemoteFileSystem CreateFileSystem()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "SFTP fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var factory = new SftpRemoteFileSystemFactory(
            new MemorySecretStore(reference, Password),
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        return factory.Create(profile);
    }

    private static RemotePath UniqueRoot() =>
        RemotePath.Parse($"{Home}/serverdesk-sftp-{Guid.NewGuid():N}");

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
                if (entry.Kind == RemoteFileKind.Directory)
                {
                    continue;
                }

                await fileSystem.DeleteFileAsync(entry.Path, cancellationToken);
            }

            await fileSystem.DeleteDirectoryAsync(root, cancellationToken);
        }
        catch (RemoteFileSystemException exception) when (exception.Error.Code == RemoteErrorCode.PathNotFound)
        {
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
            throw new InvalidOperationException("Password SFTP fixture must not request interactive authentication.");
    }

    private sealed class RecordingProgress : IProgress<RemoteTransferProgress>
    {
        public RemoteTransferProgress? Last { get; private set; }

        public void Report(RemoteTransferProgress value)
        {
            Last = value;
        }
    }

    private sealed class PatternReadStream : Stream
    {
        private readonly long _length;
        private readonly TimeSpan _readDelay;
        private long _position;

        public PatternReadStream(long length, TimeSpan? readDelay = null)
        {
            _length = length;
            _readDelay = readDelay ?? TimeSpan.Zero;
        }

        public int MaxRequestedRead { get; private set; }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(count, remaining);
            Fill(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaxRequestedRead = Math.Max(MaxRequestedRead, buffer.Length);
            if (_readDelay > TimeSpan.Zero)
            {
                await Task.Delay(_readDelay, cancellationToken).ConfigureAwait(false);
            }

            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, remaining);
            Fill(buffer.Span[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Fill(Span<byte> buffer)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = ExpectedByte(_position + index);
            }

            _position += buffer.Length;
        }
    }

    private sealed class VerifyingSinkStream : Stream
    {
        private readonly long _expectedLength;
        private readonly TimeSpan _writeDelay;

        public VerifyingSinkStream(long expectedLength, TimeSpan? writeDelay = null)
        {
            _expectedLength = expectedLength;
            _writeDelay = writeDelay ?? TimeSpan.Zero;
        }

        public long BytesWritten { get; private set; }

        public int MaxWriteSize { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Verify(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaxWriteSize = Math.Max(MaxWriteSize, buffer.Length);
            if (_writeDelay > TimeSpan.Zero)
            {
                await Task.Delay(_writeDelay, cancellationToken).ConfigureAwait(false);
            }

            Verify(buffer.Span);
        }

        private void Verify(ReadOnlySpan<byte> buffer)
        {
            if (BytesWritten + buffer.Length > _expectedLength)
            {
                throw new InvalidDataException("Downloaded more data than expected.");
            }

            for (var index = 0; index < buffer.Length; index++)
            {
                if (buffer[index] != ExpectedByte(BytesWritten + index))
                {
                    throw new InvalidDataException($"Downloaded byte mismatch at offset {BytesWritten + index}.");
                }
            }

            BytesWritten += buffer.Length;
        }
    }

    private static byte ExpectedByte(long position) => (byte)(position % 251);
}
