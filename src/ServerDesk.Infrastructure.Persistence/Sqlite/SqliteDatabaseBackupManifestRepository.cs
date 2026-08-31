using Microsoft.Data.Sqlite;
using ServerDesk.Application.Databases;
using ServerDesk.Application.RemoteFiles;

namespace ServerDesk.Infrastructure.Persistence.Sqlite;

public sealed class SqliteDatabaseBackupManifestRepository : IDatabaseBackupManifestRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteDatabaseBackupManifestRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT backup_id, server_profile_id, database_profile_id, engine, database_name,
                   username, backup_path, format, tool_name, tool_version, size_bytes, sha256,
                   structural_check, created_utc, verified_utc, is_verified
            FROM database_backup_manifests
            WHERE server_profile_id = @server_profile_id
            ORDER BY created_utc DESC, backup_id DESC;
            """;
        command.Parameters.AddWithValue("@server_profile_id", serverProfileId.ToString("D"));
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DatabaseBackupManifest?> GetAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        if (backupId == Guid.Empty)
        {
            throw new ArgumentException("Backup id cannot be empty.", nameof(backupId));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT backup_id, server_profile_id, database_profile_id, engine, database_name,
                   username, backup_path, format, tool_name, tool_version, size_bytes, sha256,
                   structural_check, created_utc, verified_utc, is_verified
            FROM database_backup_manifests
            WHERE backup_id = @backup_id;
            """;
        command.Parameters.AddWithValue("@backup_id", backupId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadManifest(reader)
            : null;
    }

    public async ValueTask AddAsync(
        DatabaseBackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsVerified || manifest.Verification.SizeBytes <= 0 ||
            manifest.Verification.Sha256.Length != 64)
        {
            throw new ArgumentException("Only fully verified database backup manifests may be persisted.", nameof(manifest));
        }

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO database_backup_manifests (
                backup_id, server_profile_id, database_profile_id, engine, database_name,
                username, backup_path, format, tool_name, tool_version, size_bytes, sha256,
                structural_check, created_utc, verified_utc, is_verified)
            VALUES (
                @backup_id, @server_profile_id, @database_profile_id, @engine, @database_name,
                @username, @backup_path, @format, @tool_name, @tool_version, @size_bytes, @sha256,
                @structural_check, @created_utc, @verified_utc, 1);
            """;
        command.Parameters.AddWithValue("@backup_id", manifest.BackupId.ToString("D"));
        command.Parameters.AddWithValue("@server_profile_id", manifest.ServerProfileId.ToString("D"));
        command.Parameters.AddWithValue("@database_profile_id", manifest.DatabaseProfileId.ToString("D"));
        command.Parameters.AddWithValue("@engine", (int)manifest.Engine);
        command.Parameters.AddWithValue("@database_name", manifest.DatabaseName);
        command.Parameters.AddWithValue("@username", (object?)manifest.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("@backup_path", manifest.BackupPath.Value);
        command.Parameters.AddWithValue("@format", (int)manifest.Format);
        command.Parameters.AddWithValue("@tool_name", manifest.ToolName);
        command.Parameters.AddWithValue("@tool_version", manifest.ToolVersion);
        command.Parameters.AddWithValue("@size_bytes", manifest.Verification.SizeBytes);
        command.Parameters.AddWithValue("@sha256", manifest.Verification.Sha256);
        command.Parameters.AddWithValue("@structural_check", manifest.Verification.StructuralCheck);
        command.Parameters.AddWithValue("@created_utc", manifest.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@verified_utc", manifest.Verification.VerifiedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS database_backup_manifests (
                backup_id TEXT NOT NULL PRIMARY KEY,
                server_profile_id TEXT NOT NULL,
                database_profile_id TEXT NOT NULL,
                engine INTEGER NOT NULL CHECK (engine BETWEEN 0 AND 5),
                database_name TEXT NOT NULL,
                username TEXT NULL,
                backup_path TEXT NOT NULL,
                format INTEGER NOT NULL CHECK (format BETWEEN 0 AND 4),
                tool_name TEXT NOT NULL,
                tool_version TEXT NOT NULL,
                size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
                sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
                structural_check TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                verified_utc TEXT NOT NULL,
                is_verified INTEGER NOT NULL CHECK (is_verified = 1),
                FOREIGN KEY(server_profile_id) REFERENCES server_profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_database_backup_manifests_server
                ON database_backup_manifests(server_profile_id, created_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_database_backup_manifests_profile
                ON database_backup_manifests(database_profile_id, created_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<DatabaseBackupManifest>> ReadManyAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var manifests = new List<DatabaseBackupManifest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            manifests.Add(ReadManifest(reader));
        }

        return manifests;
    }

    private static DatabaseBackupManifest ReadManifest(SqliteDataReader reader)
    {
        var verified = reader.GetInt32(15) == 1;
        if (!verified)
        {
            throw new InvalidDataException("Unverified database backup metadata must not be loaded as a usable manifest.");
        }

        var verifiedUtc = DateTimeOffset.Parse(
            reader.GetString(14),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        return new DatabaseBackupManifest(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            (DatabaseEngineKind)reader.GetInt32(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            RemotePath.Parse(reader.GetString(6)),
            (DatabaseBackupFormat)reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9),
            DateTimeOffset.Parse(
                reader.GetString(13),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            new DatabaseBackupVerificationEvidence(
                reader.GetInt64(10),
                reader.GetString(11),
                reader.GetString(12),
                verifiedUtc),
            true);
    }
}