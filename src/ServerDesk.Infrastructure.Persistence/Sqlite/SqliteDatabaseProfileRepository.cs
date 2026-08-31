using Microsoft.Data.Sqlite;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Secrets;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteDatabaseProfileRepository : IDatabaseProfileRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteDatabaseProfileRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, server_profile_id, name, engine, remote_host, remote_port,
                   database_name, username, authentication_kind, credential_reference,
                   authentication_database, tls_mode
            FROM database_profiles
            ORDER BY server_profile_id, name COLLATE NOCASE, id;
            """;
        return await ReadProfilesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, server_profile_id, name, engine, remote_host, remote_port,
                   database_name, username, authentication_kind, credential_reference,
                   authentication_database, tls_mode
            FROM database_profiles
            WHERE server_profile_id = @server_profile_id
            ORDER BY name COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));
        return await ReadProfilesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DatabaseConnectionProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, server_profile_id, name, engine, remote_host, remote_port,
                   database_name, username, authentication_kind, credential_reference,
                   authentication_database, tls_mode
            FROM database_profiles
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    public async ValueTask UpsertAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO database_profiles (
                id, server_profile_id, name, engine, remote_host, remote_port,
                database_name, username, authentication_kind, credential_reference,
                authentication_database, tls_mode, created_utc, updated_utc)
            VALUES (
                @id, @server_profile_id, @name, @engine, @remote_host, @remote_port,
                @database_name, @username, @authentication_kind, @credential_reference,
                @authentication_database, @tls_mode, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                server_profile_id = excluded.server_profile_id,
                name = excluded.name,
                engine = excluded.engine,
                remote_host = excluded.remote_host,
                remote_port = excluded.remote_port,
                database_name = excluded.database_name,
                username = excluded.username,
                authentication_kind = excluded.authentication_kind,
                credential_reference = excluded.credential_reference,
                authentication_database = excluded.authentication_database,
                tls_mode = excluded.tls_mode,
                updated_utc = excluded.updated_utc;
            """;

        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("@id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("@server_profile_id", profile.ServerProfileId.ToString("D"));
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@engine", (int)profile.Engine);
        command.Parameters.AddWithValue("@remote_host", profile.RemoteHost);
        command.Parameters.AddWithValue("@remote_port", profile.RemotePort);
        command.Parameters.AddWithValue("@database_name", (object?)profile.DatabaseName ?? DBNull.Value);
        command.Parameters.AddWithValue("@username", (object?)profile.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("@authentication_kind", (int)profile.AuthenticationKind);
        command.Parameters.AddWithValue(
            "@credential_reference",
            profile.CredentialReference is null ? DBNull.Value : profile.CredentialReference.Value.Value);
        command.Parameters.AddWithValue(
            "@authentication_database",
            (object?)profile.AuthenticationDatabase ?? DBNull.Value);
        command.Parameters.AddWithValue("@tls_mode", (int)profile.TlsMode);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM database_profiles WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ReadProfilesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<DatabaseConnectionProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    private static DatabaseConnectionProfile ReadProfile(SqliteDataReader reader) =>
        DatabaseConnectionProfile.Rehydrate(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            (DatabaseEngineKind)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            (DatabaseAuthenticationKind)reader.GetInt32(8),
            reader.IsDBNull(9) ? null : SecretReference.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            (DatabaseTlsMode)reader.GetInt32(11));
}
