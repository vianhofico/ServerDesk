using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Routing;

public sealed record ServerConnectionRouteSpec(
    ServerConnectionRouteKind Kind,
    string? ProxyHost = null,
    int? ProxyPort = null,
    string? ProxyUsername = null,
    Guid? BastionProfileId = null);

public sealed class ServerConnectionRouteValidationException : Exception
{
    public ServerConnectionRouteValidationException(IReadOnlyDictionary<string, string> errors)
        : base("Server connection route contains invalid fields.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string> Errors { get; }
}

public interface IConnectionRouteRepository
{
    ValueTask<ServerConnectionRoute?> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(
        ServerConnectionRoute route,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);
}

public interface IServerConnectionRouteService
{
    ValueTask<ServerConnectionRoute> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<ServerConnectionRoute> SaveAsync(
        Guid serverProfileId,
        ServerConnectionRouteSpec spec,
        string? proxyPassword,
        bool replaceProxyPassword,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);
}

public sealed class ServerConnectionRouteService : IServerConnectionRouteService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IConnectionRouteRepository _routeRepository;
    private readonly ISecretStore _secretStore;

    public ServerConnectionRouteService(
        IProfileRepository profileRepository,
        IConnectionRouteRepository routeRepository,
        ISecretStore secretStore)
    {
        _profileRepository = profileRepository;
        _routeRepository = routeRepository;
        _secretStore = secretStore;
    }

