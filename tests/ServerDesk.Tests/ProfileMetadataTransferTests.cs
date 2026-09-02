using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProfileMetadataTransferTests
{
    [Fact]
    public async Task ExportContainsOnlyApprovedMetadataAndOmitsCredentialAndPrivateKeyData()
    {
        var id = Guid.NewGuid();
        var secretReference = SecretReference.Create("export-secret-reference-sentinel");
        var profile = ServerProfile.Create(
            id,
            "Production",
            "prod.example.test",
            2222,
            "ops",
            "prod",
            secretReference,
            ServerAuthenticationKind.PrivateKey,
            @"C:\secret\private-key-sentinel");
        var profiles = new FakeProfileRepository(profile);
        var organizations = new FakeOrganizationRepository(
            ServerProfileOrganization.Create(id, "critical", ["api", "prod"], true));
        var service = new ProfileMetadataTransferService(profiles, organizations);

        var json = await service.ExportAsync([id], TestContext.Current.CancellationToken);

        Assert.Contains("prod.example.test", json, StringComparison.Ordinal);
        Assert.Contains("PrivateKey", json, StringComparison.Ordinal);
        Assert.Contains("critical", json, StringComparison.Ordinal);
        Assert.DoesNotContain("export-secret-reference-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialReference", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKeyPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRejectsUnmappedCredentialField()
    {
        var service = new ProfileMetadataTransferService(
            new FakeProfileRepository(),
            new FakeOrganizationRepository());
        const string json = """
            {
              "schema": "serverdesk.profile-metadata",
              "schemaVersion": 1,
              "profiles": [
                {
                  "name": "one",
                  "host": "one.test",
                  "port": 22,
                  "username": "ops",
                  "environment": null,
                  "authenticationKind": "Password",
                  "groupName": null,
                  "tags": [],
                  "isFavorite": false,
                  "credentialReference": "must-be-rejected"
                }
              ]
            }
            """;

        _ = Assert.Throws<ProfileMetadataTransferException>(() => service.Parse(json));
    }

    [Fact]
    public async Task ImportCreatesCredentiallessProfileAndSkipsExistingIdentity()
    {
        var existing = ServerProfile.Create("existing", "existing.test", 22, "ops");
        var profiles = new FakeProfileRepository(existing);
        var organizations = new FakeOrganizationRepository();
        var service = new ProfileMetadataTransferService(profiles, organizations);
        var document = new ProfileMetadataTransferDocument(
            ProfileMetadataTransferService.SchemaName,
            ProfileMetadataTransferService.CurrentSchemaVersion,
            [
                Entry("duplicate", "EXISTING.test", 22, "OPS", ServerAuthenticationKind.Password),
                Entry("new", "new.test", 2202, "deploy", ServerAuthenticationKind.PrivateKey),
            ]);
        var updates = new List<ProfileMetadataImportUpdate>();

        await service.ImportAsync(
            document,
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Single(updates, update => update.SourceIndex == 0 && update.State == ProfileMetadataImportState.Duplicate);
        var importedUpdate = Assert.Single(
            updates,
            update => update.SourceIndex == 1 && update.State == ProfileMetadataImportState.Imported);
        Assert.NotNull(importedUpdate.ImportedProfileId);
        var imported = await profiles.GetAsync(importedUpdate.ImportedProfileId!.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(imported);
        Assert.Equal(ServerAuthenticationKind.PrivateKey, imported.AuthenticationKind);
        Assert.Null(imported.CredentialReference);
        Assert.Null(imported.PrivateKeyPath);
        var organization = await organizations.GetAsync(imported.Id, TestContext.Current.CancellationToken);
        Assert.Equal("imported", organization.GroupName);
        Assert.Contains("portable", organization.Tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrganizationFailureCompensatesNewProfileAndSanitizesFailure()
    {
        var profiles = new FakeProfileRepository();
        var organizations = new FakeOrganizationRepository { FailNextUpsert = true };
        var service = new ProfileMetadataTransferService(profiles, organizations);
        var document = Document(Entry("new", "new.test", 22, "ops", ServerAuthenticationKind.Password));
        var updates = new List<ProfileMetadataImportUpdate>();

        await service.ImportAsync(
            document,
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(updates, update => update.State == ProfileMetadataImportState.Failed);
        Assert.Equal(nameof(InvalidOperationException), failure.FailureKind);
        Assert.Equal(1, profiles.DeleteCount);
        Assert.Empty(await profiles.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationStopsStartingNewImports()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var profiles = new FakeProfileRepository();
        var organizations = new FakeOrganizationRepository
        {
            AfterSuccessfulUpsert = _ => cancellation.Cancel(),
        };
        var service = new ProfileMetadataTransferService(profiles, organizations);
        var document = new ProfileMetadataTransferDocument(
            ProfileMetadataTransferService.SchemaName,
            ProfileMetadataTransferService.CurrentSchemaVersion,
            [
                Entry("one", "one.test", 22, "ops", ServerAuthenticationKind.Password),
                Entry("two", "two.test", 22, "ops", ServerAuthenticationKind.Password),
            ]);
        var updates = new List<ProfileMetadataImportUpdate>();

        await service.ImportAsync(
            document,
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Single(updates, update => update.SourceIndex == 0 && update.State == ProfileMetadataImportState.Imported);
        Assert.Single(updates, update => update.SourceIndex == 1 && update.State == ProfileMetadataImportState.Cancelled);
        Assert.Single(await profiles.ListAsync(TestContext.Current.CancellationToken));
    }

    private static ProfileMetadataTransferDocument Document(ProfileMetadataTransferEntry entry) =>
        new(
            ProfileMetadataTransferService.SchemaName,
            ProfileMetadataTransferService.CurrentSchemaVersion,
            [entry]);

    private static ProfileMetadataTransferEntry Entry(
        string name,
        string host,
        int port,
        string username,
        ServerAuthenticationKind authenticationKind) =>
        new(
            name,
            host,
            port,
            username,
            "prod",
            authenticationKind,
            "imported",
            ["portable"],
            true);

    private sealed class FakeProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles;

        public FakeProfileRepository(params ServerProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Id);
        }

        public int DeleteCount { get; private set; }

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
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOrganizationRepository : IServerProfileOrganizationRepository
    {
        private readonly Dictionary<Guid, ServerProfileOrganization> _organizations;

        public FakeOrganizationRepository(params ServerProfileOrganization[] organizations)
        {
            _organizations = organizations.ToDictionary(organization => organization.ServerProfileId);
        }

        public bool FailNextUpsert { get; set; }

        public Action<Guid>? AfterSuccessfulUpsert { get; set; }

        public ValueTask<ServerProfileOrganization> GetAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _organizations.GetValueOrDefault(serverProfileId) ?? ServerProfileOrganization.Empty(serverProfileId));
        }

        public ValueTask<IReadOnlyDictionary<Guid, ServerProfileOrganization>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<Guid, ServerProfileOrganization>>(
                new Dictionary<Guid, ServerProfileOrganization>(_organizations));
        }

        public ValueTask UpsertAsync(
            ServerProfileOrganization organization,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextUpsert)
            {
                FailNextUpsert = false;
                throw new InvalidOperationException("sensitive-import-failure-details");
            }

            _organizations[organization.ServerProfileId] = organization;
            AfterSuccessfulUpsert?.Invoke(organization.ServerProfileId);
            return ValueTask.CompletedTask;
        }
    }
}
