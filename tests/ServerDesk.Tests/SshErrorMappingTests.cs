using System.Net.Sockets;
using Renci.SshNet.Common;
using ServerDesk.Domain.Errors;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshErrorMappingTests
{
    [Fact]
    public void AuthenticationFailureMapsToTypedError()
    {
        var error = SshRemoteErrorMapper.Map(
            new SshAuthenticationException("Permission denied."),
            lease: null,
            timedOut: false,
            callerCancelled: false);

        Assert.Equal(RemoteErrorCode.AuthenticationFailed, error.Code);
        Assert.DoesNotContain("Permission denied", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SocketFailureMapsToConnectionFailure()
    {
        var error = SshRemoteErrorMapper.Map(
            new SocketException((int)SocketError.ConnectionRefused),
            lease: null,
            timedOut: false,
            callerCancelled: false);

        Assert.Equal(RemoteErrorCode.ConnectionFailed, error.Code);
    }

    [Fact]
    public void CallerCancellationMapsToOperationCancelled()
    {
        var error = SshRemoteErrorMapper.Map(
            new OperationCanceledException(),
            lease: null,
            timedOut: false,
            callerCancelled: true);

        Assert.Equal(RemoteErrorCode.OperationCancelled, error.Code);
    }

    [Fact]
    public void ConnectTimeoutMapsToConnectionFailureWithoutPretendingAuthenticationFailed()
    {
        var error = SshRemoteErrorMapper.Map(
            new OperationCanceledException(),
            lease: null,
            timedOut: true,
            callerCancelled: false);

        Assert.Equal(RemoteErrorCode.ConnectionFailed, error.Code);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
