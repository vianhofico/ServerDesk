using System.Globalization;
using MySqlConnector;
using ServerDesk.Application.Databases;

namespace ServerDesk.Infrastructure.Databases;

public sealed class MySqlDiagnosticAdapter : MySqlFamilyDiagnosticAdapter
{
    public MySqlDiagnosticAdapter() : base(DatabaseEngineKind.MySql)
    {
    }
}

public sealed class MariaDbDiagnosticAdapter : MySqlFamilyDiagnosticAdapter
{
    public MariaDbDiagnosticAdapter() : base(DatabaseEngineKind.MariaDb)
    {
    }
}

public abstract class MySqlFamilyDiagnosticAdapter : IDatabaseEngineDiagnosticAdapter
{
    protected MySqlFamilyDiagnosticAdapter(DatabaseEngineKind engine)
    {
        if (engine is not DatabaseEngineKind.MySql and not DatabaseEngineKind.MariaDb)
        {
            throw new ArgumentOutOfRangeException(nameof(engine));
        }

        Engine = engine;
    }

    public DatabaseEngineKind Engine { get; }

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
            var timeout = (uint)DatabaseDiagnosticAdapterUtilities.TimeoutSeconds(request.Options.CommandTimeout);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = request.LocalAddress.ToString(),
                Port = (uint)request.LocalPort,
                UserID = request.Username ?? string.Empty,
                Password = request.Secret ?? string.Empty,
                Database = request.DatabaseName ?? string.Empty,
                Pooling = false,
                ConnectionTimeout = timeout,
                DefaultCommandTimeout = timeout,
                SslMode = MySqlSslMode.Disabled,
            };

            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);

            var identity = await ReadIdentityAsync(connection, request.Options.MaxTextLength, deadline.Token)
                .ConfigureAwait(false);
            var catalogs = await ReadCatalogsAsync(
                    connection,
                    request.Options.MaxCatalogs,
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);
            var metrics = await ReadNameValueRowsAsync(
                    connection,
                    "SHOW GLOBAL STATUS WHERE Variable_name IN " +
                    "('Threads_connected','Threads_running','Connections','Aborted_connects');",
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);
            var metadataRows = await ReadNameValueRowsAsync(
                    connection,
                    "SHOW VARIABLES WHERE Variable_name IN " +
                    "('version','version_comment','log_error','general_log','slow_query_log','slow_query_log_file');",
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);

            var diagnosticMetrics = metrics
                .Where(pair => long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(pair => new DatabaseDiagnosticMetric(
                    NormalizeMetricName(pair.Key),
                    long.Parse(pair.Value, CultureInfo.InvariantCulture)))
                .ToArray();
            var metadata = metadataRows
                .Take(request.Options.MaxMetadataItems)
                .Select(pair => new DatabaseDiagnosticMetadata(pair.Key, pair.Value))
                .ToArray();
            var serverVersion = DatabaseDiagnosticAdapterUtilities.BoundText(
                connection.ServerVersion,
                request.Options.MaxTextLength);
            var flavor = serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
                ? "MariaDB"
                : "MySQL";

            return DatabaseDiagnosticResult.Success(
                new DatabaseDiagnosticSnapshot(
                    Engine,
                    serverVersion,
                    flavor,
                    identity,
                    catalogs.Items,
                    diagnosticMetrics,
                    metadata,
                    catalogs.IsTruncated,
                    DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (MySqlException exception) when (exception.Number == 1045)
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(Engine);
        }
        catch (MySqlException exception) when (exception.Number is 1044 or 1142 or 1227)
        {
            return DatabaseDiagnosticAdapterUtilities.Authorization(Engine);
        }
        catch (MySqlException exception) when (exception.Number == 1049)
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.CapabilityUnavailable,
                $"The configured {Engine} database does not exist or is unavailable.");
        }
        catch (MySqlException)
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
        MySqlConnection connection,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT CURRENT_USER(), DATABASE();",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new FormatException("MySQL identity row was missing.");
        }

        var database = reader.IsDBNull(1) ? "(none)" : reader.GetString(1);
        return DatabaseDiagnosticAdapterUtilities.BoundText(
            $"{reader.GetString(0)}@{database}",
            maxTextLength);
    }

    private static async Task<(IReadOnlyList<DatabaseCatalogItem> Items, bool IsTruncated)> ReadCatalogsAsync(
        MySqlConnection connection,
        int maxCatalogs,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT s.schema_name, COALESCE(SUM(t.data_length + t.index_length), 0) AS size_bytes " +
            "FROM information_schema.schemata s " +
            "LEFT JOIN information_schema.tables t ON t.table_schema = s.schema_name " +
            "GROUP BY s.schema_name ORDER BY size_bytes DESC, s.schema_name LIMIT @limit;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", maxCatalogs + 1);
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
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }

        return (items, truncated);
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadNameValueRowsAsync(
        MySqlConnection connection,
        string sql,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<KeyValuePair<string, string>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new KeyValuePair<string, string>(
                DatabaseDiagnosticAdapterUtilities.BoundText(reader.GetString(0), maxTextLength),
                DatabaseDiagnosticAdapterUtilities.BoundText(reader.GetString(1), maxTextLength)));
        }

        return items;
    }

    private static string NormalizeMetricName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
