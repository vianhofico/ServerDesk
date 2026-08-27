using ServerDesk.Application.Profiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ConnectionRouteServiceTests
{
    [Fact]
    public async Task ProxyPasswordIsStoredOnlyByReferenceAndRemovedWhenReturningToDirect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var secrets = new MemorySecretStore();
        var service = new ServerConnectionRouteService(profiles, routes, secrets);

        var saved = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(
                ServerConnectionRouteKind.Socks5Proxy,
                "proxy.internal",
                1080,
                "proxy-user"),
            "proxy-password",
            replaceProxyPassword: true,
            cancellationToken);

        Assert.NotNull(saved.ProxyCredentialReference);
        Assert.Equal("proxy-password", await secrets.GetAsync(saved.ProxyCredentialReference.Value, cancellationToken));
        Assert.Equal(saved, await routes.GetAsync(profile.Id, cancellationToken));

        var direct = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.Direct),
            proxyPassword: null,
            replaceProxyPassword: false,
            cancellationToken);

        Assert.True(direct.IsDirect);
        Assert.Null(await routes.GetAsync(profile.Id, cancellationToken));
        Assert.Null(await secrets.GetAsync(SecretReference.ForProxyRoute(profile.Id), cancellationToken));
    }

    [Fact]
    public async Task ExistingProxySecretIsKeptWhenEditorLeavesPasswordBlank()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var secrets = new MemorySecretStore();
        var service = new ServerConnectionRouteService(profiles, routes, secrets);

        var first = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-a", 8080, "user-a"),
            "first-secret",
            replaceProxyPassword: true,
            cancellationToken);
        var second = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-b", 8081, "user-b"),
            proxyPassword: null,
            replaceProxyPassword: false,
            cancellationToken);

        Assert.Equal(first.ProxyCredentialReference, second.ProxyCredentialReference);
        Assert.Equal("first-secret", await secrets.GetAsync(second.ProxyCredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task MissingBastionIsRejectedBeforePersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var service = new ServerConnectionRouteService(profiles, routes, new MemorySecretStore());

        var exception = await Assert.ThrowsAsync<ServerConnectionRouteValidationException>(async () =>
            await service.SaveAsync(
                profile.Id,
                new ServerConnectionRouteSpec(ServerConnectionRouteKind.Bastion, BastionProfileId: Guid.NewGuid()),
                proxyPassword: null,
                replaceProxyPassword: false,
                cancellationToken));

        Assert.Contains(nameof(ServerConnectionRouteSpec.BastionProfileId), exception.Errors.Keys);
        Assert.Null(await routes.GetAsync(profile.Id, cancellationToken));
    }

    [Fact]
    public async Task NestedBastionIsRejectedFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var bastion = ServerProfile.Create("Bastion", "bastion.internal", 22, "deploy");
        var upstream = ServerProfile.Create("Upstream", "upstream.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(target, bastion, upstream);
        var routes = new MemoryRouteRepository(ServerConnectionRoute.Bastion(bastion.Id, upstream.Id));
        var service = new ServerConnectionRouteService(profiles, routes, new MemorySecretStore());

        var exception = await Assert.ThrowsAsync<ServerConnectionRouteValidationException>(async () =>
            await service.SaveAsync(
                target.Id,
                new ServerConnectionRouteSpec(ServerConnectionRouteKind.Bastion, BastionProfileId: bastion.Id),
                proxyPassword: null,
                replaceProxyPassword: false,
                cancellationToken));

        Assert.Contains("Nested bastions", exception.Errors[nameof(ServerConnectionRouteSpec.BastionProfileId)], StringComparison.OrdinalIgnoreCase);
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
        private readonly Dictionary<SecretReference, string> _secrets = [];

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
}
