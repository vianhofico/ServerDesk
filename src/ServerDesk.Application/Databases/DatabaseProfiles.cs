using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;

namespace ServerDesk.Application.Databases;

public enum DatabaseAuthenticationKind
{
    None,
    Password,
}

public enum DatabaseTlsMode
{
    Disabled,
    Required,
}

public sealed class DatabaseConnectionProfile
{
    private DatabaseConnectionProfile(
        Guid id,
        Guid serverProfileId,
        string name,
        DatabaseEngineKind engine,
        string remoteHost,
        int remotePort,
        string? databaseName,
        string? username,
        DatabaseAuthenticationKind authenticationKind,
        SecretReference? credentialReference,
        string? authenticationDatabase,
        DatabaseTlsMode tlsMode)
    {
        Id = id;
        ServerProfileId = serverProfileId;
        Name = name;
        Engine = engine;
        RemoteHost = remoteHost;
        RemotePort = remotePort;
        DatabaseName = databaseName;
        Username = username;
        AuthenticationKind = authenticationKind;
        CredentialReference = credentialReference;
        AuthenticationDatabase = authenticationDatabase;
        TlsMode = tlsMode;
    }

    public Guid Id { get; }

    public Guid ServerProfileId { get; }

    public string Name { get; }

    public DatabaseEngineKind Engine { get; }

    public string RemoteHost { get; }

    public int RemotePort { get; }

    public string? DatabaseName { get; }

    public string? Username { get; }

    public DatabaseAuthenticationKind AuthenticationKind { get; }

    public SecretReference? CredentialReference { get; }

    public string? AuthenticationDatabase { get; }

    public DatabaseTlsMode TlsMode { get; }

    public static DatabaseConnectionProfile Create(
        Guid id,
        Guid serverProfileId,
        string name,
        DatabaseEngineKind engine,
        string remoteHost,
        int remotePort,
        string? databaseName,
        string? username,
        DatabaseAuthenticationKind authenticationKind,
        SecretReference? credentialReference,
        string? authenticationDatabase = null,
        DatabaseTlsMode tlsMode = DatabaseTlsMode.Disabled)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(id));
        }

        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        if (!Enum.IsDefined(engine))
        {
            throw new ArgumentOutOfRangeException(nameof(engine));
        }

        if (!Enum.IsDefined(authenticationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationKind));
        }

        if (!Enum.IsDefined(tlsMode))
        {
            throw new ArgumentOutOfRangeException(nameof(tlsMode));
        }

        name = NormalizeRequired(name, nameof(name), 100);
        remoteHost = NormalizeRequired(remoteHost, nameof(remoteHost), 255);
        if (remoteHost.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Remote database host cannot contain whitespace.", nameof(remoteHost));
        }

        if (remotePort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remotePort),
                "Remote database port must be between 1 and 65535.");
        }

        databaseName = NormalizeOptional(databaseName, nameof(databaseName), 128);
        username = NormalizeOptional(username, nameof(username), 128);
        authenticationDatabase = NormalizeOptional(authenticationDatabase, nameof(authenticationDatabase), 128);

        if (authenticationKind == DatabaseAuthenticationKind.None && credentialReference is not null)
        {
            throw new ArgumentException(
                "A no-password database profile cannot retain a credential reference.",
                nameof(credentialReference));
        }

        if (authenticationKind == DatabaseAuthenticationKind.Password && credentialReference is null)
        {
            throw new ArgumentException(
                "A password database profile requires a credential reference.",
                nameof(credentialReference));
        }

        if (engine == DatabaseEngineKind.MongoDb)
        {
            if (authenticationKind == DatabaseAuthenticationKind.Password)
            {
                authenticationDatabase ??= "admin";
            }
            else if (authenticationDatabase is not null)
            {
                throw new ArgumentException(
                    "A MongoDB profile without authentication cannot retain an authentication database.",
                    nameof(authenticationDatabase));
            }
        }
        else if (authenticationDatabase is not null || tlsMode != DatabaseTlsMode.Disabled)
        {
            throw new ArgumentException(
                "Authentication database and TLS profile metadata are currently supported only for MongoDB profiles.",
                nameof(authenticationDatabase));
        }

        return new DatabaseConnectionProfile(
            id,
            serverProfileId,
            name,
            engine,
            remoteHost,
            remotePort,
            databaseName,
            username,
            authenticationKind,
            credentialReference,
            authenticationDatabase,
            tlsMode);
    }

    public static DatabaseConnectionProfile Rehydrate(
        Guid id,
        Guid serverProfileId,
        string name,
        DatabaseEngineKind engine,
        string remoteHost,
        int remotePort,
        string? databaseName,
        string? username,
        DatabaseAuthenticationKind authenticationKind,
        SecretReference? credentialReference,
        string? authenticationDatabase = null,
        DatabaseTlsMode tlsMode = DatabaseTlsMode.Disabled) =>
        Create(
            id,
            serverProfileId,
            name,
            engine,
            remoteHost,
            remotePort,
            databaseName,
            username,
            authenticationKind,
            credentialReference,
            authenticationDatabase,
            tlsMode);

    public static int DefaultPortFor(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => 5432,
        DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb => 3306,
        DatabaseEngineKind.Redis => 6379,
        DatabaseEngineKind.SqlServer => 1433,
        DatabaseEngineKind.MongoDb => 27017,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static string NormalizeRequired(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        var normalized = value.Trim();
        ValidateText(normalized, parameterName, maximumLength);
        return normalized;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        ValidateText(normalized, parameterName, maximumLength);
        return normalized;
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value cannot exceed {maximumLength} characters.");
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("Value cannot contain NUL characters.", parameterName);
        }
    }
}

