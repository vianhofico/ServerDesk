from pathlib import Path

source_path = Path("src/ServerDesk.Application/Routing/ServerConnectionRouting.cs")
source = source_path.read_text(encoding="utf-8")
old = '''    private async ValueTask RollbackSecretAsync(
        SecretReference? oldReference,
        string? oldSecret,
        bool oldDeleted,
        SecretReference? desiredReference,
        bool newWritten)
    {
        if (newWritten && desiredReference is not null && desiredReference != oldReference)
        {
            await TryDeleteSecretAsync(desiredReference.Value).ConfigureAwait(false);
        }

        if (oldDeleted && oldReference is not null && oldSecret is not null)
        {
            await TrySetSecretAsync(oldReference.Value, oldSecret).ConfigureAwait(false);
        }
    }
'''
new = '''    private async ValueTask RollbackSecretAsync(
        SecretReference? oldReference,
        string? oldSecret,
        bool oldDeleted,
        SecretReference? desiredReference,
        bool newWritten)
    {
        if (newWritten && desiredReference is not null && desiredReference != oldReference)
        {
            await TryDeleteSecretAsync(desiredReference.Value).ConfigureAwait(false);
        }

        var oldReferenceChanged = oldDeleted ||
            (newWritten && oldReference is not null && desiredReference == oldReference);
        if (!oldReferenceChanged || oldReference is null)
        {
            return;
        }

        if (oldSecret is null)
        {
            await TryDeleteSecretAsync(oldReference.Value).ConfigureAwait(false);
        }
        else
        {
            await TrySetSecretAsync(oldReference.Value, oldSecret).ConfigureAwait(false);
        }
    }
'''
if old not in source:
    raise SystemExit("Expected RollbackSecretAsync block was not found")
source_path.write_text(source.replace(old, new, 1), encoding="utf-8")


test_path = Path("tests/ServerDesk.Tests/ConnectionRouteServiceTests.cs")
test = test_path.read_text(encoding="utf-8")
anchor = '''    [Fact]
    public async Task MissingBastionIsRejectedBeforePersistence()
'''
new_tests = '''    [Fact]
    public async Task FailedSameReferencePasswordReplacementRestoresPreviousSecret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var secrets = new MemorySecretStore();
        var service = new ServerConnectionRouteService(profiles, routes, secrets);

        var original = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-a", 8080, "user-a"),
            "old-password",
            replaceProxyPassword: true,
            cancellationToken);
        var reference = original.ProxyCredentialReference!.Value;
        routes.FailUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(
                profile.Id,
                new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-b", 8081, "user-b"),
                "new-password",
                replaceProxyPassword: true,
                cancellationToken).AsTask());

        Assert.Equal(original, await routes.GetAsync(profile.Id, cancellationToken));
        Assert.Equal("old-password", await secrets.GetAsync(reference, cancellationToken));
    }

    [Fact]
    public async Task FailedSameReferencePasswordReplacementRestoresPreviouslyMissingSecretState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var secrets = new MemorySecretStore();
        var service = new ServerConnectionRouteService(profiles, routes, secrets);

        var original = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.Socks5Proxy, "proxy-a", 1080, "user-a"),
            "old-password",
            replaceProxyPassword: true,
            cancellationToken);
        var reference = original.ProxyCredentialReference!.Value;
        await secrets.DeleteAsync(reference, cancellationToken);
        routes.FailUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(
                profile.Id,
                new ServerConnectionRouteSpec(ServerConnectionRouteKind.Socks5Proxy, "proxy-b", 1081, "user-b"),
                "new-password",
                replaceProxyPassword: true,
                cancellationToken).AsTask());

        Assert.Equal(original, await routes.GetAsync(profile.Id, cancellationToken));
        Assert.Null(await secrets.GetAsync(reference, cancellationToken));
    }

    [Fact]
    public async Task SuccessfulSameReferencePasswordReplacementPersistsNewSecret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        var profiles = new MemoryProfileRepository(profile);
        var routes = new MemoryRouteRepository();
        var secrets = new MemorySecretStore();
        var service = new ServerConnectionRouteService(profiles, routes, secrets);

        var original = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-a", 8080, "user-a"),
            "old-password",
            replaceProxyPassword: true,
            cancellationToken);
        var updated = await service.SaveAsync(
            profile.Id,
            new ServerConnectionRouteSpec(ServerConnectionRouteKind.HttpProxy, "proxy-b", 8081, "user-b"),
            "new-password",
            replaceProxyPassword: true,
            cancellationToken);

        Assert.Equal(original.ProxyCredentialReference, updated.ProxyCredentialReference);
        Assert.Equal("new-password", await secrets.GetAsync(updated.ProxyCredentialReference!.Value, cancellationToken));
        Assert.Equal(updated, await routes.GetAsync(profile.Id, cancellationToken));
    }

'''
if anchor not in test:
    raise SystemExit("Expected route test insertion anchor was not found")
test = test.replace(anchor, new_tests + anchor, 1)

old_repo = '''    private sealed class MemoryRouteRepository : IConnectionRouteRepository
    {
        private readonly Dictionary<Guid, ServerConnectionRoute> _routes;

        public MemoryRouteRepository(params ServerConnectionRoute[] routes)
        {
            _routes = routes.ToDictionary(route => route.ServerProfileId);
        }
'''
new_repo = '''    private sealed class MemoryRouteRepository : IConnectionRouteRepository
    {
        private readonly Dictionary<Guid, ServerConnectionRoute> _routes;

        public MemoryRouteRepository(params ServerConnectionRoute[] routes)
        {
            _routes = routes.ToDictionary(route => route.ServerProfileId);
        }

        public bool FailUpsert { get; set; }
'''
if old_repo not in test:
    raise SystemExit("Expected MemoryRouteRepository header was not found")
test = test.replace(old_repo, new_repo, 1)

old_upsert = '''        public ValueTask UpsertAsync(
            ServerConnectionRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes[route.ServerProfileId] = route;
            return ValueTask.CompletedTask;
        }
'''
new_upsert = '''        public ValueTask UpsertAsync(
            ServerConnectionRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailUpsert)
            {
                throw new InvalidOperationException("Route upsert fixture failed.");
            }

            _routes[route.ServerProfileId] = route;
            return ValueTask.CompletedTask;
        }
'''
if old_upsert not in test:
    raise SystemExit("Expected MemoryRouteRepository.UpsertAsync was not found")
test = test.replace(old_upsert, new_upsert, 1)
test_path.write_text(test, encoding="utf-8")
