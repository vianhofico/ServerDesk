using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ServerProfileServiceTests
{
    [Fact]
    public async Task CreatePasswordProfileStoresSecretOutsideProfileRepository()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var service = new ServerProfileService(repository, secrets);

        var profile = await service.CreateAsync(
            PasswordSpec("Production"),
            "correct-horse-battery-staple",
            cancellationToken);

        Assert.NotNull(profile.CredentialReference);
        Assert.Equal(SecretReference.ForServerProfile(profile.Id), profile.CredentialReference.Value);
        Assert.Equal(
            "correct-horse-battery-staple",
            await secrets.GetAsync(profile.CredentialReference.Value, cancellationToken));
        Assert.Single(await repository.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task UpdateWithoutReplacementKeepsExistingPassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var service = new ServerProfileService(repository, secrets);
        var created = await service.CreateAsync(PasswordSpec("Old name"), "old-secret", cancellationToken);

        var updated = await service.UpdateAsync(
            created.Id,
            PasswordSpec("New name"),
            replacementSecret: null,
            replaceSecret: false,
            cancellationToken);

        Assert.Equal("New name", updated.Name);
        Assert.Equal(created.CredentialReference, updated.CredentialReference);
        Assert.Equal(
            "old-secret",
            await secrets.GetAsync(updated.CredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task ExplicitReplacementChangesStoredPasswordWithoutChangingReference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var service = new ServerProfileService(repository, secrets);
        var created = await service.CreateAsync(PasswordSpec("Production"), "old-secret", cancellationToken);

        var updated = await service.UpdateAsync(
            created.Id,
            PasswordSpec("Production"),
            "new-secret",
            replaceSecret: true,
            cancellationToken);

        Assert.Equal(created.CredentialReference, updated.CredentialReference);
        Assert.Equal(
            "new-secret",
            await secrets.GetAsync(updated.CredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task SwitchingToSshAgentRemovesStoredCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var service = new ServerProfileService(repository, secrets);
        var created = await service.CreateAsync(PasswordSpec("Production"), "secret", cancellationToken);
        var oldReference = created.CredentialReference!.Value;

        var updated = await service.UpdateAsync(
            created.Id,
            new ServerProfileSpec(
                created.Name,
                created.Host,
                created.Port,
                created.Username,
                created.Environment,
                ServerAuthenticationKind.SshAgent,
                null),
            replacementSecret: null,
            replaceSecret: false,
            cancellationToken);

        Assert.Null(updated.CredentialReference);
        Assert.Null(await secrets.GetAsync(oldReference, cancellationToken));
    }

    [Fact]
    public async Task DeleteRemovesProfileAndCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var service = new ServerProfileService(repository, secrets);
        var created = await service.CreateAsync(PasswordSpec("Production"), "secret", cancellationToken);
        var reference = created.CredentialReference!.Value;

        await service.DeleteAsync(created.Id, cancellationToken);

        Assert.Empty(await repository.ListAsync(cancellationToken));
        Assert.Null(await secrets.GetAsync(reference, cancellationToken));
    }

    [Fact]
    public async Task NewPasswordProfileRejectsMissingSecret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new ServerProfileService(new InMemoryProfileRepository(), new InMemorySecretStore());

        var exception = await Assert.ThrowsAsync<ServerProfileValidationException>(async () =>
            await service.CreateAsync(PasswordSpec("Production"), null, cancellationToken));

        Assert.Contains("Secret", exception.Errors.Keys);
    }

    private static ServerProfileSpec PasswordSpec(string name) =>
        new(
            name,
            "server.example.com",
            22,
            "deploy",
            "production",
            ServerAuthenticationKind.Password,
            null);

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles = [];

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> result = _profiles.Values.OrderBy(profile => profile.Name).ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryGetValue(id, out var profile);
            return ValueTask.FromResult(profile);
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

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

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
            return ValueTask.FromResult(_secrets.TryGetValue(reference.Value, out var secret) ? secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets.Remove(reference.Value);
            return ValueTask.CompletedTask;
        }
    }
}
