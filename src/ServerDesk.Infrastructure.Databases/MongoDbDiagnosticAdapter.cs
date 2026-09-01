using MongoDB.Bson;
using MongoDB.Driver;
using ServerDesk.Application.Databases;

namespace ServerDesk.Infrastructure.Databases;

public sealed class MongoDbDiagnosticAdapter : IDatabaseEngineDiagnosticAdapter
{
    private static readonly TimeSpan MaximumConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumServerSelectionTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DiagnosticHeartbeatInterval = TimeSpan.FromMilliseconds(500);

    public DatabaseEngineKind Engine => DatabaseEngineKind.MongoDb;

    public async Task<DatabaseDiagnosticResult> InspectAsync(
        DatabaseEngineDiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        if (DatabaseDiagnosticAdapterUtilities.ValidateRequest(request, Engine) is { } invalid)
        {
            return invalid;
        }

        if (request.AuthenticationKind == DatabaseAuthenticationKind.Password &&
            (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Secret)))
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(Engine);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Options.CommandTimeout);
        try
        {
            var connectTimeout = Minimum(request.Options.CommandTimeout, MaximumConnectTimeout);
            var serverSelectionTimeout = Minimum(request.Options.CommandTimeout, MaximumServerSelectionTimeout);
            var settings = new MongoClientSettings
            {
                Server = new MongoServerAddress(request.LocalAddress.ToString(), request.LocalPort),
                DirectConnection = true,
                ApplicationName = "ServerDesk",
                ConnectTimeout = connectTimeout,
                ServerSelectionTimeout = serverSelectionTimeout,
                SocketTimeout = request.Options.CommandTimeout,
                HeartbeatInterval = DiagnosticHeartbeatInterval,
                HeartbeatTimeout = connectTimeout,
                ServerMonitoringMode = MongoDB.Driver.Core.Servers.ServerMonitoringMode.Poll,
                UseTls = request.TlsMode == DatabaseTlsMode.Required,
                RetryReads = false,
                RetryWrites = false,
            };
            if (request.AuthenticationKind == DatabaseAuthenticationKind.Password)
            {
                settings.Credential = MongoCredential.CreateCredential(
                    request.AuthenticationDatabase ?? "admin",
                    request.Username!,
                    request.Secret!);
            }

            using var client = new MongoClient(settings);
            var admin = client.GetDatabase("admin");
            var hello = await RunAsync(admin, new BsonDocument("hello", 1), deadline.Token).ConfigureAwait(false);
            var buildInfo = await RunAsync(admin, new BsonDocument("buildInfo", 1), deadline.Token).ConfigureAwait(false);
            var version = buildInfo.GetValue("version", BsonNull.Value).IsString
                ? buildInfo["version"].AsString
                : string.Empty;
            if (string.IsNullOrWhiteSpace(version))
            {
                return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
            }

            var topology = Topology(hello);
            var databases = await ReadDatabasesAsync(
                    client,
                    admin,
                    request.DatabaseName,
                    request.Options,
                    deadline.Token)
                .ConfigureAwait(false);
            var identity = request.AuthenticationKind == DatabaseAuthenticationKind.Password
                ? $"{request.Username}@{request.AuthenticationDatabase ?? "admin"}"
                : "unauthenticated";
            var metadata = new List<DatabaseDiagnosticMetadata>();
            AddMetadata(metadata, request.Options.MaxMetadataItems, "topology", topology, request.Options.MaxTextLength);
            AddMetadata(
                metadata,
                request.Options.MaxMetadataItems,
                "role",
                string.Equals(topology, "mongos", StringComparison.Ordinal) ? "mongos" : "mongod",
                request.Options.MaxTextLength);
            AddMetadata(
                metadata,
                request.Options.MaxMetadataItems,
                "max_wire_version",
                Int64(hello, "maxWireVersion")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                request.Options.MaxTextLength);
            AddMetadata(
                metadata,
                request.Options.MaxMetadataItems,
                "tls",
                request.TlsMode == DatabaseTlsMode.Required ? "required" : "disabled",
                request.Options.MaxTextLength);
            if (!string.IsNullOrWhiteSpace(request.AuthenticationDatabase))
            {
                AddMetadata(
                    metadata,
                    request.Options.MaxMetadataItems,
                    "authentication_database",
                    request.AuthenticationDatabase,
                    request.Options.MaxTextLength);
            }

            return DatabaseDiagnosticResult.Success(
                new DatabaseDiagnosticSnapshot(
                    Engine,
                    DatabaseDiagnosticAdapterUtilities.BoundText(version, request.Options.MaxTextLength),
                    "MongoDB",
                    DatabaseDiagnosticAdapterUtilities.BoundText(identity, request.Options.MaxTextLength),
                    databases.Items,
                    [
                        new DatabaseDiagnosticMetric("databases", databases.Items.Count),
                        new DatabaseDiagnosticMetric("collections", databases.CollectionCount),
                        new DatabaseDiagnosticMetric("documents", databases.DocumentCount),
                    ],
                    metadata,
                    databases.IsTruncated,
                    DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (MongoAuthenticationException)
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(Engine);
        }
        catch (MongoCommandException exception) when (exception.Code == 13)
        {
            return DatabaseDiagnosticAdapterUtilities.Authorization(Engine);
        }
        catch (TimeoutException)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (MongoException)
        {
            return DatabaseDiagnosticAdapterUtilities.Network(Engine);
        }
        catch (FormatException)
        {
            return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
        }
        catch (InvalidCastException)
        {
            return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
        }
    }

    private static async Task<DatabaseRead> ReadDatabasesAsync(
        MongoClient client,
        IMongoDatabase admin,
        string? requestedDatabase,
        DatabaseDiagnosticOptions options,
        CancellationToken cancellationToken)
    {
        var response = await RunAsync(
                admin,
                new BsonDocument
                {
                    ["listDatabases"] = 1,
                    ["nameOnly"] = true,
                    ["authorizedDatabases"] = true,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.TryGetValue("databases", out var databasesValue) || !databasesValue.IsBsonArray)
        {
            throw new FormatException("MongoDB listDatabases response did not contain a databases array.");
        }

        var names = new List<string>(databasesValue.AsBsonArray.Count);
        foreach (var value in databasesValue.AsBsonArray)
        {
            if (!value.IsBsonDocument || !value.AsBsonDocument.TryGetValue("name", out var nameValue) || !nameValue.IsString)
            {
                throw new FormatException("MongoDB listDatabases returned an invalid database row.");
            }

            names.Add(nameValue.AsString);
        }

        if (!string.IsNullOrWhiteSpace(requestedDatabase))
        {
            var requestedIndex = names.FindIndex(name => string.Equals(name, requestedDatabase, StringComparison.Ordinal));
            if (requestedIndex > 0)
            {
                var target = names[requestedIndex];
                names.RemoveAt(requestedIndex);
                names.Insert(0, target);
            }
        }

        var rows = new List<DatabaseCatalogItem>(Math.Min(options.MaxCatalogs, names.Count));
        long collections = 0;
        long documents = 0;
        foreach (var name in names.Take(options.MaxCatalogs))
        {
            var stats = await RunAsync(
                    client.GetDatabase(name),
                    new BsonDocument
                    {
                        ["dbStats"] = 1,
                        ["scale"] = 1,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var collectionCount = Int64(stats, "collections") ?? 0;
            var documentCount = Int64(stats, "objects") ?? 0;
            collections = checked(collections + Math.Max(0, collectionCount));
            documents = checked(documents + Math.Max(0, documentCount));
            rows.Add(new DatabaseCatalogItem(
                DatabaseDiagnosticAdapterUtilities.BoundText(name, options.MaxTextLength),
                Int64(stats, "storageSize"),
                Math.Max(0, collectionCount)));
        }

        return new DatabaseRead(rows, collections, documents, names.Count > rows.Count);
    }

    private static Task<BsonDocument> RunAsync(
        IMongoDatabase database,
        BsonDocument command,
        CancellationToken cancellationToken) =>
        database.RunCommandAsync(
            new BsonDocumentCommand<BsonDocument>(command),
            cancellationToken: cancellationToken);

    private static string Topology(BsonDocument hello)
    {
        if (hello.TryGetValue("msg", out var message) &&
            message.IsString && string.Equals(message.AsString, "isdbgrid", StringComparison.Ordinal))
        {
            return "mongos";
        }

        return hello.Contains("setName") ? "replica-set" : "standalone";
    }

    private static long? Int64(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.IsInt32)
        {
            return value.AsInt32;
        }

        if (value.IsInt64)
        {
            return value.AsInt64;
        }

        if (value.IsDouble && value.AsDouble is >= long.MinValue and <= long.MaxValue)
        {
            return checked((long)value.AsDouble);
        }

        return null;
    }

    private static void AddMetadata(
        ICollection<DatabaseDiagnosticMetadata> target,
        int maximumItems,
        string name,
        string value,
        int maximumTextLength)
    {
        if (target.Count >= maximumItems)
        {
            return;
        }

        target.Add(new DatabaseDiagnosticMetadata(
            name,
            DatabaseDiagnosticAdapterUtilities.BoundText(value, maximumTextLength)));
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private sealed record DatabaseRead(
        IReadOnlyList<DatabaseCatalogItem> Items,
        long CollectionCount,
        long DocumentCount,
        bool IsTruncated);
}
