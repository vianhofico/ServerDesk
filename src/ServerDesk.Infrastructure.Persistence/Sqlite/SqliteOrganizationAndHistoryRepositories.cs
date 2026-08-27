using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerDesk.Application.History;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteServerProfileOrganizationRepository : IServerProfileOrganizationRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteServerProfileOrganizationRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<ServerProfileOrganization> GetAsync(
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
            SELECT server_profile_id, group_name, tags_json, is_favorite
            FROM server_profile_organization
            WHERE server_profile_id = @server_profile_id;
            """;
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOrganization(reader)
            : ServerProfileOrganization.Empty(serverProfileId);
    }

    public async ValueTask<IReadOnlyDictionary<Guid, ServerProfileOrganization>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT server_profile_id, group_name, tags_json, is_favorite
            FROM server_profile_organization;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var organizations = new Dictionary<Guid, ServerProfileOrganization>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var organization = ReadOrganization(reader);
            organizations[organization.ServerProfileId] = organization;
        }

        return organizations;
    }

    public async ValueTask UpsertAsync(
        ServerProfileOrganization organization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO server_profile_organization (
                server_profile_id, group_name, tags_json, is_favorite, created_utc, updated_utc)
            VALUES (
                @server_profile_id, @group_name, @tags_json, @is_favorite, @now, @now)
            ON CONFLICT(server_profile_id) DO UPDATE SET
                group_name = excluded.group_name,
                tags_json = excluded.tags_json,
                is_favorite = excluded.is_favorite,
                updated_utc = excluded.updated_utc;
            """;

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("@server_profile_id", organization.ServerProfileId.ToString("D"));
        command.Parameters.AddWithValue("@group_name", (object?)organization.GroupName ?? DBNull.Value);
        command.Parameters.AddWithValue("@tags_json", JsonSerializer.Serialize(organization.Tags));
        command.Parameters.AddWithValue("@is_favorite", organization.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerProfileOrganization ReadOrganization(SqliteDataReader reader)
    {
        var tags = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
        return ServerProfileOrganization.Create(
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            tags,
            reader.GetInt32(3) != 0);
    }
}

public sealed class SqliteConnectionHistoryRepository : IConnectionHistoryRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteConnectionHistoryRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask AppendAsync(
        ConnectionHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO connection_history (
                    id, server_profile_id, profile_name, endpoint, route_summary,
                    started_utc, ended_utc, outcome, failure_code)
                VALUES (
                    @id, @server_profile_id, @profile_name, @endpoint, @route_summary,
                    @started_utc, @ended_utc, @outcome, @failure_code);
                """;
            insert.Parameters.AddWithValue("@id", entry.Id.ToString("D"));
            insert.Parameters.AddWithValue(
                "@server_profile_id",
                (object?)entry.ServerProfileId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("@profile_name", entry.ProfileName);
            insert.Parameters.AddWithValue("@endpoint", entry.Endpoint);
            insert.Parameters.AddWithValue("@route_summary", entry.RouteSummary);
            insert.Parameters.AddWithValue(
                "@started_utc",
                entry.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue(
                "@ended_utc",
                entry.EndedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@outcome", (int)entry.Outcome);
            insert.Parameters.AddWithValue(
                "@failure_code",
                entry.FailureCode is null ? DBNull.Value : (object)(int)entry.FailureCode.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText =
                """
                DELETE FROM connection_history
                WHERE id IN (
                    SELECT id
                    FROM connection_history
                    ORDER BY started_utc DESC, id DESC
                    LIMIT -1 OFFSET @maximum_entries
                );
                """;
            prune.Parameters.AddWithValue("@maximum_entries", ConnectionHistoryPolicy.MaxEntries);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async ValueTask<IReadOnlyList<ConnectionHistoryEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > ConnectionHistoryPolicy.MaxEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Limit must be between 1 and {ConnectionHistoryPolicy.MaxEntries}.");
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, server_profile_id, profile_name, endpoint, route_summary,
                   started_utc, ended_utc, outcome, failure_code
            FROM connection_history
            ORDER BY started_utc DESC, id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ConnectionHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ConnectionHistoryEntry(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (ConnectionAttemptOutcome)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : (RemoteErrorCode)reader.GetInt32(8)));
        }

        return entries;
    }
}
