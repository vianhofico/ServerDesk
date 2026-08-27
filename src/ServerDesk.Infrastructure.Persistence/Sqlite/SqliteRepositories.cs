using Microsoft.Data.Sqlite;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteProfileRepository : IProfileRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteProfileRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<IReadOnlyList<ServerProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.id, p.name, p.host, p.port, p.username, p.environment, p.credential_reference,
                   p.authentication_kind, p.private_key_path,
                   COALESCE(r.kind, 0), r.proxy_host, r.proxy_port, r.proxy_username,
                   r.proxy_credential_reference, r.bastion_profile_id
            FROM server_profiles p
            LEFT JOIN server_profile_routes r ON r.server_profile_id = p.id
            ORDER BY p.name COLLATE NOCASE, p.id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<ServerProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async ValueTask<ServerProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.id, p.name, p.host, p.port, p.username, p.environment, p.credential_reference,
                   p.authentication_kind, p.private_key_path,
                   COALESCE(r.kind, 0), r.proxy_host, r.proxy_port, r.proxy_username,
                   r.proxy_credential_reference, r.bastion_profile_id
            FROM server_profiles p
            LEFT JOIN server_profile_routes r ON r.server_profile_id = p.id
            WHERE p.id = @id;
            """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    public async ValueTask UpsertAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO server_profiles (
                    id, name, host, port, username, environment, credential_reference,
                    authentication_kind, private_key_path, created_utc, updated_utc)
                VALUES (
                    @id, @name, @host, @port, @username, @environment, @credential_reference,
                    @authentication_kind, @private_key_path, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    host = excluded.host,
                    port = excluded.port,
                    username = excluded.username,
                    environment = excluded.environment,
                    credential_reference = excluded.credential_reference,
                    authentication_kind = excluded.authentication_kind,
                    private_key_path = excluded.private_key_path,
                    updated_utc = excluded.updated_utc;
                """;

            command.Parameters.AddWithValue("@id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("@name", profile.Name);
            command.Parameters.AddWithValue("@host", profile.Host);
            command.Parameters.AddWithValue("@port", profile.Port);
            command.Parameters.AddWithValue("@username", profile.Username);
            command.Parameters.AddWithValue("@environment", (object?)profile.Environment ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@credential_reference",
                (object?)profile.CredentialReference?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("@authentication_kind", (int)profile.AuthenticationKind);
            command.Parameters.AddWithValue("@private_key_path", (object?)profile.PrivateKeyPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO server_profile_routes (
                    server_profile_id, kind, proxy_host, proxy_port, proxy_username,
                    proxy_credential_reference, bastion_profile_id)
                VALUES (
                    @server_profile_id, @kind, @proxy_host, @proxy_port, @proxy_username,
                    @proxy_credential_reference, @bastion_profile_id)
                ON CONFLICT(server_profile_id) DO UPDATE SET
                    kind = excluded.kind,
                    proxy_host = excluded.proxy_host,
                    proxy_port = excluded.proxy_port,
                    proxy_username = excluded.proxy_username,
                    proxy_credential_reference = excluded.proxy_credential_reference,
                    bastion_profile_id = excluded.bastion_profile_id;
                """;
            command.Parameters.AddWithValue("@server_profile_id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("@kind", (int)profile.Route.Kind);
            command.Parameters.AddWithValue("@proxy_host", (object?)profile.Route.ProxyHost ?? DBNull.Value);
            command.Parameters.AddWithValue("@proxy_port", (object?)profile.Route.ProxyPort ?? DBNull.Value);
            command.Parameters.AddWithValue("@proxy_username", (object?)profile.Route.ProxyUsername ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@proxy_credential_reference",
                (object?)profile.Route.ProxyCredentialReference?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@bastion_profile_id",
                profile.Route.BastionProfileId is { } bastionId ? bastionId.ToString("D") : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_profiles WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerProfile ReadProfile(SqliteDataReader reader)
    {
        var credentialReference = reader.IsDBNull(6)
            ? (SecretReference?)null
            : SecretReference.Parse(reader.GetString(6));
        var proxyCredentialReference = reader.IsDBNull(13)
            ? (SecretReference?)null
            : SecretReference.Parse(reader.GetString(13));
        var route = ServerConnectionRoute.Rehydrate(
            (ServerRouteKind)reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            proxyCredentialReference,
            reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)));

        return ServerProfile.Rehydrate(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            credentialReference,
            (ServerAuthenticationKind)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            route);
    }
}

public sealed class SqliteOperationAudit : IOperationAudit
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteOperationAudit(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask AppendAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operation_audit (id, occurred_utc, category, summary, target, risk, outcome)
            VALUES (@id, @occurred_utc, @category, @summary, @target, @risk, @outcome);
            """;
        command.Parameters.AddWithValue("@id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "@occurred_utc",
            entry.OccurredAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@category", entry.Category);
        command.Parameters.AddWithValue("@summary", entry.Summary);
        command.Parameters.AddWithValue("@target", (object?)entry.Target ?? DBNull.Value);
        command.Parameters.AddWithValue("@risk", (int)entry.Risk);
        command.Parameters.AddWithValue("@outcome", (int)entry.Outcome);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, occurred_utc, category, summary, target, risk, outcome
            FROM operation_audit
            ORDER BY occurred_utc DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<OperationAuditEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new OperationAuditEntry(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                (OperationRisk)reader.GetInt32(5),
                (OperationOutcome)reader.GetInt32(6)));
        }

        return entries;
    }
}
