using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Databases;

public sealed record DatabaseEngineProbeResult(
    bool IsSuccess,
    string Message,
    RemoteError? Error);

public interface IDatabaseEngineConnectivityProbe
{
    Task<DatabaseEngineProbeResult> ProbeAsync(
        DatabaseEngineKind engine,
        IPAddress localAddress,
        int localPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseEngineConnectivityProbe : IDatabaseEngineConnectivityProbe
{
    private static readonly byte[] PostgreSqlSslRequest = [0, 0, 0, 8, 4, 210, 22, 47];
    private static readonly byte[] RedisPing = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
    private static readonly byte[] SqlServerPreLogin =
    [
        0x12, 0x01, 0x00, 0x1A, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x0B, 0x00, 0x06,
        0x01, 0x00, 0x11, 0x00, 0x01,
        0xFF,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00,
    ];
    private static readonly byte[] MongoDbHello =
    [
        0x34, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0xDD, 0x07, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00,
        0x1F, 0x00, 0x00, 0x00,
        0x10, 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x02, 0x24, 0x64, 0x62, 0x00,
        0x06, 0x00, 0x00, 0x00,
        0x61, 0x64, 0x6D, 0x69, 0x6E, 0x00,
        0x00,
    ];

    public async Task<DatabaseEngineProbeResult> ProbeAsync(
        DatabaseEngineKind engine,
        IPAddress localAddress,
        int localPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (!IPAddress.IsLoopback(localAddress))
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, "Database protocol probe requires a loopback endpoint.");
        }

        if (localPort is < 1 or > 65535)
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, "Database protocol probe local port is invalid.");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return engine switch
            {
                DatabaseEngineKind.PostgreSql => await ProbePostgreSqlAsync(
                        localAddress,
                        localPort,
                        deadline.Token)
                    .ConfigureAwait(false),
                DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb => await ProbeMySqlFamilyAsync(
                        engine,
                        localAddress,
                        localPort,
                        deadline.Token)
                    .ConfigureAwait(false),
                DatabaseEngineKind.Redis => await ProbeRedisAsync(
                        localAddress,
                        localPort,
                        deadline.Token)
                    .ConfigureAwait(false),
                DatabaseEngineKind.SqlServer => await ProbeSqlServerAsync(
                        localAddress,
                        localPort,
                        deadline.Token)
                    .ConfigureAwait(false),
                DatabaseEngineKind.MongoDb => await ProbeMongoDbAsync(
                        localAddress,
                        localPort,
                        deadline.Token)
                    .ConfigureAwait(false),
                _ => Failure(RemoteErrorCode.CapabilityUnavailable, "Database engine connectivity probe is not supported."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                RemoteErrorCode.CommandTimeout,
                $"{engine} protocol reachability timed out through the SSH tunnel.");
        }
        catch (SocketException)
        {
            return Failure(
                RemoteErrorCode.ConnectionFailed,
                $"{engine} protocol endpoint was not reachable through the SSH tunnel.");
        }
        catch (IOException)
        {
            return Failure(
                RemoteErrorCode.ConnectionFailed,
                $"{engine} protocol connection closed before reachability could be verified.");
        }
    }

    private static async Task<DatabaseEngineProbeResult> ProbePostgreSqlAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(PostgreSqlSslRequest, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var response = new byte[1];
        await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
        return response[0] is (byte)'S' or (byte)'N'
            ? Success("PostgreSQL protocol reachability succeeded through the SSH tunnel.")
            : Failure(
                RemoteErrorCode.ParseFailed,
                "The tunneled endpoint did not return a PostgreSQL SSL negotiation response.");
    }

    private static async Task<DatabaseEngineProbeResult> ProbeMySqlFamilyAsync(
        DatabaseEngineKind engine,
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var prefix = new byte[5];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var payloadLength = prefix[0] | (prefix[1] << 8) | (prefix[2] << 16);
        var firstPayloadByte = prefix[4];
        return payloadLength > 0 && firstPayloadByte is 0x0A or 0xFF
            ? Success($"{engine} protocol reachability succeeded through the SSH tunnel.")
            : Failure(
                RemoteErrorCode.ParseFailed,
                $"The tunneled endpoint did not return a valid {engine} handshake.");
    }

    private static async Task<DatabaseEngineProbeResult> ProbeRedisAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(RedisPing, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var response = new byte[1];
        await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
        return response[0] is (byte)'+' or (byte)'-'
            ? Success("Redis protocol reachability succeeded through the SSH tunnel.")
            : Failure(
                RemoteErrorCode.ParseFailed,
                "The tunneled endpoint did not return a valid Redis PING response.");
    }

    private static async Task<DatabaseEngineProbeResult> ProbeSqlServerAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(SqlServerPreLogin, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var packetLength = (header[2] << 8) | header[3];
        return header[0] == 0x04 && packetLength >= 8
            ? Success("Microsoft SQL Server TDS pre-login reachability succeeded through the SSH tunnel.")
            : Failure(
                RemoteErrorCode.ParseFailed,
                "The tunneled endpoint did not return a valid SQL Server TDS pre-login response.");
    }

    private static async Task<DatabaseEngineProbeResult> ProbeMongoDbAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(MongoDbHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var header = new byte[16];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var messageLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var responseTo = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        var operationCode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        return messageLength >= 21 && responseTo == 1 && operationCode == 2013
            ? Success("MongoDB OP_MSG hello reachability succeeded through the SSH tunnel.")
            : Failure(
                RemoteErrorCode.ParseFailed,
                "The tunneled endpoint did not return a valid MongoDB OP_MSG response.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Database protocol endpoint closed the connection unexpectedly.");
            }

            offset += read;
        }
    }

    private static DatabaseEngineProbeResult Success(string message) => new(true, message, null);

    private static DatabaseEngineProbeResult Failure(RemoteErrorCode code, string message) =>
        new(false, message, new RemoteError(code, message));
}