using Microsoft.Data.Sqlite;
using ServerDesk.Application.Routing;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteConnectionRouteRepository : IConnectionRouteRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteConnectionRouteRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<ServerConnectionRoute?> GetAsync(
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
            SELECT server_profile_id, kind, proxy_host, proxy_port, proxy_username,
                   proxy_credential_reference, bastion_profile_id
            FROM server_connection_routes
            WHERE server_profile_id = @server_profile_id;
            """;
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var proxyReference = reader.IsDBNull(5)
            ? (SecretReference?)null
            : SecretReference.Parse(reader.GetString(5));
        var bastionProfileId = reader.IsDBNull(6)
            ? (Guid?)null
            : Guid.Parse(reader.GetString(6));

        return ServerConnectionRoute.Rehydrate(
            Guid.Parse(reader.GetString(0)),
            (ServerConnectionRouteKind)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            proxyReference,
            bastionProfileId);
    }

    public async ValueTask UpsertAsync(
        ServerConnectionRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.IsDirect)
        {
            await DeleteAsync(route.ServerProfileId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO server_connection_routes (
                server_profile_id, kind, proxy_host, proxy_port, proxy_username,
                proxy_credential_reference, bastion_profile_id, created_utc, updated_utc)
            VALUES (
                @server_profile_id, @kind, @proxy_host, @proxy_port, @proxy_username,
                @proxy_credential_reference, @bastion_profile_id, @now, @now)
            ON CONFLICT(server_profile_id) DO UPDATE SET
                kind = excluded.kind,
                proxy_host = excluded.proxy_host,
                proxy_port = excluded.proxy_port,
                proxy_username = excluded.proxy_username,
                proxy_credential_reference = excluded.proxy_credential_reference,
                bastion_profile_id = excluded.bastion_profile_id,
                updated_utc = excluded.updated_utc;
            """;

        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("@server_profile_id", route.ServerProfileId.ToString("D"));
        command.Parameters.AddWithValue("@kind", (int)route.Kind);
        command.Parameters.AddWithValue("@proxy_host", (object?)route.ProxyHost ?? DBNull.Value);
        command.Parameters.AddWithValue("@proxy_port", (object?)route.ProxyPort ?? DBNull.Value);
        command.Parameters.AddWithValue("@proxy_username", (object?)route.ProxyUsername ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@proxy_credential_reference",
            (object?)route.ProxyCredentialReference?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@bastion_profile_id",
            route.BastionProfileId is null ? DBNull.Value : route.BastionProfileId.Value.ToString("D"));
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(
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
        command.CommandText = "DELETE FROM server_connection_routes WHERE server_profile_id = @server_profile_id;";
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