public sealed record DatabaseProfileSpec(
    string Name,
    DatabaseEngineKind Engine,
    string RemoteHost,
    int RemotePort,
    string? DatabaseName,
    string? Username,
    DatabaseAuthenticationKind AuthenticationKind,
    string? AuthenticationDatabase = null,
    DatabaseTlsMode TlsMode = DatabaseTlsMode.Disabled);

public interface IDatabaseProfileRepository
{
    ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<DatabaseConnectionProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IDatabaseProfileService
{
    ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<DatabaseConnectionProfile> CreateAsync(
        Guid serverProfileId,
        DatabaseProfileSpec spec,
        string? initialSecret,
        CancellationToken cancellationToken = default);

    ValueTask<DatabaseConnectionProfile> UpdateAsync(
        Guid id,
        DatabaseProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DatabaseProfileService : IDatabaseProfileService
{
    private readonly IDatabaseProfileRepository _repository;
    private readonly IProfileRepository _serverRepository;
    private readonly ISecretStore _secretStore;

    public DatabaseProfileService(
        IDatabaseProfileRepository repository,
        IProfileRepository serverRepository,
        ISecretStore secretStore)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _serverRepository = serverRepository ?? throw new ArgumentNullException(nameof(serverRepository));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _repository.ListForServerAsync(serverProfileId, cancellationToken);

    public async ValueTask<DatabaseConnectionProfile> CreateAsync(
        Guid serverProfileId,
        DatabaseProfileSpec spec,
        string? initialSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await EnsureServerExistsAsync(serverProfileId, cancellationToken).ConfigureAwait(false);
        ValidateSecret(spec.AuthenticationKind, existing: null, replaceSecret: true, initialSecret);

        var id = Guid.NewGuid();
        var credentialReference = spec.AuthenticationKind == DatabaseAuthenticationKind.Password
            ? SecretReference.ForDatabaseProfile(id)
            : (SecretReference?)null;
        var profile = CreateProfile(id, serverProfileId, spec, credentialReference);

        if (credentialReference is not null)
        {
            await _secretStore.SetAsync(
                    credentialReference.Value,
                    initialSecret!,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await _repository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            if (credentialReference is not null)
            {
                await TryDeleteSecretAsync(credentialReference.Value).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask<DatabaseConnectionProfile> UpdateAsync(
        Guid id,
        DatabaseProfileSpec spec,
        string? replacementSecret,
        bool replaceSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(id));
        }

        var existing = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Database profile was not found.");
        await EnsureServerExistsAsync(existing.ServerProfileId, cancellationToken).ConfigureAwait(false);
        ValidateSecret(spec.AuthenticationKind, existing, replaceSecret, replacementSecret);

        var desiredReference = spec.AuthenticationKind == DatabaseAuthenticationKind.Password
            ? SecretReference.ForDatabaseProfile(id)
            : (SecretReference?)null;
        var oldReference = existing.CredentialReference;
        var referenceChanged = desiredReference != oldReference;
        var secretMustBeWritten = desiredReference is not null && (referenceChanged || replaceSecret);
        var secretMustBeDeleted = oldReference is not null && desiredReference is null;
        var changedSecret = secretMustBeWritten || secretMustBeDeleted;

        if (!changedSecret)
        {
            var unchangedSecretProfile = CreateProfile(id, existing.ServerProfileId, spec, desiredReference);
            await _repository.UpsertAsync(unchangedSecretProfile, cancellationToken).ConfigureAwait(false);
            return unchangedSecretProfile;
        }

        var oldSecret = oldReference is null
            ? null
            : await _secretStore.GetAsync(oldReference.Value, cancellationToken).ConfigureAwait(false);
        var oldReferenceDeleted = false;
        var desiredReferenceWritten = false;

        try
        {
            if (secretMustBeDeleted)
            {
                await _secretStore.DeleteAsync(oldReference!.Value, cancellationToken).ConfigureAwait(false);
                oldReferenceDeleted = true;
            }

            if (secretMustBeWritten)
            {
                await _secretStore.SetAsync(
                        desiredReference!.Value,
                        replacementSecret!,
                        cancellationToken)
                    .ConfigureAwait(false);
                desiredReferenceWritten = true;
            }

            var updated = CreateProfile(id, existing.ServerProfileId, spec, desiredReference);
            await _repository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (desiredReferenceWritten && desiredReference is not null && oldSecret is null)
            {
                await TryDeleteSecretAsync(desiredReference.Value).ConfigureAwait(false);
            }

            if (oldReference is not null && oldSecret is not null &&
                (oldReferenceDeleted || desiredReferenceWritten))
            {
                await TrySetSecretAsync(oldReference.Value, oldSecret).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(id));
        }

        var existing = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var reference = existing.CredentialReference;
        var oldSecret = reference is null
            ? null
            : await _secretStore.GetAsync(reference.Value, cancellationToken).ConfigureAwait(false);
        if (reference is not null)
        {
            await _secretStore.DeleteAsync(reference.Value, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (reference is not null && oldSecret is not null)
            {
                await TrySetSecretAsync(reference.Value, oldSecret).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async ValueTask EnsureServerExistsAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        if (await _serverRepository.GetAsync(serverProfileId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Server profile was not found.");
        }
    }

    private static DatabaseConnectionProfile CreateProfile(
        Guid id,
        Guid serverProfileId,
        DatabaseProfileSpec spec,
        SecretReference? credentialReference) =>
        DatabaseConnectionProfile.Create(
            id,
            serverProfileId,
            spec.Name,
            spec.Engine,
            spec.RemoteHost,
            spec.RemotePort,
            spec.DatabaseName,
            spec.Username,
            spec.AuthenticationKind,
            credentialReference,
            spec.AuthenticationDatabase,
            spec.TlsMode);

    private static void ValidateSecret(
        DatabaseAuthenticationKind authenticationKind,
        DatabaseConnectionProfile? existing,
        bool replaceSecret,
        string? secret)
    {
        if (!Enum.IsDefined(authenticationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationKind));
        }

        if (authenticationKind == DatabaseAuthenticationKind.None)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException(
                    "A no-password database profile cannot store a password.",
                    nameof(secret));
            }

            return;
        }

        var switchingToPassword = existing is not null &&
            existing.AuthenticationKind != DatabaseAuthenticationKind.Password;
        if (existing is null || switchingToPassword || replaceSecret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("Database password cannot be empty.", nameof(secret));
            }
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
            // Preserve the original persistence/secret-store failure.
        }
    }

    private async ValueTask TrySetSecretAsync(SecretReference reference, string secret)
    {
        try
        {
            await _secretStore.SetAsync(reference, secret, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original persistence/secret-store failure.
        }
    }
}