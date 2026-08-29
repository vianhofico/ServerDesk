using System.Net.Sockets;
using System.Text;
using ServerDesk.Application.Databases;

namespace ServerDesk.Infrastructure.Databases;

public sealed class RedisDiagnosticAdapter : IDatabaseEngineDiagnosticAdapter
{
    private const int MaxInfoBytes = 1024 * 1024;

    public DatabaseEngineKind Engine => DatabaseEngineKind.Redis;

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
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(request.LocalAddress, request.LocalPort, deadline.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            if (request.AuthenticationKind == DatabaseAuthenticationKind.Password)
            {
                var arguments = string.IsNullOrWhiteSpace(request.Username)
                    ? new[] { "AUTH", request.Secret ?? string.Empty }
                    : new[] { "AUTH", request.Username!, request.Secret ?? string.Empty };
                await WriteCommandAsync(stream, arguments, deadline.Token).ConfigureAwait(false);
                var auth = await ReadRespAsync(stream, deadline.Token).ConfigureAwait(false);
                if (auth.IsError)
                {
                    return ClassifyServerError(auth.Text);
                }
            }

            await WriteCommandAsync(stream, ["INFO"], deadline.Token).ConfigureAwait(false);
            var infoResponse = await ReadRespAsync(stream, deadline.Token).ConfigureAwait(false);
            if (infoResponse.IsError)
            {
                return ClassifyServerError(infoResponse.Text);
            }

            if (infoResponse.Text is null)
            {
                return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
            }

            var info = ParseInfo(infoResponse.Text);
            if (!info.Values.TryGetValue("redis_version", out var version) || string.IsNullOrWhiteSpace(version))
            {
                return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
            }

            var catalogs = info.Keyspaces
                .OrderBy(item => item.DatabaseIndex)
                .Take(request.Options.MaxCatalogs)
                .Select(item => new DatabaseCatalogItem(
                    $"db{item.DatabaseIndex}",
                    SizeBytes: null,
                    ItemCount: item.Keys,
                    ExpiringItemCount: item.Expires))
                .ToArray();
            var metrics = BuildMetrics(info.Values);
            var metadata = BuildMetadata(
                info.Values,
                request.Options.MaxMetadataItems,
                request.Options.MaxTextLength);
            var identity = DatabaseDiagnosticAdapterUtilities.BoundText(
                string.IsNullOrWhiteSpace(request.Username) ? "default" : request.Username,
                request.Options.MaxTextLength);

            return DatabaseDiagnosticResult.Success(
                new DatabaseDiagnosticSnapshot(
                    Engine,
                    DatabaseDiagnosticAdapterUtilities.BoundText(version, request.Options.MaxTextLength),
                    "Redis",
                    identity,
                    catalogs,
                    metrics,
                    metadata,
                    info.Keyspaces.Count > request.Options.MaxCatalogs,
                    DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatabaseDiagnosticAdapterUtilities.Timeout(Engine);
        }
        catch (SocketException)
        {
            return DatabaseDiagnosticAdapterUtilities.Network(Engine);
        }
        catch (IOException)
        {
            return DatabaseDiagnosticAdapterUtilities.Network(Engine);
        }
        catch (FormatException)
        {
            return DatabaseDiagnosticAdapterUtilities.Parse(Engine);
        }
    }

    private static DatabaseDiagnosticResult ClassifyServerError(string? message)
    {
        if (message?.Contains("NOPERM", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DatabaseDiagnosticAdapterUtilities.Authorization(DatabaseEngineKind.Redis);
        }

        if (message?.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) == true ||
            message?.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase) == true ||
            message?.Contains("AUTH", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DatabaseDiagnosticAdapterUtilities.Authentication(DatabaseEngineKind.Redis);
        }

        return DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.CapabilityUnavailable,
            "Redis rejected the bounded read-only diagnostic operation.");
    }

