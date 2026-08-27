using Microsoft.Data.Sqlite;
using ServerDesk.Application.HostTrust;
using ServerDesk.Domain.Security;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteKnownHostRepository : IKnownHostRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteKnownHostRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<IReadOnlyList<KnownHostRecord>> ListForEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var normalizedHost = HostKeyObservation.NormalizeHost(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, host, port, key_algorithm, fingerprint_sha256, trusted_utc
            FROM known_hosts
            WHERE host = @host COLLATE NOCASE AND port = @port
            ORDER BY key_algorithm;
            """;
        command.Parameters.AddWithValue("@host", normalizedHost);
        command.Parameters.AddWithValue("@port", port);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<KnownHostRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(KnownHostRecord.Rehydrate(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                HostKeyFingerprint.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(
                    reader.GetString(5),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return records;
    }

    public async ValueTask UpsertAsync(
        KnownHostRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO known_hosts (
                id, host, port, key_algorithm, fingerprint_sha256, trusted_utc)
            VALUES (
                @id, @host, @port, @key_algorithm, @fingerprint_sha256, @trusted_utc)
            ON CONFLICT(host, port, key_algorithm) DO UPDATE SET
                id = excluded.id,
                fingerprint_sha256 = excluded.fingerprint_sha256,
                trusted_utc = excluded.trusted_utc;
            """;
        command.Parameters.AddWithValue("@id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("@host", record.Host);
        command.Parameters.AddWithValue("@port", record.Port);
        command.Parameters.AddWithValue("@key_algorithm", record.KeyAlgorithm);
        command.Parameters.AddWithValue("@fingerprint_sha256", record.Fingerprint.Value);
        command.Parameters.AddWithValue(
            "@trusted_utc",
            record.TrustedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var normalizedHost = HostKeyObservation.NormalizeHost(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM known_hosts WHERE host = @host COLLATE NOCASE AND port = @port;";
        command.Parameters.AddWithValue("@host", normalizedHost);
        command.Parameters.AddWithValue("@port", port);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
