using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class SshIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string PrivateKeyPath = Environment.GetEnvironmentVariable("SERVERDESK_SSH_KEY") ?? "/tmp/serverdesk_ci_key";
    private static readonly string PrivateKeyPassphrase = Environment.GetEnvironmentVariable("SERVERDESK_SSH_KEY_PASSPHRASE") ?? "serverdesk-key-pass";

    [Fact]
    public async Task PasswordAuthenticationConnectsAndDisconnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var secretStore = new MemorySecretStore((reference, Password));
        var profile = ServerProfile.Create(
            profileId,
            "Password fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);

        await using var session = CreateFactory(secretStore).Create(profile);
        await session.ConnectAsync(cancellationToken);

        Assert.Equal(RemoteSessionState.Connected, session.State);
        Assert.NotNull(session.ConnectedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(session.ServerVersion));
        Assert.Null(session.LastError);

        await session.DisconnectAsync(cancellationToken);
        Assert.Equal(RemoteSessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task EncryptedPrivateKeyAuthenticationConnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var secretStore = new MemorySecretStore((reference, PrivateKeyPassphrase));
        var profile = ServerProfile.Create(
            profileId,
            "Private key fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.PrivateKey,
            privateKeyPath: PrivateKeyPath);

        await using var session = CreateFactory(secretStore).Create(profile);
        await session.ConnectAsync(cancellationToken);

        Assert.Equal(RemoteSessionState.Connected, session.State);
        Assert.Null(session.LastError);
    }

    [Fact]
    public async Task KeyboardInteractiveAuthenticationConnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create(
            "Keyboard interactive fixture",
            Host,
            Port,
            Username,
            authenticationKind: ServerAuthenticationKind.KeyboardInteractive);
        var prompt = new PasswordInteractivePrompt(Password);
        var factory = CreateFactory(new MemorySecretStore(), prompt);

        await using var session = factory.Create(profile);
        await session.ConnectAsync(cancellationToken);

        Assert.Equal(RemoteSessionState.Connected, session.State);
        Assert.True(prompt.ChallengeCount > 0);
        Assert.Null(session.LastError);
    }

    [Fact]
    public async Task InvalidPasswordMapsToAuthenticationFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var secretStore = new MemorySecretStore((reference, "definitely-wrong-password"));
        var profile = ServerProfile.Create(
            profileId,
            "Invalid password fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);

        await using var session = CreateFactory(secretStore).Create(profile);
        var exception = await Assert.ThrowsAsync<RemoteSessionException>(async () =>
            await session.ConnectAsync(cancellationToken));

        Assert.Equal(RemoteErrorCode.AuthenticationFailed, exception.Error.Code);
        Assert.Equal(RemoteSessionState.Faulted, session.State);
        Assert.Equal(RemoteErrorCode.AuthenticationFailed, session.LastError?.Code);
    }

    [Fact]
    public async Task RejectedUnknownHostNeverAuthenticates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var secretStore = new TrackingSecretStore((reference, Password));
        var profile = ServerProfile.Create(
            profileId,
            "Rejected host fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var factory = CreateFactory(secretStore, hostTrustService: new RejectUnknownHostTrustService());

        await using var session = factory.Create(profile);
        var exception = await Assert.ThrowsAsync<RemoteSessionException>(async () =>
            await session.ConnectAsync(cancellationToken));

        Assert.Equal(RemoteErrorCode.HostKeyUnknown, exception.Error.Code);
        Assert.Equal(RemoteSessionState.Faulted, session.State);
        Assert.Equal(1, secretStore.ReadCount);
    }

    private static SshRemoteSessionFactory CreateFactory(
        ISecretStore secretStore,
        IInteractiveAuthenticationPrompt? interactivePrompt = null,
        IHostTrustService? hostTrustService = null) =>
        new(
            secretStore,
            hostTrustService ?? new TrustOnceHostTrustService(),
            interactivePrompt ?? new PasswordInteractivePrompt(Password),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));

    private class MemorySecretStore : ISecretStore
    {
        protected readonly Dictionary<SecretReference, string> Secrets;

        public MemorySecretStore(params (SecretReference Reference, string Secret)[] secrets)
        {
            Secrets = secrets.ToDictionary(item => item.Reference, item => item.Secret);
        }

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Secrets[reference] = secret;
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Secrets.TryGetValue(reference, out var secret);
            return ValueTask.FromResult(secret);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Secrets.Remove(reference);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingSecretStore : MemorySecretStore
    {
        public TrackingSecretStore(params (SecretReference Reference, string Secret)[] secrets)
            : base(secrets)
        {
        }

        public int ReadCount { get; private set; }

        public override ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.GetAsync(reference, cancellationToken);
        }
    }

    private sealed class PasswordInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        private readonly string _password;

        public PasswordInteractivePrompt(string password)
        {
            _password = password;
        }

        public int ChallengeCount { get; private set; }

        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChallengeCount++;
            IReadOnlyList<string> responses = challenge.Prompts
                .Select(prompt => prompt.IsSecret ? _password : string.Empty)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<string>?>(responses);
        }
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

    private sealed class RejectUnknownHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(
                HostTrustOutcome.RejectedUnknown,
                observation,
                []));
        }
    }
}
