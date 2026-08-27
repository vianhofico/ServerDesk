namespace ServerDesk.Application.Profiles;

public sealed record ServerProfileOrganization
{
    private const int MaxTags = 20;

    private ServerProfileOrganization(
        Guid serverProfileId,
        string? groupName,
        IReadOnlyList<string> tags,
        bool isFavorite)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        if (groupName?.Trim().Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(groupName), "Group name must be 64 characters or fewer.");
        }

        var normalizedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTags.Length > MaxTags)
        {
            throw new ArgumentOutOfRangeException(nameof(tags), $"A profile may have at most {MaxTags} tags.");
        }

        if (normalizedTags.Any(tag => tag.Length > 32))
        {
            throw new ArgumentOutOfRangeException(nameof(tags), "Each tag must be 32 characters or fewer.");
        }

        ServerProfileId = serverProfileId;
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        Tags = normalizedTags;
        IsFavorite = isFavorite;
    }

    public Guid ServerProfileId { get; }

    public string? GroupName { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool IsFavorite { get; }

    public static ServerProfileOrganization Empty(Guid serverProfileId) =>
        new(serverProfileId, null, [], false);

    public static ServerProfileOrganization Create(
        Guid serverProfileId,
        string? groupName,
        IEnumerable<string>? tags,
        bool isFavorite) =>
        new(serverProfileId, groupName, tags?.ToArray() ?? [], isFavorite);

    public static ServerProfileOrganization FromCommaSeparatedTags(
        Guid serverProfileId,
        string? groupName,
        string? tags,
        bool isFavorite) =>
        Create(
            serverProfileId,
            groupName,
            string.IsNullOrWhiteSpace(tags)
                ? []
                : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            isFavorite);
}

public sealed record ServerProfileSearchFilter(
    string? Query = null,
    string? GroupName = null,
    string? Tag = null,
    string? Environment = null,
    bool FavoritesOnly = false);

public interface IServerProfileOrganizationRepository
{
    ValueTask<ServerProfileOrganization> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<Guid, ServerProfileOrganization>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(
        ServerProfileOrganization organization,
        CancellationToken cancellationToken = default);
}

public interface IServerProfileOrganizationService
{
    ValueTask<ServerProfileOrganization> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<ServerProfileOrganization> SaveAsync(
        Guid serverProfileId,
        string? groupName,
        string? commaSeparatedTags,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OrganizedServerProfile>> SearchAsync(
        ServerProfileSearchFilter filter,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizedServerProfile(
    ServerDesk.Domain.Servers.ServerProfile Profile,
    ServerProfileOrganization Organization);

public sealed class ServerProfileOrganizationService : IServerProfileOrganizationService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IServerProfileOrganizationRepository _organizationRepository;

    public ServerProfileOrganizationService(
        IProfileRepository profileRepository,
        IServerProfileOrganizationRepository organizationRepository)
    {
        _profileRepository = profileRepository;
        _organizationRepository = organizationRepository;
    }

    public ValueTask<ServerProfileOrganization> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _organizationRepository.GetAsync(serverProfileId, cancellationToken);

    public async ValueTask<ServerProfileOrganization> SaveAsync(
        Guid serverProfileId,
        string? groupName,
        string? commaSeparatedTags,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        _ = await _profileRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Server profile was not found.");
        var organization = ServerProfileOrganization.FromCommaSeparatedTags(
            serverProfileId,
            groupName,
            commaSeparatedTags,
            isFavorite);
        await _organizationRepository.UpsertAsync(organization, cancellationToken).ConfigureAwait(false);
        return organization;
    }

    public async ValueTask<IReadOnlyList<OrganizedServerProfile>> SearchAsync(
        ServerProfileSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var profiles = await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var organizations = await _organizationRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var query = filter.Query?.Trim();
        var group = filter.GroupName?.Trim();
        var tag = filter.Tag?.Trim();
        var environment = filter.Environment?.Trim();

        return profiles
            .Select(profile => new OrganizedServerProfile(
                profile,
                organizations.GetValueOrDefault(profile.Id) ?? ServerProfileOrganization.Empty(profile.Id)))
            .Where(item => !filter.FavoritesOnly || item.Organization.IsFavorite)
            .Where(item => string.IsNullOrWhiteSpace(group) ||
                string.Equals(item.Organization.GroupName, group, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(tag) ||
                item.Organization.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(environment) ||
                string.Equals(item.Profile.Environment, environment, StringComparison.OrdinalIgnoreCase))
            .Where(item => MatchesQuery(item, query))
            .OrderByDescending(item => item.Organization.IsFavorite)
            .ThenBy(item => item.Organization.GroupName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesQuery(OrganizedServerProfile item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Contains(item.Profile.Name, query) ||
               Contains(item.Profile.Host, query) ||
               Contains(item.Profile.Username, query) ||
               Contains(item.Profile.Environment, query) ||
               Contains(item.Organization.GroupName, query) ||
               item.Organization.Tags.Any(value => Contains(value, query));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
