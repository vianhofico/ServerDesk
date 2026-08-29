using ServerDesk.Application.Databases;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Profiles;

public sealed record ServerProfileSpec(
    string Name,
    string Host,
    int Port,
    string Username,
    string? Environment,
    ServerAuthenticationKind AuthenticationKind,
    string? PrivateKeyPath);

public sealed class ServerProfileValidationException : Exception
{
    public ServerProfileValidationException(IReadOnlyDictionary<string, string> errors)
        : base("Server profile contains invalid fields.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string> Errors { get; }
}

public interface IServerProfileService
{
    ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<ServerProfile> CreateAsync(
        ServerProfileSpec spec,
        string? initialSecret,
        CancellationToken cancellationToken = default);

    ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ServerProfileService : IServerProfileService
{
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretStore _secretStore;
    private readonly IConnectionRouteRepository? _connectionRouteRepository;
    private readonly IDatabaseProfileRepository? _databaseProfileRepository;

    public ServerProfileService(
        IProfileRepository profileRepository,
        ISecretStore secretStore,
        IConnectionRouteRepository? connectionRouteRepository = null,
        IDatabaseProfileRepository? databaseProfileRepository = null)
    {
        _profileRepository = profileRepository;
        _secretStore = secretStore;
        _connectionRouteRepository = connectionRouteRepository;
        _databaseProfileRepository = databaseProfileRepository;
    }

    public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        _profileRepository.ListAsync(cancellationToken);

    public async ValueTask<ServerProfile> CreateAsync(
        ServerProfileSpec spec,
        string? initialSecret,
        CancellationToken cancellationToken = default)
    {
        Validate(spec, existing: null, replaceSecret: !string.IsNullOrEmpty(initialSecret), initialSecret);

        var id = Guid.NewGuid();
        var credentialReference = NeedsCredentialReference(spec.AuthenticationKind, initialSecret)
            ? SecretReference.ForServerProfile(id)
            : (SecretReference?)null;

        var profile = CreateProfile(id, spec, credentialReference);
        if (credentialReference is not null)
        {
            await _secretStore.SetAsync(credentialReference.Value, initialSecret!, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            if (credentialReference is not null)
            {
                await TryDeleteSecretForCompensationAsync(credentialReference.Value).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        var existing = await _profileRepository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Server profile was not found.");

        Validate(spec, existing, replaceSecret, replacementSecret);

        var authenticationChanged = existing.AuthenticationKind != spec.AuthenticationKind;
        var desiredReference = ResolveDesiredReference(
            existing,
            spec.AuthenticationKind,
            replacementSecret,
            replaceSecret,
            authenticationChanged);

        var oldReference = existing.CredentialReference;
        var secretStateChanged = replaceSecret || oldReference != desiredReference;
        if (!secretStateChanged)
        {
            var unchangedSecretProfile = CreateProfile(id, spec, desiredReference);
            await _profileRepository.UpsertAsync(unchangedSecretProfile, cancellationToken).ConfigureAwait(false);
            return unchangedSecretProfile;
        }

        var oldSecret = oldReference is null
            ? null
            : await _secretStore.GetAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
        var oldReferenceDeleted = false;
        var desiredReferenceWritten = false;

        try
        {
            if (oldReference is not null && oldReference != desiredReference)
            {
                await _secretStore.DeleteAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
                oldReferenceDeleted = true;
            }

            if (replaceSecret && desiredReference is not null)
            {
                await _secretStore.SetAsync(desiredReference.Value, replacementSecret!, cancellationToken)
                    .ConfigureAwait(false);
                desiredReferenceWritten = true;
            }

            var updated = CreateProfile(id, spec, desiredReference);
            await _profileRepository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            await TryRollbackSecretAsync(
                    oldReference,
                    oldSecret,
                    oldReferenceDeleted,
                    desiredReference,
                    desiredReferenceWritten)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        var existing = await _profileRepository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var route = _connectionRouteRepository is null
            ? null
            : await _connectionRouteRepository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var databaseProfiles = _databaseProfileRepository is null
            ? []
            : await _databaseProfileRepository.ListForServerAsync(id, cancellationToken).ConfigureAwait(false);
        var references = new[]
            {
                existing.CredentialReference,
                route?.ProxyCredentialReference,
            }
            .Concat(databaseProfiles.Select(profile => profile.CredentialReference))
            .Where(reference => reference is not null)
            .Select(reference => reference!.Value)
            .Distinct()
            .ToArray();
        var previousSecrets = new Dictionary<SecretReference, string?>();

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
    }

    private static ServerProfile CreateProfile(
        Guid id,
        ServerProfileSpec spec,
        SecretReference? credentialReference) =>
        ServerProfile.Create(
            id,
            spec.Name,
            spec.Host,
            spec.Port,
            spec.Username,
            spec.Environment,
            credentialReference,
            spec.AuthenticationKind,
            spec.PrivateKeyPath);

    private static SecretReference? ResolveDesiredReference(
        ServerProfile existing,
        ServerAuthenticationKind authenticationKind,
        string? replacementSecret,
        bool replaceSecret,
        bool authenticationChanged)
    {
        if (authenticationKind is ServerAuthenticationKind.SshAgent or ServerAuthenticationKind.KeyboardInteractive)
        {
            return null;
        }

        if (!authenticationChanged && !replaceSecret)
        {
            return existing.CredentialReference;
        }

        if (!string.IsNullOrEmpty(replacementSecret))
        {
            return SecretReference.ForServerProfile(existing.Id);
        }

        return null;
    }

    private static bool NeedsCredentialReference(
        ServerAuthenticationKind authenticationKind,
        string? secret) =>
        authenticationKind == ServerAuthenticationKind.Password ||
        (authenticationKind == ServerAuthenticationKind.PrivateKey && !string.IsNullOrEmpty(secret));

    private async ValueTask TryRollbackSecretAsync(
        SecretReference? oldReference,
        string? oldSecret,
        bool oldReferenceDeleted,
        SecretReference? desiredReference,
        bool desiredReferenceWritten)
    {
        if (desiredReferenceWritten && desiredReference is not null && desiredReference != oldReference)
        {
            await TryDeleteSecretForCompensationAsync(desiredReference.Value).ConfigureAwait(false);
        }

        if (oldReference is not null && oldSecret is not null &&
            (oldReferenceDeleted || desiredReference == oldReference))
        {
            await TrySetSecretForCompensationAsync(oldReference.Value, oldSecret).ConfigureAwait(false);
        }
    }

    private async ValueTask TryDeleteSecretForCompensationAsync(SecretReference reference)
    {
        try
        {
            await _secretStore.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original operation failure. A later repair/audit path can surface cleanup failures.
        }
    }

    private async ValueTask TrySetSecretForCompensationAsync(SecretReference reference, string secret)
    {
        try
        {
            await _secretStore.SetAsync(reference, secret, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original operation failure. A later repair/audit path can surface cleanup failures.
        }
    }

    private static void Validate(
        ServerProfileSpec spec,
        ServerProfile? existing,
        bool replaceSecret,
        string? secret)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        ValidateRequired(spec.Name, 100, nameof(spec.Name), "Name", errors);
        ValidateRequired(spec.Host, 255, nameof(spec.Host), "Host", errors);
        ValidateRequired(spec.Username, 128, nameof(spec.Username), "Username", errors);

        if (!string.IsNullOrWhiteSpace(spec.Host) && spec.Host.Any(char.IsWhiteSpace))
        {
            errors[nameof(spec.Host)] = "Host cannot contain whitespace.";
        }

        if (spec.Port is < 1 or > 65535)
        {
            errors[nameof(spec.Port)] = "Port must be between 1 and 65535.";
        }

        if (spec.Environment?.Trim().Length > 64)
        {
            errors[nameof(spec.Environment)] = "Environment must be 64 characters or fewer.";
        }

        if (!Enum.IsDefined(spec.AuthenticationKind))
        {
            errors[nameof(spec.AuthenticationKind)] = "Authentication type is not supported.";
        }

        var authenticationChanged = existing is not null && existing.AuthenticationKind != spec.AuthenticationKind;
        switch (spec.AuthenticationKind)
        {
            case ServerAuthenticationKind.Password:
                if (existing is null && string.IsNullOrEmpty(secret))
                {
                    errors["Secret"] = "Password is required for a new password profile.";
                }
                else if (authenticationChanged && !replaceSecret)
                {
                    errors["Secret"] = "Enter a password when switching to password authentication.";
                }
                else if (replaceSecret && string.IsNullOrEmpty(secret))
                {
                    errors["Secret"] = "Password cannot be empty.";
                }
                else if (existing is not null && !authenticationChanged && !replaceSecret && existing.CredentialReference is null)
                {
                    errors["Secret"] = "This profile has no stored password. Enter a password before saving.";
                }

                break;

            case ServerAuthenticationKind.PrivateKey:
                if (string.IsNullOrWhiteSpace(spec.PrivateKeyPath))
                {
                    errors[nameof(spec.PrivateKeyPath)] = "Private key path is required.";
                }
                else if (spec.PrivateKeyPath.Trim().Length > 1024)
                {
                    errors[nameof(spec.PrivateKeyPath)] = "Private key path is too long.";
                }

                break;

            case ServerAuthenticationKind.SshAgent:
            case ServerAuthenticationKind.KeyboardInteractive:
                if (replaceSecret && !string.IsNullOrEmpty(secret))
                {
                    errors["Secret"] = "This authentication type does not persist a secret.";
                }

                break;
        }

        if (errors.Count > 0)
        {
            throw new ServerProfileValidationException(errors);
        }
    }

    private static void ValidateRequired(
        string value,
        int maxLength,
        string key,
        string displayName,
        IDictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = $"{displayName} is required.";
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            errors[key] = $"{displayName} must be {maxLength} characters or fewer.";
        }
    }
}
