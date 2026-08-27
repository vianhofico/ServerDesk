using Microsoft.Data.Sqlite;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Domain.Networking;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqlitePortForwardProfileRepository : IPortForwardProfileRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqlitePortForwardProfileRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<IReadOnlyList<PortForwardProfile>> ListForServerAsync(
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
            SELECT id, server_profile_id, name, kind, bind_host, bind_port,
                   destination_host, destination_port
            FROM port_forward_profiles
            WHERE server_profile_id = @server_profile_id
            ORDER BY name COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<PortForwardProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async ValueTask<PortForwardProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Port-forward profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, server_profile_id, name, kind, bind_host, bind_port,
                   destination_host, destination_port
            FROM port_forward_profiles
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    public async ValueTask UpsertAsync(
        PortForwardProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO port_forward_profiles (
                id, server_profile_id, name, kind, bind_host, bind_port,
                destination_host, destination_port, created_utc, updated_utc)
            VALUES (
                @id, @server_profile_id, @name, @kind, @bind_host, @bind_port,
                @destination_host, @destination_port, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                server_profile_id = excluded.server_profile_id,
                name = excluded.name,
                kind = excluded.kind,
                bind_host = excluded.bind_host,
                bind_port = excluded.bind_port,
                destination_host = excluded.destination_host,
                destination_port = excluded.destination_port,
                updated_utc = excluded.updated_utc;
            """;

        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("@id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("@server_profile_id", profile.ServerProfileId.ToString("D"));
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@kind", (int)profile.Kind);
        command.Parameters.AddWithValue("@bind_host", profile.BindHost);
        command.Parameters.AddWithValue("@bind_port", profile.BindPort);
        command.Parameters.AddWithValue("@destination_host", (object?)profile.DestinationHost ?? DBNull.Value);
        command.Parameters.AddWithValue("@destination_port", (object?)profile.DestinationPort ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Port-forward profile id cannot be empty.", nameof(id));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM port_forward_profiles WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PortForwardProfile ReadProfile(SqliteDataReader reader) =>
        PortForwardProfile.Rehydrate(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            (PortForwardKind)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7));
}
