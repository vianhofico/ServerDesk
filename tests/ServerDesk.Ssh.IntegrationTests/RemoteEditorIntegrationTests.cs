using System.Text;
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

public sealed class RemoteEditorIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task WritableSaveReplacesContentAndPreservesMode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var root = UniqueRoot();
        var target = root.Combine("editor.conf");

        await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
        {
            await fileSystem.ConnectAsync(cancellationToken);
            await fileSystem.CreateDirectoryAsync(root, cancellationToken);
            await UploadTextAsync(fileSystem, target, "before\n", cancellationToken);
            await fileSystem.SetPermissionsAsync(target, RemoteUnixPermissions.FromMode(640), cancellationToken);
        }

        try
        {
            var original = await fixture.Service.LoadAsync(fixture.Profile, target, cancellationToken);
            var saved = await fixture.Service.SaveWritableAsync(
                fixture.Profile,
                original,
                "after\n",
                cancellationToken);

            Assert.True(saved.IsSuccess, saved.Message);
            var reloaded = await fixture.Service.LoadAsync(fixture.Profile, target, cancellationToken);
            Assert.Equal("after\n", reloaded.Text);
            Assert.Equal(640, reloaded.Metadata.Permissions.Mode);
        }
        finally
        {
            await CleanupAsync(fixture, root, target, cancellationToken);
        }
    }

    [Fact]
    public async Task PrivilegedValidationFailureLeavesLiveFileUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var root = UniqueRoot();
        var target = root.Combine("validated.conf");
        const string liveContent = "live-original\n";

        await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
        {
            await fileSystem.ConnectAsync(cancellationToken);
            await fileSystem.CreateDirectoryAsync(root, cancellationToken);
            await UploadTextAsync(fileSystem, target, liveContent, cancellationToken);
            await fileSystem.SetPermissionsAsync(target, RemoteUnixPermissions.FromMode(600), cancellationToken);
        }

        try
        {
            var original = await fixture.Service.LoadAsync(fixture.Profile, target, cancellationToken);
            var validation = new RemoteEditValidationSpec(
                "/bin/sh",
                ["-c", "test -f \"$1\"; exit 42", "serverdesk-validator", "{file}"]);

            var result = await fixture.Service.SavePrivilegedAsync(
                fixture.Profile,
                original,
                "candidate-invalid\n",
                validation,
                cancellationToken);

            Assert.False(result.IsSuccess);
            Assert.True(result.ValidationFailed);

            var reloaded = await fixture.Service.LoadAsync(fixture.Profile, target, cancellationToken);
            Assert.Equal(liveContent, reloaded.Text);
            Assert.Equal(600, reloaded.Metadata.Permissions.Mode);

            await using var verification = fixture.FileSystemFactory.Create(fixture.Profile);
            await verification.ConnectAsync(cancellationToken);
            var entries = await verification.ListAsync(root, cancellationToken);
            Assert.DoesNotContain(entries, entry => entry.Name.StartsWith(".serverdesk-edit-", StringComparison.Ordinal));
        }
        finally
        {
            await CleanupAsync(fixture, root, target, cancellationToken);
        }
    }

    [Fact]
    public async Task PrivilegedSaveRejectsSymbolicLinkTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var symlink = RemotePath.Parse($"{Home}/serverdesk-fixture-link");
        var original = await fixture.Service.LoadAsync(fixture.Profile, symlink, cancellationToken);

        Assert.Equal(RemoteFileKind.SymbolicLink, original.Metadata.Kind);
        var result = await fixture.Service.SavePrivilegedAsync(
            fixture.Profile,
            original,
            original.Text,
            validation: null,
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.CapabilityUnavailable, result.Error?.Code);
    }

    private static EditorFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var secretReference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Remote editor fixture",
            Host,
            Port,
            Username,
            credentialReference: secretReference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(secretReference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var fileSystemFactory = new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options);
        var commandFactory = new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options);
        IRemoteFileEditorService service = new GuardedRemoteFileEditorService(fileSystemFactory, commandFactory);
        return new EditorFixture(profile, fileSystemFactory, service);
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

    private static async Task CleanupAsync(
        EditorFixture fixture,
        RemotePath root,
        RemotePath target,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            try
            {
                await fileSystem.DeleteFileAsync(target, cancellationToken);
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

    private static RemotePath UniqueRoot() =>
        RemotePath.Parse($"{Home}/serverdesk-editor-{Guid.NewGuid():N}");

    private sealed record EditorFixture(
        ServerProfile Profile,
        IRemoteFileSystemFactory FileSystemFactory,
        IRemoteFileEditorService Service);

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
