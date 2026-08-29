using System.Text;
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
            SELECT id, name, host, port, username, environment, credential_reference,
                   authentication_kind, private_key_path
            FROM server_profiles
            ORDER BY name COLLATE NOCASE, id;
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
            SELECT id, name, host, port, username, environment, credential_reference,
                   authentication_kind, private_key_path
            FROM server_profiles
            WHERE id = @id;
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
        await using var command = connection.CreateCommand();
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

        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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

        return ServerProfile.Rehydrate(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            credentialReference,
            (ServerAuthenticationKind)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}

public sealed class SqliteOperationAudit : IOperationAudit, IOperationAuditReader
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
            ORDER BY occurred_utc DESC, id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OperationAuditEntry>> QueryAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > OperationHistoryService.MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"History limit must be between 1 and {OperationHistoryService.MaximumLimit}.");
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            "SELECT id, occurred_utc, category, summary, target, risk, outcome FROM operation_audit");
        var clauses = new List<string>();

        if (query.FromUtc is { } fromUtc)
        {
            clauses.Add("julianday(occurred_utc) >= julianday(@from_utc)");
            command.Parameters.AddWithValue("@from_utc", ToSqlTime(fromUtc));
        }

        if (query.ToUtc is { } toUtc)
        {
            clauses.Add("julianday(occurred_utc) <= julianday(@to_utc)");
            command.Parameters.AddWithValue("@to_utc", ToSqlTime(toUtc));
        }

        if (query.ServerProfileId is { } serverProfileId)
        {
            clauses.Add("substr(target, 1, length(@server_prefix)) = @server_prefix");
            command.Parameters.AddWithValue("@server_prefix", $"server:{serverProfileId:D} ");
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            clauses.Add("category = @category COLLATE NOCASE");
            command.Parameters.AddWithValue("@category", query.Category);
        }

        if (query.Risk is { } risk)
        {
            clauses.Add("risk = @risk");
            command.Parameters.AddWithValue("@risk", (int)risk);
        }

        if (query.Outcome is { } outcome)
        {
            clauses.Add("outcome = @outcome");
            command.Parameters.AddWithValue("@outcome", (int)outcome);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            clauses.Add(
                "(instr(lower(category), lower(@search)) > 0 OR " +
                "instr(lower(summary), lower(@search)) > 0 OR " +
                "instr(lower(coalesce(target, '')), lower(@search)) > 0)");
            command.Parameters.AddWithValue("@search", query.SearchText);
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }

        sql.Append(" ORDER BY occurred_utc DESC, id DESC LIMIT @limit;");
        command.Parameters.AddWithValue("@limit", query.Limit);
        command.CommandText = sql.ToString();
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<OperationAuditEntry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
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

    private static string ToSqlTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
