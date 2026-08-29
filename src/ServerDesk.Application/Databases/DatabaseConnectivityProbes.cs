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
