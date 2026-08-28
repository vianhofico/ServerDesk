using System.Globalization;
using System.Text;
using ServerDesk.Application.EnvironmentFiles;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class EnvironmentFileIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task GuardedApplyPreservesMetadataBlocksLostUpdateAndNeverExecutesEnvContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var root = RemotePath.Parse($"{Home}/serverdesk-env-{Guid.NewGuid():N}");
        var target = root.Combine(".env");
        var marker = root.Combine("must-not-exist");
        const string originalText = """
            # deployment environment
            APP_PORT=5000
            DATABASE_PASSWORD=integration-secret
            export LEGACY_SETTING=preserve-raw
            """;

        await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
        {
            await fileSystem.ConnectAsync(cancellationToken);
            await fileSystem.CreateDirectoryAsync(root, cancellationToken);
            await UploadTextAsync(fileSystem, target, originalText, cancellationToken);
            await fileSystem.SetPermissionsAsync(target, RemoteUnixPermissions.FromMode(640), cancellationToken);
        }

        try
        {
            var loaded = await fixture.Service.LoadAsync(fixture.Profile, target, cancellationToken);
            Assert.True(loaded.IsSuccess, loaded.Error?.Message);
            var snapshot = loaded.Snapshot!;
            var initialMetadata = snapshot.Original.Metadata;
            var password = Assert.Single(snapshot.Entries, entry => entry.Key == "DATABASE_PASSWORD");
            Assert.True(password.IsSecret);
            Assert.True(snapshot.HasUnsupportedLines);

            var port = Assert.Single(snapshot.Entries, entry => entry.Key == "APP_PORT");
            var candidate = EnvironmentFileEditor.SetValueAtLine(snapshot.Text, port.LineNumber, port.Key, "7000");
            candidate = EnvironmentFileEditor.AddAssignment(candidate, "RAW_COMMAND", $"$(touch {marker.Value})");

            var applied = await fixture.Service.ApplyAsync(
                fixture.Profile,
                snapshot,
                candidate,
                cancellationToken: cancellationToken);

            Assert.True(applied.IsSuccess, applied.Message);
            Assert.NotNull(applied.Snapshot);
            Assert.Equal(candidate, applied.Snapshot!.Text);
            Assert.Contains("export LEGACY_SETTING=preserve-raw", applied.Snapshot.Text, StringComparison.Ordinal);
            Assert.Equal(initialMetadata.Permissions, applied.Snapshot.Original.Metadata.Permissions);
            Assert.Equal(initialMetadata.UserId, applied.Snapshot.Original.Metadata.UserId);
            Assert.Equal(initialMetadata.GroupId, applied.Snapshot.Original.Metadata.GroupId);

            await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
            {
                await fileSystem.ConnectAsync(cancellationToken);
                var entries = await fileSystem.ListAsync(root, cancellationToken);
                Assert.DoesNotContain(entries, entry => string.Equals(entry.Name, marker.Name, StringComparison.Ordinal));
            }

            var beforeConcurrentChange = applied.Snapshot;
            var externallyChanged = candidate.Replace("APP_PORT=7000", "APP_PORT=7100", StringComparison.Ordinal);
            var externalDocument = await fixture.Editor.LoadAsync(fixture.Profile, target, cancellationToken);
            var externalSave = await fixture.Editor.SaveWritableAsync(
                fixture.Profile,
                externalDocument,
                externallyChanged,
                cancellationToken);
            Assert.True(externalSave.IsSuccess, externalSave.Message);

            var staleCandidate = candidate.Replace("APP_PORT=7000", "APP_PORT=7200", StringComparison.Ordinal);
            var blocked = await fixture.Service.ApplyAsync(
                fixture.Profile,
                beforeConcurrentChange!,
                staleCandidate,
                cancellationToken: cancellationToken);

            Assert.False(blocked.IsSuccess);
            Assert.Equal(RemoteErrorCode.PathConflict, blocked.Error?.Code);
            var liveAfterBlockedOverwrite = await fixture.Editor.LoadAsync(fixture.Profile, target, cancellationToken);
            Assert.Equal(externallyChanged, liveAfterBlockedOverwrite.Text);
        }
        finally
        {
            await CleanupAsync(fixture, root, CancellationToken.None);
        }
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "environment-file fixture",
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
        var fileSystemFactory = new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options);
        var commandFactory = new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options);
        IRemoteFileEditorService editor = new GuardedRemoteFileEditorService(fileSystemFactory, commandFactory);
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        return new Fixture(profile, fileSystemFactory, editor, service);
    }

    private static async Task UploadTextAsync(
        IRemoteFileSystem fileSystem,
        RemotePath target,
        string text,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            target,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
    }

    private static async Task CleanupAsync(Fixture fixture, RemotePath root, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            IReadOnlyList<RemoteFileEntry> entries;
            try
            {
                entries = await fileSystem.ListAsync(root, cancellationToken);
            }
            catch (RemoteFileSystemException)
            {
                return;
            }

            foreach (var entry in entries.Where(entry => entry.Kind != RemoteFileKind.Directory))
            {
                try
                {
                    await fileSystem.DeleteFileAsync(entry.Path, cancellationToken);
                }
                catch
                {
                }
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

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteFileSystemFactory FileSystemFactory,
        IRemoteFileEditorService Editor,
        EnvironmentFileService Service);

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
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
