using System.Net;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Databases;

public enum DatabaseDiagnosticFailureKind
{
    SecretUnavailable,
    AuthenticationFailed,
    AuthorizationDenied,
    NetworkFailed,
    CapabilityUnavailable,
    ParseFailed,
    Timeout,
    UnsupportedEngine,
    Unexpected,
}

public sealed record DatabaseCatalogItem(
    string Name,
    long? SizeBytes,
    long? ItemCount = null,
    long? ExpiringItemCount = null);

public sealed record DatabaseDiagnosticMetric(string Name, long Value);

public sealed record DatabaseDiagnosticMetadata(string Name, string Value);

public sealed record DatabaseDiagnosticSnapshot(
    DatabaseEngineKind Engine,
    string ServerVersion,
    string? ServerFlavor,
    string? ConnectionIdentity,
    IReadOnlyList<DatabaseCatalogItem> Catalogs,
    IReadOnlyList<DatabaseDiagnosticMetric> Metrics,
    IReadOnlyList<DatabaseDiagnosticMetadata> Metadata,
    bool IsTruncated,
    DateTimeOffset ObservedUtc);

public sealed record DatabaseDiagnosticFailure(
    DatabaseDiagnosticFailureKind Kind,
    string Message,
    RemoteError? RemoteError = null);

public sealed record DatabaseDiagnosticResult(
    DatabaseDiagnosticSnapshot? Snapshot,
    DatabaseDiagnosticFailure? Failure)
{
    public bool IsSuccess => Snapshot is not null && Failure is null;

    public static DatabaseDiagnosticResult Success(DatabaseDiagnosticSnapshot snapshot) =>
        new(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);

    public static DatabaseDiagnosticResult Failed(
        DatabaseDiagnosticFailureKind kind,
        string message,
        RemoteError? remoteError = null) =>
        new(null, new DatabaseDiagnosticFailure(kind, message, remoteError));
}

public sealed record DatabaseDiagnosticOptions(
    int MaxCatalogs,
    int MaxMetadataItems,
    TimeSpan CommandTimeout)
{
    public static DatabaseDiagnosticOptions Default { get; } = new(
        MaxCatalogs: 100,
        MaxMetadataItems: 32,
        CommandTimeout: TimeSpan.FromSeconds(8));

    public int MaxTextLength { get; init; } = 1024;

    public void Validate()
    {
        if (MaxCatalogs is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCatalogs));
        }

        if (MaxMetadataItems is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMetadataItems));
        }

        if (MaxTextLength is < 64 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTextLength));
        }

        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
    }
}

public sealed record DatabaseEngineDiagnosticRequest(
    Guid ProfileId,
    DatabaseEngineKind Engine,
    IPAddress LocalAddress,
    int LocalPort,
    string? DatabaseName,
    string? Username,
    DatabaseAuthenticationKind AuthenticationKind,
    string? Secret,
    DatabaseDiagnosticOptions Options,
    string? AuthenticationDatabase = null,
    DatabaseTlsMode TlsMode = DatabaseTlsMode.Disabled);

public interface IDatabaseEngineDiagnosticAdapter
{
    DatabaseEngineKind Engine { get; }

    Task<DatabaseDiagnosticResult> InspectAsync(
        DatabaseEngineDiagnosticRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseDiagnosticService
{
    Task<DatabaseDiagnosticResult> InspectAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseDiagnosticService : IDatabaseDiagnosticService
{
    private readonly IDatabaseTunnelService _tunnelService;
    private readonly ISecretStore _secretStore;
    private readonly IReadOnlyDictionary<DatabaseEngineKind, IDatabaseEngineDiagnosticAdapter> _adapters;
    private readonly DatabaseDiagnosticOptions _options;

    public DatabaseDiagnosticService(
        IDatabaseTunnelService tunnelService,
        ISecretStore secretStore,
        IEnumerable<IDatabaseEngineDiagnosticAdapter> adapters,
        DatabaseDiagnosticOptions options)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        ArgumentNullException.ThrowIfNull(adapters);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        var resolved = new Dictionary<DatabaseEngineKind, IDatabaseEngineDiagnosticAdapter>();
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!resolved.TryAdd(adapter.Engine, adapter))
            {
                throw new ArgumentException(
                    $"Multiple database diagnostic adapters are registered for {adapter.Engine}.",
                    nameof(adapters));
            }
        }

        _adapters = resolved;
    }

    public async Task<DatabaseDiagnosticResult> InspectAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_adapters.TryGetValue(profile.Engine, out var adapter))
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.UnsupportedEngine,
                $"Authenticated diagnostics are not available for {profile.Engine}.");
        }

        string? secret = null;
        if (profile.AuthenticationKind == DatabaseAuthenticationKind.Password)
        {
            if (profile.CredentialReference is not { } reference)
            {
                return DatabaseDiagnosticResult.Failed(
                    DatabaseDiagnosticFailureKind.SecretUnavailable,
                    "The database profile does not contain a credential reference.");
            }

            try
            {
                secret = await _secretStore.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return DatabaseDiagnosticResult.Failed(
                    DatabaseDiagnosticFailureKind.SecretUnavailable,
                    "The stored database credential could not be read from the secure secret store.");
            }

            if (string.IsNullOrEmpty(secret))
            {
                return DatabaseDiagnosticResult.Failed(
                    DatabaseDiagnosticFailureKind.SecretUnavailable,
                    "The stored database credential is unavailable.");
            }
        }

        try
        {
            await using var tunnel = await _tunnelService.OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            var endpoint = tunnel.Endpoint;
            if (!IPAddress.TryParse(endpoint.LocalHost, out var localAddress) ||
                !IPAddress.IsLoopback(localAddress) ||
                endpoint.LocalPort is < 1 or > 65535)
            {
                return DatabaseDiagnosticResult.Failed(
                    DatabaseDiagnosticFailureKind.NetworkFailed,
                    "Authenticated diagnostics require a valid loopback SSH tunnel endpoint.",
                    new RemoteError(RemoteErrorCode.InvalidEndpoint, "Database tunnel endpoint was not loopback-only."));
            }

            var request = new DatabaseEngineDiagnosticRequest(
                profile.Id,
                profile.Engine,
                localAddress,
                endpoint.LocalPort,
                profile.DatabaseName,
                profile.Username,
                profile.AuthenticationKind,
                secret,
                _options,
                profile.AuthenticationDatabase,
                profile.TlsMode);
            try
            {
                return await adapter.InspectAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return DatabaseDiagnosticResult.Failed(
                    DatabaseDiagnosticFailureKind.Unexpected,
                    $"{profile.Engine} diagnostics failed unexpectedly. No credential details were retained.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseTunnelException exception)
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.NetworkFailed,
                exception.Error.Message,
                exception.Error);
        }
        catch (PortForwardSessionException exception)
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.NetworkFailed,
                exception.Error.Message,
                exception.Error);
        }
    }
}
