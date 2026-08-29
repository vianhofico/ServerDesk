using System.Globalization;
using Npgsql;
using ServerDesk.Application.Databases;

namespace ServerDesk.Infrastructure.Databases;

public sealed class PostgreSqlDiagnosticAdapter : IDatabaseEngineDiagnosticAdapter
{
    public DatabaseEngineKind Engine => DatabaseEngineKind.PostgreSql;

    public async Task<DatabaseDiagnosticResult> InspectAsync(
        DatabaseEngineDiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        if (DatabaseDiagnosticAdapterUtilities.ValidateRequest(request, Engine) is { } invalid)
        {
            return invalid;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Options.CommandTimeout);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = request.LocalAddress.ToString(),
                Port = request.LocalPort,
                Username = request.Username ?? string.Empty,
                Password = request.Secret ?? string.Empty,
                Database = request.DatabaseName ?? "postgres",
                Pooling = false,
                Timeout = DatabaseDiagnosticAdapterUtilities.TimeoutSeconds(request.Options.CommandTimeout),
                CommandTimeout = DatabaseDiagnosticAdapterUtilities.TimeoutSeconds(request.Options.CommandTimeout),
                SslMode = SslMode.Disable,
                IncludeErrorDetail = false,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);

            var identity = await ReadIdentityAsync(connection, request.Options.MaxTextLength, deadline.Token)
                .ConfigureAwait(false);
            var catalogs = await ReadCatalogsAsync(
                    connection,
                    request.Options.MaxCatalogs,
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);
            var metrics = await ReadMetricsAsync(connection, deadline.Token).ConfigureAwait(false);
            var metadata = await ReadMetadataAsync(
                    connection,
                    request.Options.MaxMetadataItems,
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);

            return DatabaseDiagnosticResult.Success(
                new DatabaseDiagnosticSnapshot(
                    Engine,
                    DatabaseDiagnosticAdapterUtilities.BoundText(
                        connection.PostgreSqlVersion.ToString(),
                        request.Options.MaxTextLength),
                    "PostgreSQL",
                    identity,
                    catalogs.Items,
                    metrics,
                    metadata,
                    catalogs.IsTruncated,
                    DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (PostgresException exception) when (exception.SqlState == "28P01")
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(Engine);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            return DatabaseDiagnosticAdapterUtilities.Authorization(Engine);
        }
        catch (NpgsqlException exception) when (exception.InnerException is TimeoutException)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (NpgsqlException)
        {
            return DatabaseDiagnosticAdapterUtilities.Network(Engine);
        }
        catch (InvalidCastException)
        {
            return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
        }
        catch (FormatException)
        {
            return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
        }
    }

    private static async Task<string> ReadIdentityAsync(
        NpgsqlConnection connection,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_user, current_database();",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new FormatException("PostgreSQL identity row was missing.");
        }

        return DatabaseDiagnosticAdapterUtilities.BoundText(
            $"{reader.GetString(0)}@{reader.GetString(1)}",
            maxTextLength);
    }

    private static async Task<(IReadOnlyList<DatabaseCatalogItem> Items, bool IsTruncated)> ReadCatalogsAsync(
        NpgsqlConnection connection,
        int maxCatalogs,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT datname, pg_database_size(datname) FROM pg_database " +
            "WHERE datallowconn AND NOT datistemplate " +
            "ORDER BY pg_database_size(datname) DESC, datname LIMIT @limit;",
            connection);
        command.Parameters.AddWithValue("limit", maxCatalogs + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<DatabaseCatalogItem>(maxCatalogs);
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count == maxCatalogs)
            {
                truncated = true;
                break;
            }

            items.Add(new DatabaseCatalogItem(
                DatabaseDiagnosticAdapterUtilities.BoundText(reader.GetString(0), maxTextLength),
                reader.GetInt64(1)));
        }

        return (items, truncated);
    }

    private static async Task<IReadOnlyList<DatabaseDiagnosticMetric>> ReadMetricsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT numbackends, xact_commit, xact_rollback, blks_read, blks_hit " +
            "FROM pg_stat_database WHERE datname = current_database();",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return
        [
            new("connections", reader.GetInt64(0)),
            new("transactions_committed", reader.GetInt64(1)),
            new("transactions_rolled_back", reader.GetInt64(2)),
            new("blocks_read", reader.GetInt64(3)),
            new("blocks_hit", reader.GetInt64(4)),
        ];
    }

    private static async Task<IReadOnlyList<DatabaseDiagnosticMetadata>> ReadMetadataAsync(
        NpgsqlConnection connection,
        int maxItems,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT current_setting('server_version', true), " +
            "current_setting('log_destination', true), " +
            "current_setting('logging_collector', true), " +
            "current_setting('log_directory', true), " +
            "current_setting('log_filename', true);";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var names = new[] { "server_version", "log_destination", "logging_collector", "log_directory", "log_filename" };
        var items = new List<DatabaseDiagnosticMetadata>(Math.Min(names.Length, maxItems));
        for (var index = 0; index < names.Length && items.Count < maxItems; index++)
        {
            if (!reader.IsDBNull(index))
            {
                items.Add(new DatabaseDiagnosticMetadata(
                    names[index],
                    DatabaseDiagnosticAdapterUtilities.BoundText(
                        Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture),
                        maxTextLength)));
            }
        }

        return items;
    }
}
