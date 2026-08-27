using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(appPaths);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = appPaths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        };

        _connectionString = builder.ToString();
    }

    public SqliteConnection Create() => new(_connectionString);
}

public sealed class SqliteDatabaseInitializer
{
    public const int CurrentSchemaVersion = 3;

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteDatabaseInitializer(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS schema_info (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                version INTEGER NOT NULL CHECK (version >= 0)
            );

            INSERT INTO schema_info (singleton_id, version)
            SELECT 1, 0
            WHERE NOT EXISTS (SELECT 1 FROM schema_info WHERE singleton_id = 1);
            """,
            cancellationToken).ConfigureAwait(false);

        var version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"ServerDesk database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                CREATE TABLE server_profiles (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    host TEXT NOT NULL,
                    port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                    username TEXT NOT NULL,
                    environment TEXT NULL,
                    credential_reference TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE INDEX ix_server_profiles_name ON server_profiles(name COLLATE NOCASE);

                CREATE TABLE operation_audit (
                    id TEXT NOT NULL PRIMARY KEY,
                    occurred_utc TEXT NOT NULL,
                    category TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    target TEXT NULL,
                    risk INTEGER NOT NULL,
                    outcome INTEGER NOT NULL
                );

                CREATE INDEX ix_operation_audit_occurred_utc
                    ON operation_audit(occurred_utc DESC);

                UPDATE schema_info SET version = 1 WHERE singleton_id = 1;
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            version = 1;
        }

        if (version < 2)
        {
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                ALTER TABLE server_profiles
                    ADD COLUMN authentication_kind INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE server_profiles
                    ADD COLUMN private_key_path TEXT NULL;

                UPDATE schema_info SET version = 2 WHERE singleton_id = 1;
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            version = 2;
        }

        if (version < 3)
        {
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                CREATE TABLE known_hosts (
                    id TEXT NOT NULL PRIMARY KEY,
                    host TEXT NOT NULL COLLATE NOCASE,
                    port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                    key_algorithm TEXT NOT NULL,
                    fingerprint_sha256 TEXT NOT NULL,
                    trusted_utc TEXT NOT NULL,
                    UNIQUE(host, port, key_algorithm)
                );

                CREATE INDEX ix_known_hosts_endpoint
                    ON known_hosts(host COLLATE NOCASE, port);

                UPDATE schema_info SET version = 3 WHERE singleton_id = 1;
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
    }

    public async ValueTask<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
