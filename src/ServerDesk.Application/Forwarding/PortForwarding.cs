using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Forwarding;

public enum PortForwardKind
{
    Local = 0,
    Remote = 1,
    Dynamic = 2,
}

public enum PortForwardState
{
    Stopped,
    Starting,
    Active,
    Stopping,
    Faulted,
}

public enum PortForwardBindScope
{
    LocalMachine,
    RemoteServer,
}

public sealed record PortForwardSpec(
    Guid ServerProfileId,
    string Name,
    PortForwardKind Kind,
    string BindHost,
    int BindPort,
    string? TargetHost,
    int? TargetPort);

public sealed record PortForwardProfile(
    Guid Id,
    Guid ServerProfileId,
    string Name,
    PortForwardKind Kind,
    string BindHost,
    int BindPort,
    string? TargetHost,
    int? TargetPort)
{
    public PortForwardBindScope BindScope =>
        Kind == PortForwardKind.Remote ? PortForwardBindScope.RemoteServer : PortForwardBindScope.LocalMachine;

    public bool IsLoopbackBind => IsLoopbackHost(BindHost);

    public string BindEndpoint => $"{BindHost}:{BindPort}";

    public string TargetEndpoint => Kind == PortForwardKind.Dynamic
        ? "SOCKS destination selected by client"
        : $"{TargetHost}:{TargetPort}";

    public static PortForwardProfile Create(PortForwardSpec spec, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = Validate(spec);
        if (errors.Count > 0)
        {
            throw new PortForwardValidationException(errors);
        }

        return new PortForwardProfile(
            id ?? Guid.NewGuid(),
            spec.ServerProfileId,
            spec.Name.Trim(),
            spec.Kind,
            NormalizeHost(spec.BindHost),
            spec.BindPort,
            spec.Kind == PortForwardKind.Dynamic ? null : NormalizeHost(spec.TargetHost!),
            spec.Kind == PortForwardKind.Dynamic ? null : spec.TargetPort);
    }

    public static PortForwardProfile Rehydrate(
        Guid id,
        Guid serverProfileId,
        string name,
        PortForwardKind kind,
        string bindHost,
        int bindPort,
        string? targetHost,
        int? targetPort) =>
        Create(new PortForwardSpec(serverProfileId, name, kind, bindHost, bindPort, targetHost, targetPort), id);

    public static string CollisionKey(PortForwardProfile profile) =>
        $"{profile.BindScope}|{profile.BindHost.ToUpperInvariant()}|{profile.BindPort}";

    public static bool IsLoopbackHost(string host)
    {
        var normalized = NormalizeHost(host);
        return normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> Validate(PortForwardSpec spec)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec.ServerProfileId == Guid.Empty)
        {
            errors[nameof(spec.ServerProfileId)] = "A server profile is required.";
        }

        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Trim().Length > 100)
        {
            errors[nameof(spec.Name)] = "Name must be between 1 and 100 characters.";
        }

        if (!IsValidHost(spec.BindHost))
        {
            errors[nameof(spec.BindHost)] = "Bind host is required and must be 255 characters or fewer.";
        }

        if (spec.BindPort is < 1 or > 65535)
        {
            errors[nameof(spec.BindPort)] = "Bind port must be between 1 and 65535.";
        }

        if (!Enum.IsDefined(spec.Kind))
        {
            errors[nameof(spec.Kind)] = "Forward type is invalid.";
        }

        if (spec.Kind == PortForwardKind.Dynamic)
        {
            if (!string.IsNullOrWhiteSpace(spec.TargetHost) || spec.TargetPort is not null)
            {
                errors[nameof(spec.TargetHost)] = "Dynamic SOCKS forwarding does not use a fixed target.";
            }
        }
        else
        {
            if (!IsValidHost(spec.TargetHost))
            {
                errors[nameof(spec.TargetHost)] = "Target host is required and must be 255 characters or fewer.";
            }

            if (spec.TargetPort is not >= 1 or > 65535)
            {
                errors[nameof(spec.TargetPort)] = "Target port must be between 1 and 65535.";
            }
        }

        return errors;
    }

    private static bool IsValidHost(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 255;

    private static string NormalizeHost(string value) => value.Trim();
}

public sealed class PortForwardValidationException : Exception
{
    public PortForwardValidationException(IReadOnlyDictionary<string, string> errors)
        : base("Port forward configuration is invalid.")
    {
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    public IReadOnlyDictionary<string, string> Errors { get; }
}

public sealed class PortForwardSessionException : Exception
{
    public PortForwardSessionException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IPortForwardRepository
{
    ValueTask<IReadOnlyList<PortForwardProfile>> ListByServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<PortForwardProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(PortForwardProfile profile, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IPortForwardProfileService
{
    ValueTask<IReadOnlyList<PortForwardProfile>> ListAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<PortForwardProfile> SaveAsync(
        PortForwardSpec spec,
        Guid? existingId = null,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class PortForwardProfileService : IPortForwardProfileService
{
    private readonly IPortForwardRepository _repository;

    public PortForwardProfileService(IPortForwardRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ValueTask<IReadOnlyList<PortForwardProfile>> ListAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _repository.ListByServerAsync(serverProfileId, cancellationToken);

    public async ValueTask<PortForwardProfile> SaveAsync(
        PortForwardSpec spec,
        Guid? existingId = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = PortForwardProfile.Create(spec, existingId);
        var siblings = await _repository.ListByServerAsync(spec.ServerProfileId, cancellationToken)
            .ConfigureAwait(false);
        var collisionKey = PortForwardProfile.CollisionKey(candidate);
        if (siblings.Any(profile =>
                profile.Id != candidate.Id &&
                PortForwardProfile.CollisionKey(profile).Equals(collisionKey, StringComparison.Ordinal)))
        {
            throw new PortForwardValidationException(new Dictionary<string, string>
            {
                [nameof(spec.BindPort)] =
                    "Another saved tunnel uses the same bind host and port on the same side of this SSH connection.",
            });
        }

        await _repository.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(id, cancellationToken);
}

public interface IPortForwardSession : IAsyncDisposable
{
    Guid ProfileId { get; }

    Guid ServerProfileId { get; }

    PortForwardState State { get; }

    RemoteError? LastError { get; }

    event Action<PortForwardState>? StateChanged;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IPortForwardSessionFactory
{
    IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile);
}