    private static IReadOnlyList<DatabaseDiagnosticMetric> BuildMetrics(IReadOnlyDictionary<string, string> values)
    {
        var names = new[]
        {
            "connected_clients",
            "used_memory",
            "total_connections_received",
            "instantaneous_ops_per_sec",
            "expired_keys",
            "evicted_keys",
        };
        var metrics = new List<DatabaseDiagnosticMetric>();
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var text) && long.TryParse(text, out var value))
            {
                metrics.Add(new DatabaseDiagnosticMetric(name, value));
            }
        }

        return metrics;
    }

    private static IReadOnlyList<DatabaseDiagnosticMetadata> BuildMetadata(
        IReadOnlyDictionary<string, string> values,
        int maxItems,
        int maxTextLength)
    {
        var names = new[]
        {
            "redis_version",
            "redis_mode",
            "role",
            "os",
            "arch_bits",
            "process_id",
            "uptime_in_seconds",
            "rdb_last_save_time",
            "rdb_last_bgsave_status",
            "aof_enabled",
            "aof_last_bgrewrite_status",
            "loading",
        };
        var metadata = new List<DatabaseDiagnosticMetadata>(Math.Min(names.Length, maxItems));
        foreach (var name in names)
        {
            if (metadata.Count == maxItems)
            {
                break;
            }

            if (values.TryGetValue(name, out var value))
            {
                metadata.Add(new DatabaseDiagnosticMetadata(
                    name,
                    DatabaseDiagnosticAdapterUtilities.BoundText(value, maxTextLength)));
            }
        }

        return metadata;
    }

    private static ParsedRedisInfo ParseInfo(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxInfoBytes)
        {
            throw new FormatException("Redis INFO output exceeded the supported bound.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyspaces = new List<RedisKeyspace>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var value = line[(separator + 1)..];
            values[name] = value;
            if (name.StartsWith("db", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan(2), out var databaseIndex))
            {
                var fields = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(field => field.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
                if (fields.TryGetValue("keys", out var keysText) && long.TryParse(keysText, out var keys))
                {
                    var expires = fields.TryGetValue("expires", out var expiresText) && long.TryParse(expiresText, out var parsedExpires)
                        ? parsedExpires
                        : 0;
                    keyspaces.Add(new RedisKeyspace(databaseIndex, keys, expires));
                }
            }
        }

        return new ParsedRedisInfo(values, keyspaces);
    }

    private static async Task WriteCommandAsync(
        Stream stream,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var header = Encoding.UTF8.GetBytes($"*{arguments.Count}\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        foreach (var argument in arguments)
        {
            var bytes = Encoding.UTF8.GetBytes(argument);
            var prefix = Encoding.UTF8.GetBytes($"${bytes.Length}\r\n");
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RespValue> ReadRespAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => new RespValue(await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false), false),
            (byte)'-' => new RespValue(await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false), true),
            (byte)'$' => await ReadBulkStringAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new FormatException("Unsupported Redis response type."),
        };
    }

    private static async Task<RespValue> ReadBulkStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthText = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(lengthText, out var length) || length < -1 || length > MaxInfoBytes)
        {
            throw new FormatException("Redis bulk response length was invalid.");
        }

        if (length == -1)
        {
            return new RespValue(null, false);
        }

        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != '\r' ||
            await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != '\n')
        {
            throw new FormatException("Redis bulk response terminator was invalid.");
        }

        return new RespValue(Encoding.UTF8.GetString(buffer), false);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (bytes.Count <= MaxInfoBytes)
        {
            var value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (value == '\r')
            {
                if (await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != '\n')
                {
                    throw new FormatException("Redis line terminator was invalid.");
                }

                return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
            }

            bytes.Add(value);
        }

        throw new FormatException("Redis line exceeded the supported bound.");
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException();
        }

        return buffer[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private sealed record RespValue(string? Text, bool IsError);
    private sealed record RedisKeyspace(int DatabaseIndex, long Keys, long Expires);
    private sealed record ParsedRedisInfo(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyList<RedisKeyspace> Keyspaces);
}
