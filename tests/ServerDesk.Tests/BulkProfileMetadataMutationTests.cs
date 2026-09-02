using ServerDesk.Application.Profiles;
using Xunit;

namespace ServerDesk.Tests;

public sealed class BulkProfileMetadataMutationTests
{
    [Fact]
    public async Task AddTagPreservesExistingGroupTagsAndFavoriteState()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var fake = new FakeOrganizationService(
            ServerProfileOrganization.Create(firstId, "prod", ["api"], true),
            ServerProfileOrganization.Create(secondId, "prod", ["worker"], false));
        var service = new BulkProfileMetadataMutationService(fake);
        var updates = new List<BulkProfileMetadataUpdate>();

        await service.ExecuteAsync(
            new BulkProfileMetadataRequest(
                [Target(firstId, "one"), Target(secondId, "two")],
                BulkProfileMetadataOperation.AddTag,
                "reviewed"),
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var first = await fake.GetAsync(firstId, TestContext.Current.CancellationToken);
        var second = await fake.GetAsync(secondId, TestContext.Current.CancellationToken);
        Assert.Equal("prod", first.GroupName);
        Assert.True(first.IsFavorite);
        Assert.Contains("api", first.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("reviewed", first.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("prod", second.GroupName);
        Assert.False(second.IsFavorite);
        Assert.Contains("worker", second.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("reviewed", second.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, updates.Count(update => update.State == BulkProfileMetadataUpdateState.Succeeded));
    }

    [Fact]
    public async Task FailedTargetDoesNotPreventFollowingTargetAndFailureDoesNotExposeExceptionMessage()
    {
        var failedId = Guid.NewGuid();
        var succeedingId = Guid.NewGuid();
        var fake = new FakeOrganizationService(
            ServerProfileOrganization.Empty(failedId),
            ServerProfileOrganization.Empty(succeedingId));
        fake.FailOnSave.Add(failedId);
        var service = new BulkProfileMetadataMutationService(fake);
        var updates = new List<BulkProfileMetadataUpdate>();

        await service.ExecuteAsync(
            new BulkProfileMetadataRequest(
                [Target(failedId, "failed"), Target(succeedingId, "success")],
                BulkProfileMetadataOperation.MarkFavorite),
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(updates.Where(update =>
            update.ServerProfileId == failedId && update.State == BulkProfileMetadataUpdateState.Failed));
        Assert.Equal(nameof(InvalidOperationException), failure.FailureKind);
        Assert.DoesNotContain("sensitive", failure.FailureKind, StringComparison.OrdinalIgnoreCase);
        var succeeded = await fake.GetAsync(succeedingId, TestContext.Current.CancellationToken);
        Assert.True(succeeded.IsFavorite);
    }

    [Fact]
    public async Task CancellationStopsStartingNewTargetsAndPublishesRemainingAsCancelled()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fake = new FakeOrganizationService(
            ServerProfileOrganization.Empty(firstId),
            ServerProfileOrganization.Empty(secondId),
            ServerProfileOrganization.Empty(thirdId));
        fake.AfterSuccessfulSave = _ => cancellation.Cancel();
        var service = new BulkProfileMetadataMutationService(fake);
        var updates = new List<BulkProfileMetadataUpdate>();

        await service.ExecuteAsync(
            new BulkProfileMetadataRequest(
                [Target(firstId, "one"), Target(secondId, "two"), Target(thirdId, "three")],
                BulkProfileMetadataOperation.MarkFavorite),
            update =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(1, fake.SaveCount);
        Assert.Contains(updates, update =>
            update.ServerProfileId == firstId && update.State == BulkProfileMetadataUpdateState.Succeeded);
        Assert.Contains(updates, update =>
            update.ServerProfileId == secondId && update.State == BulkProfileMetadataUpdateState.Cancelled);
        Assert.Contains(updates, update =>
            update.ServerProfileId == thirdId && update.State == BulkProfileMetadataUpdateState.Cancelled);
    }

    [Fact]
    public void TargetIdentityModelDoesNotExposeSecretOrPrivateKeyFields()
    {
        var names = typeof(BulkProfileMetadataTarget)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
    }

    private static BulkProfileMetadataTarget Target(Guid id, string name) =>
        new(id, name, $"ops@{name}.test:22");

    private sealed class FakeOrganizationService : IServerProfileOrganizationService
    {
        private readonly Dictionary<Guid, ServerProfileOrganization> _organizations;

        public FakeOrganizationService(params ServerProfileOrganization[] organizations)
        {
            _organizations = organizations.ToDictionary(item => item.ServerProfileId);
        }

        public HashSet<Guid> FailOnSave { get; } = [];

        public Action<Guid>? AfterSuccessfulSave { get; set; }

        public int SaveCount { get; private set; }

        public ValueTask<ServerProfileOrganization> GetAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _organizations.GetValueOrDefault(serverProfileId) ?? ServerProfileOrganization.Empty(serverProfileId));
        }

        public ValueTask<ServerProfileOrganization> SaveAsync(
            Guid serverProfileId,
            string? groupName,
            string? commaSeparatedTags,
            bool isFavorite,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOnSave.Contains(serverProfileId))
            {
                throw new InvalidOperationException("sensitive-details-must-not-surface");
            }

            var organization = ServerProfileOrganization.FromCommaSeparatedTags(
                serverProfileId,
                groupName,
                commaSeparatedTags,
                isFavorite);
            _organizations[serverProfileId] = organization;
            SaveCount++;
            AfterSuccessfulSave?.Invoke(serverProfileId);
            return ValueTask.FromResult(organization);
        }

        public ValueTask<IReadOnlyList<OrganizedServerProfile>> SearchAsync(
            ServerProfileSearchFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OrganizedServerProfile>>([]);
    }
}
