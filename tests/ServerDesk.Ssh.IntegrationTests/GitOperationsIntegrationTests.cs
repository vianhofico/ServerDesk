using System.Globalization;
using System.Text;
using ServerDesk.Application.Git;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
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

public sealed class GitOperationsIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task RealGitFetchPreviewAndFastForwardCrossOpenSshWithVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var origin = $"{Home}/serverdesk-git-origin-{token}.git";
        var seed = $"{Home}/serverdesk-git-seed-{token}";
        var work = $"{Home}/serverdesk-git-work-{token}";

        await SetupRepositoriesAsync(fixture, origin, seed, work, cancellationToken);
        var service = new GitOperationsService(fixture.CommandFactory, GitOperationsOptions.Default);

        var initial = await service.InspectAsync(fixture.Profile, work, cancellationToken);
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        Assert.True(initial.Snapshot?.IsClean);
        Assert.Equal("main", initial.Snapshot?.Branch);
        Assert.Equal("origin/main", initial.Snapshot?.Upstream);
        Assert.Equal(0, initial.Snapshot?.Behind);
        var initialRevision = initial.Snapshot!.Revision;

        var discovery = await service.DiscoverAsync(fixture.Profile, Home, 2, cancellationToken);
        Assert.True(discovery.IsSuccess, discovery.Error?.Message);
        Assert.Contains(seed, discovery.RepositoryPaths);
        Assert.Contains(work, discovery.RepositoryPaths);

        await UploadTextAsync(
            fixture,
            RemotePath.Parse($"{seed}/incoming file.txt"),
            "second revision\n",
            overwrite: false,
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-C", seed, "add", "--", "incoming file.txt"],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-c", "core.hooksPath=/dev/null", "-C", seed, "commit", "--no-gpg-sign", "-m", "incoming change"],
            cancellationToken);
        await ExecuteRequiredAsync(fixture, ["-C", seed, "push", "origin", "main"], cancellationToken);

        var beforeFetch = await service.InspectAsync(fixture.Profile, work, cancellationToken);
        Assert.True(beforeFetch.IsSuccess, beforeFetch.Error?.Message);
        Assert.Equal(0, beforeFetch.Snapshot?.Behind);

        var fetched = await service.FetchAsync(fixture.Profile, work, cancellationToken);
        Assert.True(fetched.IsSuccess, fetched.Error?.Message);
        Assert.Equal(1, fetched.VerifiedSnapshot?.Behind);
        Assert.Equal(initialRevision, fetched.VerifiedSnapshot?.Revision);

        var preview = await service.PreviewPullAsync(fixture.Profile, work, cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.True(preview.Preview?.CanApply);
        Assert.Equal(1, preview.Preview?.Behind);
        Assert.Equal(initialRevision, preview.Preview?.CurrentRevision);
        Assert.Contains(preview.Preview!.IncomingCommits, commit =>
            commit.Contains("incoming change", StringComparison.Ordinal));

        var pulled = await service.PullAsync(
            fixture.Profile,
            work,
            initialRevision,
            cancellationToken);
        Assert.True(pulled.IsSuccess, pulled.Error?.Message);
        Assert.NotEqual(initialRevision, pulled.VerifiedSnapshot?.Revision);
        Assert.Equal(0, pulled.VerifiedSnapshot?.Behind);
        Assert.True(pulled.VerifiedSnapshot?.IsClean);

        await UploadTextAsync(
            fixture,
            RemotePath.Parse($"{work}/local-untracked.txt"),
            "local work\n",
            overwrite: false,
            cancellationToken);
        var dirtyPreview = await service.PreviewPullAsync(fixture.Profile, work, cancellationToken);
        Assert.True(dirtyPreview.IsSuccess, dirtyPreview.Error?.Message);
        Assert.False(dirtyPreview.Preview?.CanApply);
        Assert.Contains("clean", dirtyPreview.Preview!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SetupRepositoriesAsync(
        GitFixture fixture,
        string origin,
        string seed,
        string work,
        CancellationToken cancellationToken)
    {
        await ExecuteRequiredAsync(
            fixture,
            ["init", "--bare", "--initial-branch=main", origin],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["init", "--initial-branch=main", seed],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-C", seed, "config", "user.email", "serverdesk-ci@example.invalid"],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-C", seed, "config", "user.name", "ServerDesk CI"],
            cancellationToken);
        await UploadTextAsync(
            fixture,
            RemotePath.Parse($"{seed}/README.md"),
            "initial revision\n",
            overwrite: false,
            cancellationToken);
        await ExecuteRequiredAsync(fixture, ["-C", seed, "add", "--", "README.md"], cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-c", "core.hooksPath=/dev/null", "-C", seed, "commit", "--no-gpg-sign", "-m", "initial"],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-C", seed, "remote", "add", "origin", origin],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["-C", seed, "push", "-u", "origin", "main"],
            cancellationToken);
        await ExecuteRequiredAsync(
            fixture,
            ["clone", "--no-recurse-submodules", origin, work],
            cancellationToken);
    }

    private static async Task ExecuteRequiredAsync(
        GitFixture fixture,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        await using var executor = fixture.CommandFactory.Create(fixture.Profile);
        var execution = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "git",
                arguments,
                TimeSpan.FromSeconds(30),
                OperationRisk.Mutating,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LC_ALL"] = "C",
                    ["GIT_TERMINAL_PROMPT"] = "0",
                }),
            cancellationToken);
        Assert.True(execution.IsSuccess, execution.Error?.Message);
        Assert.Equal(0, execution.Command!.ExitCode);
    }

    private static async Task UploadTextAsync(
        GitFixture fixture,
        RemotePath path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            path,
            payload.Length,
            overwrite,
            cancellationToken: cancellationToken);
    }

    private static GitFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Git fixture",
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
        return new GitFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record GitFixture(
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
