using System.Net;
using System.Net.Sockets;
using System.Text;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Errors;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseConnectivityProbeTests
{
    [Fact]
    public async Task PostgreSqlProbeRequiresSslNegotiationResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = StartListener(out var port);
        var serverTask = ServePostgreSqlAsync(listener, cancellationToken);
        var probe = new DatabaseEngineConnectivityProbe();

        var result = await probe.ProbeAsync(
            DatabaseEngineKind.PostgreSql,
            IPAddress.Loopback,
            port,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        await serverTask;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.Contains("PostgreSQL", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseEngineKind.MySql)]
    [InlineData(DatabaseEngineKind.MariaDb)]
    public async Task MySqlFamilyProbeRequiresServerHandshake(DatabaseEngineKind engine)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = StartListener(out var port);
        var serverTask = ServeMySqlAsync(listener, cancellationToken);
        var probe = new DatabaseEngineConnectivityProbe();

        var result = await probe.ProbeAsync(
            engine,
            IPAddress.Loopback,
            port,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        await serverTask;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.Contains(engine.ToString(), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedisNoAuthResponseStillProvesProtocolReachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = StartListener(out var port);
        var serverTask = ServeRedisNoAuthAsync(listener, cancellationToken);
        var probe = new DatabaseEngineConnectivityProbe();

        var result = await probe.ProbeAsync(
            DatabaseEngineKind.Redis,
            IPAddress.Loopback,
            port,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        await serverTask;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.Contains("Redis", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedProtocolResponseFailsInsteadOfReportingTcpOnlySuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = StartListener(out var port);
        var serverTask = ServeBytesAsync(listener, Encoding.ASCII.GetBytes("H"), cancellationToken);
        var probe = new DatabaseEngineConnectivityProbe();

        var result = await probe.ProbeAsync(
            DatabaseEngineKind.PostgreSql,
            IPAddress.Loopback,
            port,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        await serverTask;

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.ParseFailed, result.Error!.Code);
    }

    [Fact]
    public async Task NonLoopbackProbeFailsBeforeTcpConnection()
    {
        var probe = new DatabaseEngineConnectivityProbe();

        var result = await probe.ProbeAsync(
            DatabaseEngineKind.Redis,
            IPAddress.Parse("0.0.0.0"),
            6379,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error!.Code);
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task ServePostgreSqlAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var request = new byte[8];
        await ReadExactlyAsync(stream, request, cancellationToken);
        Assert.Equal(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, request);
        await stream.WriteAsync(new byte[] { (byte)'N' }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ServeMySqlAsync(TcpListener listener, CancellationToken cancellationToken) =>
        await ServeBytesAsync(listener, new byte[] { 1, 0, 0, 0, 0x0A }, cancellationToken);

    private static async Task ServeRedisNoAuthAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var request = new byte[14];
        await ReadExactlyAsync(stream, request, cancellationToken);
        Assert.Equal("*1\r\n$4\r\nPING\r\n", Encoding.ASCII.GetString(request));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("-NOAUTH Authentication required.\r\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ServeBytesAsync(
        TcpListener listener,
        byte[] response,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
