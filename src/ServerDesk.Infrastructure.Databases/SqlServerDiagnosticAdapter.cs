using System.Globalization;
using Microsoft.Data.SqlClient;
using ServerDesk.Application.Databases;

namespace ServerDesk.Infrastructure.Databases;

public sealed class SqlServerDiagnosticAdapter : IDatabaseEngineDiagnosticAdapter
{
    public DatabaseEngineKind Engine => DatabaseEngineKind.SqlServer;

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
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = $"tcp:{request.LocalAddress},{request.LocalPort.ToString(CultureInfo.InvariantCulture)}",
                InitialCatalog = request.DatabaseName ?? "master",
                Pooling = false,
                ConnectTimeout = DatabaseDiagnosticAdapterUtilities.TimeoutSeconds(request.Options.CommandTimeout),
                CommandTimeout = DatabaseDiagnosticAdapterUtilities.TimeoutSeconds(request.Options.CommandTimeout),
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = true,
                PersistSecurityInfo = false,
                ApplicationName = "ServerDesk",
            };

            if (request.AuthenticationKind == DatabaseAuthenticationKind.Password)
            {
                builder.UserID = request.Username ?? string.Empty;
                builder.Password = request.Secret ?? string.Empty;
                builder.IntegratedSecurity = false;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);

            var serverVersion = await ReadServerVersionAsync(
                    connection,
                    request.Options.MaxTextLength,
                    deadline.Token)
                .ConfigureAwait(false);
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
                    serverVersion,
                    "Microsoft SQL Server",
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
        catch (SqlException exception) when (exception.Number == 18456)
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(Engine);
        }
        catch (SqlException exception) when (exception.Number is 229 or 262 or 297 or 916)
        {
            return DatabaseDiagnosticAdapterUtilities.Authorization(Engine);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (SqlException)
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

    private static async Task<string> ReadServerVersionAsync(
        SqlConnection connection,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));";
        await using var command = new SqlCommand(sql, connection);
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("SQL Server product version was missing.");
        }

        return DatabaseDiagnosticAdapterUtilities.BoundText(value, maxTextLength);
    }

    private static async Task<string> ReadIdentityAsync(
        SqlConnection connection,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT SUSER_SNAME(), DB_NAME(), CAST(SERVERPROPERTY('Edition') AS nvarchar(256));";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new FormatException("SQL Server identity row was missing.");
        }

        var user = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var database = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var edition = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        return DatabaseDiagnosticAdapterUtilities.BoundText(
            $"{user}@{database} ({edition})",
            maxTextLength);
    }

    private static async Task<(IReadOnlyList<DatabaseCatalogItem> Items, bool IsTruncated)> ReadCatalogsAsync(
        SqlConnection connection,
        int maxCatalogs,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT TOP (@limit) d.name, " +
            "COALESCE(SUM(CONVERT(bigint, mf.size)) * CONVERT(bigint, 8192), 0) AS size_bytes " +
            "FROM sys.databases AS d " +
            "LEFT JOIN sys.master_files AS mf ON mf.database_id = d.database_id " +
            "WHERE d.state = 0 " +
            "GROUP BY d.name " +
            "ORDER BY size_bytes DESC, d.name;";
        await using var command = new SqlCommand(sql, connection);
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
                reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }

        return (items, truncated);
    }

    private static async Task<IReadOnlyList<DatabaseDiagnosticMetric>> ReadMetricsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT " +
            "(SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0), " +
            "(SELECT COUNT_BIG(*) FROM sys.views WHERE is_ms_shipped = 0), " +
            "(SELECT COUNT_BIG(*) FROM sys.indexes WHERE object_id > 0 AND index_id > 0);";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return
        [
            new("user_tables", reader.GetInt64(0)),
            new("user_views", reader.GetInt64(1)),
            new("indexes", reader.GetInt64(2)),
        ];
    }

    private static async Task<IReadOnlyList<DatabaseDiagnosticMetadata>> ReadMetadataAsync(
        SqlConnection connection,
        int maxItems,
        int maxTextLength,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
            "CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)), " +
            "CAST(SERVERPROPERTY('Edition') AS nvarchar(256)), " +
            "d.recovery_model_desc, d.collation_name, d.compatibility_level " +
            "FROM sys.databases AS d WHERE d.name = DB_NAME();";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var names = new[]
        {
            "product_version",
            "product_level",
            "edition",
            "recovery_model",
            "collation",
            "compatibility_level",
        };
        var items = new List<DatabaseDiagnosticMetadata>(Math.Min(names.Length, maxItems));
        for (var index = 0; index < names.Length && items.Count < maxItems; index++)
        {
            if (reader.IsDBNull(index))
            {
                continue;
            }

            items.Add(new DatabaseDiagnosticMetadata(
                names[index],
                DatabaseDiagnosticAdapterUtilities.BoundText(
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture),
                    maxTextLength)));
        }

        return items;
    }
}
