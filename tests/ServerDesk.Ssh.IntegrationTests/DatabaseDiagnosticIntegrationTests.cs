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

public sealed class DatabaseDiagnosticIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string DatabasePassword = Environment.GetEnvironmentVariable("SERVERDESK_DB_PASSWORD") ?? "serverdesk-db-password";
    private static readonly string SqlServerPassword = Environment.GetEnvironmentVariable("SERVERDESK_SQLSERVER_PASSWORD") ?? "ServerDesk!SqlServer-Fixture-Only#110";

    public static TheoryData<DatabaseEngineKind, string, int, string> CertifiedEngines => new()
    {
        { DatabaseEngineKind.PostgreSql, "SERVERDESK_POSTGRES_PORT", 15432, "18." },
        { DatabaseEngineKind.MySql, "SERVERDESK_MYSQL_PORT", 13306, "8.4." },
        { DatabaseEngineKind.MariaDb, "SERVERDESK_MARIADB_PORT", 13307, "11.8." },
        { DatabaseEngineKind.Redis, "SERVERDESK_REDIS_PORT", 16379, "8.10." },
        { DatabaseEngineKind.SqlServer, "SERVERDESK_SQLSERVER_PORT", 11433, "17.0.4075.5" },
    };

    [Theory]
    [MemberData(nameof(CertifiedEngines))]
    public async Task AuthenticatedDiagnosticsRunThroughRealOpenSshTunnelAgainstCertifiedEngine(
        DatabaseEngineKind engine,
        string portVariable,
        int fallbackPort,
        string expectedVersionPrefix)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "Database diagnostics fixture",
            Host,
            Port,
            Username,
            credentialReference: sshReference,
            authenticationKind: ServerAuthenticationKind.Password);
        var databaseId = Guid.NewGuid();
        var databaseReference = SecretReference.ForDatabaseProfile(databaseId);
        var databaseProfile = DatabaseConnectionProfile.Create(
            databaseId,
            server.Id,
            $"{engine} certified fixture",
            engine,
            "127.0.0.1",
            ReadPort(portVariable, fallbackPort),
            engine == DatabaseEngineKind.Redis ? null : "serverdesk",
            DatabaseUsername(engine),
            DatabaseAuthenticationKind.Password,
            databaseReference);
        var profiles = new MemoryProfileRepository(server);
        var databaseSecret = DatabaseSecret(engine);
        var secrets = new MemorySecretStore(
            new Dictionary<SecretReference, string>
            {
                [sshReference] = SshPassword,
                [databaseReference] = databaseSecret,
            });
        var sessionFactory = new SshPortForwardSessionFactory(
            secrets,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var tunnelService = new DatabaseTunnelService(profiles, sessionFactory);
        var adapter = CreateAdapter(engine);
        var diagnostics = new DatabaseDiagnosticService(
            tunnelService,
            secrets,
            [adapter],
            new DatabaseDiagnosticOptions(25, 20, TimeSpan.FromSeconds(10)) { MaxTextLength = 512 });

        var result = await diagnostics.InspectAsync(databaseProfile, cancellationToken);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(engine, result.Snapshot.Engine);
        Assert.StartsWith(expectedVersionPrefix, result.Snapshot.ServerVersion, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Snapshot.Metrics);
        Assert.NotEmpty(result.Snapshot.Metadata);
        Assert.DoesNotContain(databaseSecret, result.Snapshot.ConnectionIdentity ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Snapshot.Metadata,
            item => item.Value.Contains(databaseSecret, StringComparison.Ordinal));
        Assert.InRange(result.Snapshot.Catalogs.Count, 0, 25);
    }

    private static IDatabaseEngineDiagnosticAdapter CreateAdapter(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => new PostgreSqlDiagnosticAdapter(),
        DatabaseEngineKind.MySql => new MySqlDiagnosticAdapter(),
        DatabaseEngineKind.MariaDb => new MariaDbDiagnosticAdapter(),
        DatabaseEngineKind.Redis => new RedisDiagnosticAdapter(),
        DatabaseEngineKind.SqlServer => new SqlServerDiagnosticAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static string? DatabaseUsername(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.Redis => null,
        DatabaseEngineKind.SqlServer => "sa",
        _ => "serverdesk",
    };

    private static string DatabaseSecret(DatabaseEngineKind engine) =>
        engine == DatabaseEngineKind.SqlServer ? SqlServerPassword : DatabasePassword;

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
            return ValueTask.FromResult<string?>(_secrets.TryGetValue(reference, out var secret) ? secret : null);
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
