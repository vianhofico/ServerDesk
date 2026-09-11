from pathlib import Path

source_path = Path("src/ServerDesk.Application/Profiles/ServerProfileService.cs")
source = source_path.read_text(encoding="utf-8")
old = '''        var previousSecrets = new Dictionary<SecretReference, string?>();

        foreach (var reference in references)
        {
            previousSecrets[reference] = await _secretStore.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            await _secretStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _profileRepository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var (reference, previousSecret) in previousSecrets)
            {
                if (previousSecret is not null)
                {
                    await TrySetSecretForCompensationAsync(reference, previousSecret).ConfigureAwait(false);
                }
            }

            throw;
        }
'''
new = '''        var previousSecrets = new Dictionary<SecretReference, string?>();
        var deletedReferences = new List<SecretReference>();

        try
        {
            foreach (var reference in references)
            {
                previousSecrets[reference] = await _secretStore.GetAsync(reference, cancellationToken).ConfigureAwait(false);
                await _secretStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
                deletedReferences.Add(reference);
            }

            await _profileRepository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var reference in deletedReferences)
            {
                if (previousSecrets[reference] is { } previousSecret)
                {
                    await TrySetSecretForCompensationAsync(reference, previousSecret).ConfigureAwait(false);
                }
            }

            throw;
        }
'''
if old not in source:
    raise SystemExit("Expected ServerProfileService.DeleteAsync block was not found")
source_path.write_text(source.replace(old, new, 1), encoding="utf-8")


test_path = Path("tests/ServerDesk.Tests/ServerProfileServiceTests.cs")
test = test_path.read_text(encoding="utf-8")
if "using ServerDesk.Application.Routing;\n" not in test:
    test = test.replace(
        "using ServerDesk.Application.Profiles;\n",
        "using ServerDesk.Application.Profiles;\nusing ServerDesk.Application.Routing;\n",
        1,
    )

anchor = '''    [Fact]
    public async Task NewPasswordProfileRejectsMissingSecret()
'''
new_tests = '''    [Fact]
    public async Task DeleteRestoresEarlierCredentialWhenLaterProxyCredentialDeleteFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var routes = new InMemoryRouteRepository();
        var secrets = new FailingDeleteSecretStore();
        var service = new ServerProfileService(repository, secrets, routes);
        var created = await service.CreateAsync(PasswordSpec("Production"), "server-secret", cancellationToken);
        var serverReference = created.CredentialReference!.Value;
        var proxyReference = SecretReference.ForProxyRoute(created.Id);
        await secrets.SetAsync(proxyReference, "proxy-secret", cancellationToken);
        await routes.UpsertAsync(
            ServerConnectionRoute.Proxy(
                created.Id,
                ServerConnectionRouteKind.HttpProxy,
                "proxy.example.com",
                8080,
                "proxy-user",
                proxyReference),
            cancellationToken);
        secrets.FailDeleteFor = proxyReference;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(created.Id, cancellationToken).AsTask());

        Assert.NotNull(await repository.GetAsync(created.Id, cancellationToken));
        Assert.Equal("server-secret", await secrets.GetAsync(serverReference, cancellationToken));
        Assert.Equal("proxy-secret", await secrets.GetAsync(proxyReference, cancellationToken));
    }

    [Fact]
    public async Task DeleteRestoresAllDeletedCredentialsWhenProfileDeleteFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new FailingDeleteProfileRepository();
        var routes = new InMemoryRouteRepository();
        var secrets = new FailingDeleteSecretStore();
        var service = new ServerProfileService(repository, secrets, routes);
        var created = await service.CreateAsync(PasswordSpec("Production"), "server-secret", cancellationToken);
        var serverReference = created.CredentialReference!.Value;
        var proxyReference = SecretReference.ForProxyRoute(created.Id);
        await secrets.SetAsync(proxyReference, "proxy-secret", cancellationToken);
        await routes.UpsertAsync(
            ServerConnectionRoute.Proxy(
                created.Id,
                ServerConnectionRouteKind.Socks5Proxy,
                "proxy.example.com",
                1080,
                "proxy-user",
                proxyReference),
            cancellationToken);
        repository.FailDelete = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(created.Id, cancellationToken).AsTask());

        Assert.NotNull(await repository.GetAsync(created.Id, cancellationToken));
        Assert.Equal("server-secret", await secrets.GetAsync(serverReference, cancellationToken));
        Assert.Equal("proxy-secret", await secrets.GetAsync(proxyReference, cancellationToken));
    }

'''
if anchor not in test:
    raise SystemExit("Expected test insertion anchor was not found")
test = test.replace(anchor, new_tests + anchor, 1)

class_anchor = '''    private sealed class InMemorySecretStore : ISecretStore
'''
helpers = '''    private sealed class FailingDeleteProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles = [];

        public bool FailDelete { get; set; }

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
            if (FailDelete)
            {
                throw new InvalidOperationException("Profile delete fixture failed.");
            }

            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryRouteRepository : IConnectionRouteRepository
    {
        private readonly Dictionary<Guid, ServerConnectionRoute> _routes = [];

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

    private sealed class FailingDeleteSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public SecretReference? FailDeleteFor { get; set; }

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[reference.Value] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secrets.GetValueOrDefault(reference.Value));
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDeleteFor is { } failing && failing == reference)
            {
                throw new InvalidOperationException("Secret delete fixture failed.");
            }

            _secrets.Remove(reference.Value);
            return ValueTask.CompletedTask;
        }
    }

'''
if class_anchor not in test:
    raise SystemExit("Expected test helper insertion anchor was not found")
test = test.replace(class_anchor, helpers + class_anchor, 1)
test_path.write_text(test, encoding="utf-8")