    public async ValueTask<ServerConnectionRoute> GetAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(serverProfileId);
        return await _routeRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false)
            ?? ServerConnectionRoute.Direct(serverProfileId);
    }

    public async ValueTask<ServerConnectionRoute> SaveAsync(
        Guid serverProfileId,
        ServerConnectionRouteSpec spec,
        string? proxyPassword,
        bool replaceProxyPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(serverProfileId);
        ArgumentNullException.ThrowIfNull(spec);

        _ = await _profileRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Server profile was not found.");

        var existing = await _routeRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false)
            ?? ServerConnectionRoute.Direct(serverProfileId);
        await ValidateAsync(
                serverProfileId,
                spec,
                existing,
                proxyPassword,
                replaceProxyPassword,
                cancellationToken)
            .ConfigureAwait(false);

        var desiredReference = ResolveDesiredProxyReference(
            serverProfileId,
            spec.Kind,
            existing,
            proxyPassword,
            replaceProxyPassword);
        var desired = CreateRoute(serverProfileId, spec, desiredReference);

        var oldReference = existing.ProxyCredentialReference;
        var oldSecret = oldReference is null
            ? null
            : await _secretStore.GetAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
        var oldDeleted = false;
        var newWritten = false;

        try
        {
            if (oldReference is not null && oldReference != desiredReference)
            {
                await _secretStore.DeleteAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
                oldDeleted = true;
            }

            if (replaceProxyPassword && desiredReference is not null)
            {
                if (string.IsNullOrEmpty(proxyPassword))
                {
                    await _secretStore.DeleteAsync(desiredReference.Value, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _secretStore.SetAsync(desiredReference.Value, proxyPassword, cancellationToken).ConfigureAwait(false);
                    newWritten = true;
                }
            }

            if (desired.IsDirect)
            {
                await _routeRepository.DeleteAsync(serverProfileId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _routeRepository.UpsertAsync(desired, cancellationToken).ConfigureAwait(false);
            }

            return desired;
        }
        catch
        {
            await RollbackSecretAsync(
                    oldReference,
                    oldSecret,
                    oldDeleted,
                    desiredReference,
                    newWritten)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DeleteAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(serverProfileId);
        var existing = await _routeRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var reference = existing.ProxyCredentialReference;
        var previous = reference is null
            ? null
            : await _secretStore.GetAsync(reference.Value, cancellationToken).ConfigureAwait(false);
        var deletedSecret = false;

        try
        {
            if (reference is not null)
            {
                await _secretStore.DeleteAsync(reference.Value, cancellationToken).ConfigureAwait(false);
                deletedSecret = true;
            }

            await _routeRepository.DeleteAsync(serverProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (deletedSecret && reference is not null && previous is not null)
            {
                await TrySetSecretAsync(reference.Value, previous).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async ValueTask ValidateAsync(
        Guid serverProfileId,
        ServerConnectionRouteSpec spec,
        ServerConnectionRoute existing,
        string? proxyPassword,
        bool replaceProxyPassword,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Enum.IsDefined(spec.Kind))
        {
            errors[nameof(spec.Kind)] = "Connection route type is not supported.";
        }

        if (spec.Kind is ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy)
        {
            if (string.IsNullOrWhiteSpace(spec.ProxyHost))
            {
                errors[nameof(spec.ProxyHost)] = "Proxy host is required.";
            }
            else if (spec.ProxyHost.Any(char.IsWhiteSpace) || spec.ProxyHost.Trim().Length > 255)
            {
                errors[nameof(spec.ProxyHost)] = "Proxy host is invalid.";
            }

            if (spec.ProxyPort is null or < 1 or > 65535)
            {
                errors[nameof(spec.ProxyPort)] = "Proxy port must be between 1 and 65535.";
            }

            if (spec.ProxyUsername?.Trim().Length > 128)
            {
                errors[nameof(spec.ProxyUsername)] = "Proxy username must be 128 characters or fewer.";
            }

            var existingProxy = existing.IsProxy;
            if (!existingProxy && !replaceProxyPassword && !string.IsNullOrEmpty(proxyPassword))
            {
                errors["ProxyPassword"] = "Mark the proxy password for replacement before saving a new proxy secret.";
            }
        }
        else if (replaceProxyPassword && !string.IsNullOrEmpty(proxyPassword))
        {
            errors["ProxyPassword"] = "Only proxy routes can store a proxy password.";
        }

        if (spec.Kind == ServerConnectionRouteKind.Bastion)
        {
            if (spec.BastionProfileId is null || spec.BastionProfileId == Guid.Empty)
            {
                errors[nameof(spec.BastionProfileId)] = "Select a bastion server profile.";
            }
            else if (spec.BastionProfileId == serverProfileId)
            {
                errors[nameof(spec.BastionProfileId)] = "A server cannot use itself as its bastion.";
            }
            else
            {
                var bastion = await _profileRepository.GetAsync(spec.BastionProfileId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (bastion is null)
                {
                    errors[nameof(spec.BastionProfileId)] = "The selected bastion profile no longer exists.";
                }
                else
                {
                    var bastionRoute = await _routeRepository.GetAsync(bastion.Id, cancellationToken).ConfigureAwait(false)
                        ?? ServerConnectionRoute.Direct(bastion.Id);
                    if (bastionRoute.IsBastion)
                    {
                        errors[nameof(spec.BastionProfileId)] =
                            "Nested bastions are not supported in V1. Route the selected bastion directly or through HTTP/SOCKS proxy.";
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ServerConnectionRouteValidationException(errors);
        }
    }

    private static SecretReference? ResolveDesiredProxyReference(
        Guid serverProfileId,
        ServerConnectionRouteKind kind,
        ServerConnectionRoute existing,
        string? proxyPassword,
        bool replaceProxyPassword)
    {
        var isProxy = kind is ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy;
        if (!isProxy)
        {
            return null;
        }

        if (!replaceProxyPassword && existing.IsProxy)
        {
            return existing.ProxyCredentialReference;
        }

        return string.IsNullOrEmpty(proxyPassword)
            ? null
            : SecretReference.ForProxyRoute(serverProfileId);
    }

    private static ServerConnectionRoute CreateRoute(
        Guid serverProfileId,
        ServerConnectionRouteSpec spec,
        SecretReference? proxyReference) =>
        spec.Kind switch
        {
            ServerConnectionRouteKind.Direct => ServerConnectionRoute.Direct(serverProfileId),
            ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy => ServerConnectionRoute.Proxy(
                serverProfileId,
                spec.Kind,
                spec.ProxyHost!,
                spec.ProxyPort!.Value,
                spec.ProxyUsername,
                proxyReference),
            ServerConnectionRouteKind.Bastion => ServerConnectionRoute.Bastion(
                serverProfileId,
                spec.BastionProfileId!.Value),
            _ => throw new InvalidOperationException("Unsupported connection route type."),
        };

    private async ValueTask RollbackSecretAsync(
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

    private async ValueTask TryDeleteSecretAsync(SecretReference reference)
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

    private async ValueTask TrySetSecretAsync(SecretReference reference, string value)
    {
        try
        {
            await _secretStore.SetAsync(reference, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original operation failure. A later repair/audit path can surface cleanup failures.
        }
    }

    private static void ValidateProfileId(Guid serverProfileId)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }
    }
}
