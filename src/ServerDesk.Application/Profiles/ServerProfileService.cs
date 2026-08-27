using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Profiles;

public sealed record ServerRouteSpec(
    ServerRouteKind Kind = ServerRouteKind.Direct,
    string? ProxyHost = null,
    int? ProxyPort = null,
    string? ProxyUsername = null,
    Guid? BastionProfileId = null);

public sealed record ServerProfileSpec(
    string Name,
    string Host,
    int Port,
    string Username,
    string? Environment,
    ServerAuthenticationKind AuthenticationKind,
    string? PrivateKeyPath,
    ServerRouteSpec? Route = null);

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

    ValueTask<ServerProfile> CreateAsync(
        ServerProfileSpec spec,
        string? initialSecret,
        string? initialProxySecret,
        CancellationToken cancellationToken = default);

    ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default);

    ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        string? replacementProxySecret,
        bool replaceProxySecret,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ServerProfileService : IServerProfileService
{
    private readonly IProfileRepository _profileRepository;
    private readonly ISecretStore _secretStore;

    public ServerProfileService(IProfileRepository profileRepository, ISecretStore secretStore)
    {
        _profileRepository = profileRepository;
        _secretStore = secretStore;
    }

    public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        _profileRepository.ListAsync(cancellationToken);

    public ValueTask<ServerProfile> CreateAsync(
        ServerProfileSpec spec,
        string? initialSecret,
        CancellationToken cancellationToken = default) =>
        CreateAsync(spec, initialSecret, initialProxySecret: null, cancellationToken);

    public async ValueTask<ServerProfile> CreateAsync(
        ServerProfileSpec spec,
        string? initialSecret,
        string? initialProxySecret,
        CancellationToken cancellationToken = default)
    {
        Validate(spec, existing: null, replaceSecret: !string.IsNullOrEmpty(initialSecret), initialSecret);

        var id = Guid.NewGuid();
        var credentialReference = NeedsCredentialReference(spec.AuthenticationKind, initialSecret)
            ? SecretReference.ForServerProfile(id)
            : (SecretReference?)null;
        var route = await BuildRouteAsync(
                id,
                spec.Route,
                existing: null,
                initialProxySecret,
                replaceProxySecret: !string.IsNullOrEmpty(initialProxySecret),
                cancellationToken)
            .ConfigureAwait(false);

        var profile = CreateProfile(id, spec, credentialReference, route);
        var written = new List<SecretReference>();
        try
        {
            if (credentialReference is not null)
            {
                await _secretStore.SetAsync(credentialReference.Value, initialSecret!, cancellationToken)
                    .ConfigureAwait(false);
                written.Add(credentialReference.Value);
            }

            if (route.ProxyCredentialReference is not null)
            {
                await _secretStore.SetAsync(route.ProxyCredentialReference.Value, initialProxySecret!, cancellationToken)
                    .ConfigureAwait(false);
                written.Add(route.ProxyCredentialReference.Value);
            }

            await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            foreach (var reference in written)
            {
                await TryDeleteSecretForCompensationAsync(reference).ConfigureAwait(false);
            }

            throw;
        }
    }

    public ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            spec,
            replacementSecret,
            replaceSecret,
            replacementProxySecret: null,
            replaceProxySecret: false,
            cancellationToken);

    public async ValueTask<ServerProfile> UpdateAsync(
        Guid id,
        ServerProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        string? replacementProxySecret,
        bool replaceProxySecret,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        var existing = await _profileRepository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Server profile was not found.");

        Validate(spec, existing, replaceSecret, replacementSecret);
        var route = await BuildRouteAsync(
                id,
                spec.Route,
                existing,
                replacementProxySecret,
                replaceProxySecret,
                cancellationToken)
            .ConfigureAwait(false);

        var authenticationChanged = existing.AuthenticationKind != spec.AuthenticationKind;
        var desiredReference = ResolveDesiredReference(
            existing,
            spec.AuthenticationKind,
            replacementSecret,
            replaceSecret,
            authenticationChanged);
        var updated = CreateProfile(id, spec, desiredReference, route);

        var oldCredentialSecret = await ReadSecretAsync(existing.CredentialReference, cancellationToken).ConfigureAwait(false);
        var oldProxySecret = await ReadSecretAsync(existing.Route.ProxyCredentialReference, cancellationToken).ConfigureAwait(false);

        try
        {
            await ApplySecretChangeAsync(
                    existing.CredentialReference,
                    desiredReference,
                    replacementSecret,
                    replaceSecret,
                    cancellationToken)
                .ConfigureAwait(false);
            await ApplySecretChangeAsync(
                    existing.Route.ProxyCredentialReference,
                    route.ProxyCredentialReference,
                    replacementProxySecret,
                    replaceProxySecret,
                    cancellationToken)
                .ConfigureAwait(false);

            await _profileRepository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            await RestoreSecretStateAsync(
                    existing.CredentialReference,
                    oldCredentialSecret,
                    desiredReference)
                .ConfigureAwait(false);
            await RestoreSecretStateAsync(
                    existing.Route.ProxyCredentialReference,
                    oldProxySecret,
                    route.ProxyCredentialReference)
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

        var profiles = await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var dependent = profiles.FirstOrDefault(profile => profile.Route.BastionProfileId == id);
        if (dependent is not null)
        {
            throw new InvalidOperationException(
                $"Server '{existing.Name}' cannot be deleted because '{dependent.Name}' uses it as a bastion. Change the dependent route first.");
        }

        var credentialSecret = await ReadSecretAsync(existing.CredentialReference, cancellationToken).ConfigureAwait(false);
        var proxySecret = await ReadSecretAsync(existing.Route.ProxyCredentialReference, cancellationToken).ConfigureAwait(false);

        if (existing.CredentialReference is not null)
        {
            await _secretStore.DeleteAsync(existing.CredentialReference.Value, cancellationToken).ConfigureAwait(false);
        }

        if (existing.Route.ProxyCredentialReference is not null)
        {
            await _secretStore.DeleteAsync(existing.Route.ProxyCredentialReference.Value, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _profileRepository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (existing.CredentialReference is not null && credentialSecret is not null)
            {
                await TrySetSecretForCompensationAsync(existing.CredentialReference.Value, credentialSecret).ConfigureAwait(false);
            }

            if (existing.Route.ProxyCredentialReference is not null && proxySecret is not null)
            {
                await TrySetSecretForCompensationAsync(existing.Route.ProxyCredentialReference.Value, proxySecret).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async ValueTask<ServerConnectionRoute> BuildRouteAsync(
        Guid profileId,
        ServerRouteSpec? routeSpec,
        ServerProfile? existing,
        string? proxySecret,
        bool replaceProxySecret,
        CancellationToken cancellationToken)
    {
        var spec = routeSpec ?? new ServerRouteSpec();
        if (!Enum.IsDefined(spec.Kind))
        {
            throw Validation(nameof(ServerRouteSpec.Kind), "Connection route type is not supported.");
        }

        try
        {
            switch (spec.Kind)
            {
                case ServerRouteKind.Direct:
                    return ServerConnectionRoute.Direct;

                case ServerRouteKind.HttpProxy:
                case ServerRouteKind.Socks4Proxy:
                case ServerRouteKind.Socks5Proxy:
                    var oldRoute = existing?.Route;
                    var usernameChanged = oldRoute?.IsProxy == true &&
                        !string.Equals(oldRoute.ProxyUsername, NormalizeOptional(spec.ProxyUsername), StringComparison.Ordinal);
                    if (usernameChanged && oldRoute?.ProxyCredentialReference is not null && !replaceProxySecret)
                    {
                        throw Validation(
                            nameof(ServerRouteSpec.ProxyUsername),
                            "Re-enter or clear the proxy password when changing the proxy username.");
                    }

                    var proxyReference = ResolveProxyReference(
                        profileId,
                        existing,
                        proxySecret,
                        replaceProxySecret);
                    return ServerConnectionRoute.Proxy(
                        spec.Kind,
                        spec.ProxyHost ?? string.Empty,
                        spec.ProxyPort ?? 0,
                        spec.ProxyUsername,
                        proxyReference);

                case ServerRouteKind.Bastion:
                    if (spec.BastionProfileId is not { } bastionId || bastionId == Guid.Empty)
                    {
                        throw Validation(nameof(ServerRouteSpec.BastionProfileId), "Select a bastion server profile.");
                    }

                    if (bastionId == profileId)
                    {
                        throw Validation(nameof(ServerRouteSpec.BastionProfileId), "A server cannot use itself as its bastion.");
                    }

                    var bastion = await _profileRepository.GetAsync(bastionId, cancellationToken).ConfigureAwait(false);
                    if (bastion is null)
                    {
                        throw Validation(nameof(ServerRouteSpec.BastionProfileId), "The selected bastion profile no longer exists.");
                    }

                    if (bastion.Route.Kind == ServerRouteKind.Bastion)
                    {
                        throw Validation(
                            nameof(ServerRouteSpec.BastionProfileId),
                            "Nested bastions are not supported in this release. Choose a direct or proxy-routed bastion.");
                    }

                    return ServerConnectionRoute.Bastion(bastionId);

                default:
                    throw Validation(nameof(ServerRouteSpec.Kind), "Connection route type is not supported.");
            }
        }
        catch (ServerProfileValidationException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw Validation("Route", exception.Message);
        }
    }

    private static ServerProfile CreateProfile(
        Guid id,
        ServerProfileSpec spec,
        SecretReference? credentialReference,
        ServerConnectionRoute route) =>
        ServerProfile.Create(
            id,
            spec.Name,
            spec.Host,
            spec.Port,
            spec.Username,
            spec.Environment,
            credentialReference,
            spec.AuthenticationKind,
            spec.PrivateKeyPath,
            route);

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

    private static SecretReference? ResolveProxyReference(
        Guid profileId,
        ServerProfile? existing,
        string? replacementSecret,
        bool replaceSecret)
    {
        if (replaceSecret)
        {
            return string.IsNullOrEmpty(replacementSecret)
                ? null
                : SecretReference.ForServerProxy(profileId);
        }

        return existing?.Route.IsProxy == true
            ? existing.Route.ProxyCredentialReference
            : null;
    }

    private static bool NeedsCredentialReference(
        ServerAuthenticationKind authenticationKind,
        string? secret) =>
        authenticationKind == ServerAuthenticationKind.Password ||
        (authenticationKind == ServerAuthenticationKind.PrivateKey && !string.IsNullOrEmpty(secret));

    private async ValueTask<string?> ReadSecretAsync(
        SecretReference? reference,
        CancellationToken cancellationToken) =>
        reference is null
            ? null
            : await _secretStore.GetAsync(reference.Value, cancellationToken).ConfigureAwait(false);

    private async ValueTask ApplySecretChangeAsync(
        SecretReference? oldReference,
        SecretReference? desiredReference,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken)
    {
        if (oldReference is not null && oldReference != desiredReference)
        {
            await _secretStore.DeleteAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
        }

        if (replaceSecret && desiredReference is not null)
        {
            await _secretStore.SetAsync(desiredReference.Value, replacementSecret!, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RestoreSecretStateAsync(
        SecretReference? oldReference,
        string? oldSecret,
        SecretReference? attemptedReference)
    {
        if (attemptedReference is not null)
        {
            await TryDeleteSecretForCompensationAsync(attemptedReference.Value).ConfigureAwait(false);
        }

        if (oldReference is not null && oldSecret is not null)
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

    private static ServerProfileValidationException Validation(string key, string message) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = message });

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
