using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class ConnectionRoutingIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int BastionPort = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly int TargetPort = ReadPort("SERVERDESK_SSH_TARGET_PORT", 2223);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    public static TheoryData<ServerConnectionRouteKind, string, int> ProxyRoutes =>
        new()
        {
            { ServerConnectionRouteKind.HttpProxy, "SERVERDESK_HTTP_PROXY_PORT", 18081 },
            { ServerConnectionRouteKind.Socks4Proxy, "SERVERDESK_SOCKS4_PROXY_PORT", 18082 },
            { ServerConnectionRouteKind.Socks5Proxy, "SERVERDESK_SOCKS5_PROXY_PORT", 18083 },
        };

    [Theory]
    [MemberData(nameof(ProxyRoutes))]
    public async Task NativeProxyRoutesExecuteCommandsAcrossRealOpenSsh(
        ServerConnectionRouteKind routeKind,
        string portEnvironmentVariable,
        int defaultPort)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = CreatePasswordProfile("Proxy target", Host, BastionPort);
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository(
            ServerConnectionRoute.Proxy(
                profile.Id,
                routeKind,
                "127.0.0.1",
                ReadPort(portEnvironmentVariable, defaultPort)));
        var secretStore = new MemorySecretStore((profile.CredentialReference!.Value, Password));
        var trust = new RecordingTrustService();
        var factory = CreateFactory(secretStore, trust, routes, profiles);
        await using var executor = factory.Create(profile);

        var result = await executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("printf", "%s", $"route-{routeKind}"),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal($"route-{routeKind}", result.Command?.StandardOutput);
        Assert.Contains(trust.Observations, observation =>
            observation.Host == Host && observation.Port == BastionPort);
    }

    [Fact]
    public async Task SingleHopBastionVerifiesBastionAndTargetEndpointsIndependently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bastion = CreatePasswordProfile("Bastion", Host, BastionPort);
        var target = CreatePasswordProfile("Target through bastion", Host, TargetPort);
        var profiles = new MemoryProfileRepository(bastion, target);
        var routes = new MemoryRouteRepository(
            ServerConnectionRoute.Bastion(target.Id, bastion.Id));
        var secretStore = new MemorySecretStore(
            (bastion.CredentialReference!.Value, Password),
            (target.CredentialReference!.Value, Password));
        var trust = new RecordingTrustService();
        var factory = CreateFactory(secretStore, trust, routes, profiles);
        await using var executor = factory.Create(target);

        var result = await executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("printf", "%s", "bastion-route-ok"),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("bastion-route-ok", result.Command?.StandardOutput);
        Assert.Contains(trust.Observations, observation => observation.Host == Host && observation.Port == BastionPort);
        Assert.Contains(trust.Observations, observation => observation.Host == Host && observation.Port == TargetPort);
    }

    [Fact]
    public async Task MissingBastionFailsClosedWithTypedConnectionResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = CreatePasswordProfile("Missing bastion target", Host, TargetPort);
        var profiles = new MemoryProfileRepository(target);
        var routes = new MemoryRouteRepository(
            ServerConnectionRoute.Bastion(target.Id, Guid.NewGuid()));
        var secretStore = new MemorySecretStore((target.CredentialReference!.Value, Password));
        var factory = CreateFactory(secretStore, new RecordingTrustService(), routes, profiles);
        await using var executor = factory.Create(target);

        var result = await executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("true"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal(ServerDesk.Domain.Errors.RemoteErrorCode.InvalidEndpoint, result.Error.Code);
    }

    private static RouteAwareRemoteCommandExecutorFactory CreateFactory(
        ISecretStore secretStore,
        IHostTrustService trust,
        IConnectionRouteRepository routes,
        IProfileRepository profiles) =>
        new(
            secretStore,
            trust,
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)),
            routes,
            profiles);

    private static ServerProfile CreatePasswordProfile(string name, string host, int port)
    {
        var id = Guid.NewGuid();
        return ServerProfile.Create(
            id,
            name,
            host,
            port,
            Username,
            credentialReference: SecretReference.ForServerProfile(id),
            authenticationKind: ServerAuthenticationKind.Password);
    }

    private static int ReadPort(string environmentVariable, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(environmentVariable),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port)
            ? port
            : fallback;

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

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<SecretReference, string> _secrets;

        public MemorySecretStore(params (SecretReference Reference, string Secret)[] secrets)
        {
            _secrets = secrets.ToDictionary(item => item.Reference, item => item.Secret);
        }

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[reference] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secrets.GetValueOrDefault(reference));
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets.Remove(reference);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTrustService : IHostTrustService
    {
        public List<HostKeyObservation> Observations { get; } = [];

        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Observations)
            {
                Observations.Add(observation);
            }

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
            throw new InvalidOperationException("Password routing fixtures must not request interactive authentication.");
    }
}
