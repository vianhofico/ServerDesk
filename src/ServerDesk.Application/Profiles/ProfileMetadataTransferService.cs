using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Profiles;

public static class ProfileMetadataTransferLimits
{
    public const int MaxDocumentBytes = 2_000_000;
    public const int MaxProfiles = 1000;
}

public sealed record ProfileMetadataTransferDocument(
    string Schema,
    int SchemaVersion,
    List<ProfileMetadataTransferEntry> Profiles);

public sealed record ProfileMetadataTransferEntry(
    string Name,
    string Host,
    int Port,
    string Username,
    string? Environment,
    ServerAuthenticationKind AuthenticationKind,
    string? GroupName,
    List<string> Tags,
    bool IsFavorite);

public enum ProfileMetadataImportState
{
    Imported,
    Duplicate,
    Failed,
    Cancelled,
}

public sealed record ProfileMetadataImportUpdate(
    int SourceIndex,
    ProfileMetadataImportState State,
    Guid? ImportedProfileId = null,
    string? FailureKind = null);

public sealed class ProfileMetadataTransferException : Exception
{
    public ProfileMetadataTransferException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IProfileMetadataTransferService
{
    ValueTask<string> ExportAsync(
        IReadOnlyList<Guid> profileIds,
        CancellationToken cancellationToken = default);

    ProfileMetadataTransferDocument Parse(string json);

    Task ImportAsync(
        ProfileMetadataTransferDocument document,
        Func<ProfileMetadataImportUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileMetadataTransferService : IProfileMetadataTransferService
{
    public const string SchemaName = "serverdesk.profile-metadata";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Guid ValidationProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly IProfileRepository _profileRepository;
    private readonly IServerProfileOrganizationRepository _organizationRepository;

    public ProfileMetadataTransferService(
        IProfileRepository profileRepository,
        IServerProfileOrganizationRepository organizationRepository)
    {
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _organizationRepository = organizationRepository ?? throw new ArgumentNullException(nameof(organizationRepository));
    }

    public async ValueTask<string> ExportAsync(
        IReadOnlyList<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        if (profileIds.Count == 0)
        {
            throw new ArgumentException("At least one profile is required for export.", nameof(profileIds));
        }

        if (profileIds.Any(id => id == Guid.Empty) || profileIds.Distinct().Count() != profileIds.Count)
        {
            throw new ArgumentException("Export profile ids must be non-empty and unique.", nameof(profileIds));
        }

        var profiles = await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var organizations = await _organizationRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var byId = profiles.ToDictionary(profile => profile.Id);
        var entries = new List<ProfileMetadataTransferEntry>(profileIds.Count);
        foreach (var profileId in profileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(profileId, out var profile))
            {
                throw new KeyNotFoundException("An export profile was not found.");
            }

            var organization = organizations.GetValueOrDefault(profile.Id) ?? ServerProfileOrganization.Empty(profile.Id);
            entries.Add(new ProfileMetadataTransferEntry(
                profile.Name,
                profile.Host,
                profile.Port,
                profile.Username,
                profile.Environment,
                profile.AuthenticationKind,
                organization.GroupName,
                organization.Tags.ToList(),
                organization.IsFavorite));
        }

        var document = new ProfileMetadataTransferDocument(SchemaName, CurrentSchemaVersion, entries);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > ProfileMetadataTransferLimits.MaxDocumentBytes)
        {
            throw new ProfileMetadataTransferException("The export document exceeds the supported size limit.");
        }

        return json;
    }

    public ProfileMetadataTransferDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > ProfileMetadataTransferLimits.MaxDocumentBytes)
        {
            throw new ProfileMetadataTransferException("The import document exceeds the supported size limit.");
        }

        ProfileMetadataTransferDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileMetadataTransferDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProfileMetadataTransferException("The import document is not valid profile metadata JSON.", exception);
        }

        ValidateDocument(document);
        return document!;
    }

    public async Task ImportAsync(
        ProfileMetadataTransferDocument document,
        Func<ProfileMetadataImportUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishAsync);
        ValidateDocument(document);

        var existing = await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var identities = existing.Select(ProfileIdentity.FromProfile).ToHashSet();
        for (var index = 0; index < document.Profiles.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await PublishCancelledAsync(document.Profiles.Count, index, publishAsync).ConfigureAwait(false);
                return;
            }

            var entry = document.Profiles[index];
            var identity = ProfileIdentity.FromEntry(entry);
            if (identities.Contains(identity))
            {
                await publishAsync(new ProfileMetadataImportUpdate(
                    index,
                    ProfileMetadataImportState.Duplicate)).ConfigureAwait(false);
                continue;
            }

            var profile = CreateCredentiallessProfile(entry);
            var organization = ServerProfileOrganization.Create(
                profile.Id,
                entry.GroupName,
                entry.Tags,
                entry.IsFavorite);
            var profilePersisted = false;
            try
            {
                await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
                profilePersisted = true;
                await _organizationRepository.UpsertAsync(organization, cancellationToken).ConfigureAwait(false);
                identities.Add(identity);
                await publishAsync(new ProfileMetadataImportUpdate(
                    index,
                    ProfileMetadataImportState.Imported,
                    profile.Id)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (profilePersisted)
                {
                    await CompensateProfileAsync(profile.Id).ConfigureAwait(false);
                }

                await publishAsync(new ProfileMetadataImportUpdate(
                    index,
                    ProfileMetadataImportState.Cancelled)).ConfigureAwait(false);
                await PublishCancelledAsync(document.Profiles.Count, index + 1, publishAsync).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                if (profilePersisted)
                {
                    await CompensateProfileAsync(profile.Id).ConfigureAwait(false);
                }

                await publishAsync(new ProfileMetadataImportUpdate(
                    index,
                    ProfileMetadataImportState.Failed,
                    FailureKind: exception.GetType().Name)).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateDocument(ProfileMetadataTransferDocument? document)
    {
        if (document is null ||
            !string.Equals(document.Schema, SchemaName, StringComparison.Ordinal) ||
            document.SchemaVersion != CurrentSchemaVersion ||
            document.Profiles is null ||
            document.Profiles.Count is < 1 or > ProfileMetadataTransferLimits.MaxProfiles)
        {
            throw new ProfileMetadataTransferException("The import document schema is unsupported or incomplete.");
        }

        foreach (var entry in document.Profiles)
        {
            if (entry is null || entry.Tags is null)
            {
                throw new ProfileMetadataTransferException("The import document contains an incomplete profile entry.");
            }

            try
            {
                _ = ServerProfile.Create(
                    ValidationProfileId,
                    entry.Name,
                    entry.Host,
                    entry.Port,
                    entry.Username,
                    entry.Environment,
                    credentialReference: null,
                    entry.AuthenticationKind,
                    privateKeyPath: null);
                _ = ServerProfileOrganization.Create(
                    ValidationProfileId,
                    entry.GroupName,
                    entry.Tags,
                    entry.IsFavorite);
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new ProfileMetadataTransferException("The import document contains invalid profile metadata.", exception);
            }
        }
    }

    private static ServerProfile CreateCredentiallessProfile(ProfileMetadataTransferEntry entry) =>
        ServerProfile.Create(
            entry.Name,
            entry.Host,
            entry.Port,
            entry.Username,
            entry.Environment,
            credentialReference: null,
            entry.AuthenticationKind,
            privateKeyPath: null);

    private async Task CompensateProfileAsync(Guid profileId)
    {
        try
        {
            await _profileRepository.DeleteAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort compensation only. Import result remains failed/cancelled and never exposes exception details.
        }
    }

    private static async Task PublishCancelledAsync(
        int count,
        int startIndex,
        Func<ProfileMetadataImportUpdate, ValueTask> publishAsync)
    {
        for (var index = startIndex; index < count; index++)
        {
            await publishAsync(new ProfileMetadataImportUpdate(
                index,
                ProfileMetadataImportState.Cancelled)).ConfigureAwait(false);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly record struct ProfileIdentity(string Host, int Port, string Username)
    {
        public static ProfileIdentity FromProfile(ServerProfile profile) =>
            Create(profile.Host, profile.Port, profile.Username);

        public static ProfileIdentity FromEntry(ProfileMetadataTransferEntry entry) =>
            Create(entry.Host, entry.Port, entry.Username);

        private static ProfileIdentity Create(string host, int port, string username) =>
            new(host.Trim().ToUpperInvariant(), port, username.Trim().ToUpperInvariant());
    }
}
