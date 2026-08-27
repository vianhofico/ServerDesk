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
            SELECT id, name, host, port, username, environment, credential_reference
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
            SELECT id, name, host, port, username, environment, credential_reference
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
                id, name, host, port, username, environment, credential_reference, created_utc, updated_utc)
            VALUES (
                @id, @name, @host, @port, @username, @environment, @credential_reference, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                host = excluded.host,
                port = excluded.port,
                username = excluded.username,
                environment = excluded.environment,
                credential_reference = excluded.credential_reference,
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
            credentialReference);
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
