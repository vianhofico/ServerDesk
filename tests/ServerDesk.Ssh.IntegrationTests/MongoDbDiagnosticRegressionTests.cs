using System.Globalization;
using ServerDesk.Application.Databases;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Databases;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class MongoDbDiagnosticRegressionTests
{
    private const string TargetDatabase = "serverdesk_restore";

    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int SshPort = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly int MongoDbPort = ReadPort("SERVERDESK_MONGODB_PORT", 17017);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string DatabasePassword = Environment.GetEnvironmentVariable("SERVERDESK_DB_PASSWORD") ?? "serverdesk-db-password";

    [Fact]
    public async Task RequestedDatabaseIsPrioritizedInsideCatalogBound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "MongoDB target-first diagnostics fixture",
            Host,
            SshPort,
            Username,
            credentialReference: sshReference,
            authenticationKind: ServerAuthenticationKind.Password);
        var profileId = Guid.NewGuid();
        var databaseReference = SecretReference.ForDatabaseProfile(profileId);
        var profile = DatabaseConnectionProfile.Create(
            profileId,
            server.Id,
            "MongoDB target-first fixture",
            DatabaseEngineKind.MongoDb,
            "127.0.0.1",
            MongoDbPort,
            TargetDatabase,
            "serverdesk",
            DatabaseAuthenticationKind.Password,
            databaseReference,
            "admin");
        var secrets = new MemorySecretStore(
            new Dictionary<SecretReference, string>
            {
                [sshReference] = SshPassword,
                [databaseReference] = DatabasePassword,
            });
        var tunnelService = new DatabaseTunnelService(
            new MemoryProfileRepository(server),
            new SshPortForwardSessionFactory(
                secrets,
                new TrustOnceHostTrustService(),
                new RejectInteractivePrompt(),
                new SshSessionOptions(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(250))));
        var diagnostics = new DatabaseDiagnosticService(
            tunnelService,
            secrets,
            [new MongoDbDiagnosticAdapter()],
            new DatabaseDiagnosticOptions(1, 20, TimeSpan.FromSeconds(10)) { MaxTextLength = 512 });

        var result = await diagnostics.InspectAsync(profile, cancellationToken);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        var snapshot = Assert.IsType<DatabaseDiagnosticSnapshot>(result.Snapshot);
        var catalog = Assert.Single(snapshot.Catalogs);
        Assert.Equal(TargetDatabase, catalog.Name);
        Assert.True(snapshot.IsTruncated);
        Assert.DoesNotContain(DatabasePassword, snapshot.ConnectionIdentity ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Metadata, item => item.Value.Contains(DatabasePassword, StringComparison.Ordinal));
    }

    private static int ReadPort(string name, int fallback) => int.Parse(
        Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        private readonly ServerProfile _profile;

        public MemoryProfileRepository(ServerProfile profile) => _profile = profile;

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> profiles = [_profile];
            return ValueTask.FromResult(profiles);
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ServerProfile?>(id == _profile.Id ? _profile : null);
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<SecretReference, string> _secrets;

        public MemorySecretStore(IReadOnlyDictionary<SecretReference, string> secrets) => _secrets = secrets;

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(_secrets.TryGetValue(reference, out var secret) ? secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(HostKeyObservation observation, CancellationToken cancellationToken = default)
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
