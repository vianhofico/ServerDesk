using ServerDesk.Application.Databases;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseProfileServiceTests
{
    [Fact]
    public async Task CreatePasswordProfileStoresOnlyStableSecretReferenceInProfile()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;

        var profile = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            PasswordSpec("Postgres"),
            "database-password",
            cancellationToken);

        Assert.Equal(SecretReference.ForDatabaseProfile(profile.Id), profile.CredentialReference);
        Assert.Equal(
            "database-password",
            await fixture.Secrets.GetAsync(profile.CredentialReference!.Value, cancellationToken));
        Assert.Single(await fixture.DatabaseProfiles.ListForServerAsync(fixture.Server.Id, cancellationToken));
    }

    [Fact]
    public async Task UpdateMetadataWithoutReplacementKeepsExistingPassword()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            PasswordSpec("Old"),
            "old-secret",
            cancellationToken);

        var updated = await fixture.Service.UpdateAsync(
            created.Id,
            PasswordSpec("New"),
            replacementSecret: null,
            replaceSecret: false,
            cancellationToken);

        Assert.Equal("New", updated.Name);
        Assert.Equal(created.CredentialReference, updated.CredentialReference);
        Assert.Equal(
            "old-secret",
            await fixture.Secrets.GetAsync(updated.CredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task SwitchingFromNoPasswordToPasswordWritesSecretEvenWithoutReplaceFlag()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            NoPasswordSpec("Redis"),
            initialSecret: null,
            cancellationToken);

        var updated = await fixture.Service.UpdateAsync(
            created.Id,
            PasswordSpec("Redis secured", DatabaseEngineKind.Redis, 6379),
            replacementSecret: "new-secret",
            replaceSecret: false,
            cancellationToken);

        Assert.Equal(DatabaseAuthenticationKind.Password, updated.AuthenticationKind);
        Assert.NotNull(updated.CredentialReference);
        Assert.Equal(
            "new-secret",
            await fixture.Secrets.GetAsync(updated.CredentialReference.Value, cancellationToken));
    }

    [Fact]
    public async Task SwitchingFromPasswordToNoneDeletesSecret()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            PasswordSpec("Redis", DatabaseEngineKind.Redis, 6379),
            "secret",
            cancellationToken);
        var reference = created.CredentialReference!.Value;

        var updated = await fixture.Service.UpdateAsync(
            created.Id,
            NoPasswordSpec("Redis"),
            replacementSecret: null,
            replaceSecret: false,
            cancellationToken);

        Assert.Null(updated.CredentialReference);
        Assert.Null(await fixture.Secrets.GetAsync(reference, cancellationToken));
    }

    [Fact]
    public async Task RepositoryFailureAfterPasswordReplacementRestoresOldSecret()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            PasswordSpec("Postgres"),
            "old-secret",
            cancellationToken);
        fixture.DatabaseProfiles.FailNextUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.UpdateAsync(
                created.Id,
                PasswordSpec("Postgres"),
                "new-secret",
                replaceSecret: true,
                cancellationToken));

        Assert.Equal(
            "old-secret",
            await fixture.Secrets.GetAsync(created.CredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task RepositoryFailureDuringCreateDeletesNewSecret()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.DatabaseProfiles.FailNextUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.CreateAsync(
                fixture.Server.Id,
                PasswordSpec("Postgres"),
                "secret",
                cancellationToken));

        Assert.Empty(fixture.Secrets.Values);
    }

    [Fact]
    public async Task RepositoryFailureDuringDeleteRestoresSecret()
    {
        var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await fixture.Service.CreateAsync(
            fixture.Server.Id,
            PasswordSpec("Postgres"),
            "secret",
            cancellationToken);
        fixture.DatabaseProfiles.FailNextDelete = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.DeleteAsync(created.Id, cancellationToken));

        Assert.Equal(
            "secret",
            await fixture.Secrets.GetAsync(created.CredentialReference!.Value, cancellationToken));
    }

    [Fact]
    public async Task MissingServerProfileFailsClosed()
    {
        var fixture = new Fixture(addServer: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Service.CreateAsync(
                fixture.Server.Id,
                PasswordSpec("Postgres"),
                "secret",
                TestContext.Current.CancellationToken));
    }

    private static DatabaseProfileSpec PasswordSpec(
        string name,
        DatabaseEngineKind engine = DatabaseEngineKind.PostgreSql,
        int port = 5432) =>
        new(name, engine, "127.0.0.1", port, "appdb", "app", DatabaseAuthenticationKind.Password);

    private static DatabaseProfileSpec NoPasswordSpec(string name) =>
        new(name, DatabaseEngineKind.Redis, "127.0.0.1", 6379, null, null, DatabaseAuthenticationKind.None);

    private sealed class Fixture
    {
        public Fixture(bool addServer = true)
        {
            Server = ServerProfile.Create("DB host", "example.invalid", 22, "dev");
            if (addServer)
            {
                Servers.Items[Server.Id] = Server;
            }

            Service = new DatabaseProfileService(DatabaseProfiles, Servers, Secrets);
        }

        public ServerProfile Server { get; }
        public MemoryDatabaseProfileRepository DatabaseProfiles { get; } = new();
        public MemoryProfileRepository Servers { get; } = new();
        public MemorySecretStore Secrets { get; } = new();
        public DatabaseProfileService Service { get; }
    }

    private sealed class MemoryDatabaseProfileRepository : IDatabaseProfileRepository
    {
        private readonly Dictionary<Guid, DatabaseConnectionProfile> _profiles = [];

        public bool FailNextUpsert { get; set; }
        public bool FailNextDelete { get; set; }

        public ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DatabaseConnectionProfile> values = _profiles.Values.ToArray();
            return ValueTask.FromResult(values);
        }

        public ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DatabaseConnectionProfile> values = _profiles.Values
                .Where(profile => profile.ServerProfileId == serverProfileId)
                .ToArray();
            return ValueTask.FromResult(values);
        }

        public ValueTask<DatabaseConnectionProfile?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryGetValue(id, out var profile);
            return ValueTask.FromResult(profile);
        }

        public ValueTask UpsertAsync(
            DatabaseConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextUpsert)
            {
                FailNextUpsert = false;
                throw new InvalidOperationException("fixture upsert failure");
            }

            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("fixture delete failure");
            }

            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        public Dictionary<Guid, ServerProfile> Items { get; } = [];

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> values = Items.Values.ToArray();
            return ValueTask.FromResult(values);
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.TryGetValue(id, out var profile);
            return ValueTask.FromResult(profile);
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Values => _values;

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[reference.Value] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_values.TryGetValue(reference.Value, out var secret) ? secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(reference.Value);
            return ValueTask.CompletedTask;
        }
    }
}
